using System.Collections.Immutable;
using InstalmentFlow.Domain;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Engine;

internal sealed class AccountsService(TenantContext context) : IAccounts
{
    public async Task<AccountOpened> OpenAsync(OpenAccountRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var externalId = Validate.ExternalId(request.ExternalId, nameof(request.ExternalId));
        var tenant = await context.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (await context.Accounts.FindAccountAsync(context.TenantId, externalId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new RuleViolationException(ErrorCodes.DuplicateExternalId, $"Account external ID '{externalId}' is already in use in this tenant.");
        }

        var account = CustomerAccount.Open(context.TenantId, AccountId.New(context.Services.TimeProvider), externalId, tenant.Clock.UtcNow);
        var saved = await context.Accounts.SaveAsync(account, cancellationToken).ConfigureAwait(false);
        return new AccountOpened(saved.Id, saved.ExternalId);
    }
}

internal sealed class ObligationsService(TenantContext context) : IObligations
{
    public async Task<ObligationOpened> OpenAsync(OpenObligationRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var externalId = Validate.ExternalId(request.ExternalId, nameof(request.ExternalId));
        var plan = Validate.Required(request.Plan, nameof(request.Plan));
        var components = Validate.Required(request.Components, nameof(request.Components));
        var tenant = await context.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (!tenant.Policy.PaymentReference.TryNormalise(request.PaymentReference, out var reference))
        {
            throw new RuleViolationException(
                ErrorCodes.InvalidPaymentReference,
                $"Payment reference '{request.PaymentReference}' does not match the tenant's format {tenant.Policy.PaymentReference}.");
        }

        var originated = ValidateComponents(components);
        var currency = originated[0].Amount.Currency;
        var accountId = await context.ResolveAsync(Validate.Required(request.Account, nameof(request.Account)), cancellationToken).ConfigureAwait(false);
        var account = await context.LoadAccountAsync(accountId, cancellationToken).ConfigureAwait(false);

        // Friendly early checks; the store re-checks uniqueness atomically on save.
        if (await context.Accounts.FindObligationAsync(context.TenantId, new ObligationLookup(null, externalId, null), cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new RuleViolationException(ErrorCodes.DuplicateExternalId, $"Obligation external ID '{externalId}' is already in use in this tenant.");
        }

        if (await context.Accounts.FindObligationAsync(context.TenantId, new ObligationLookup(null, null, reference), cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new RuleViolationException(ErrorCodes.DuplicatePaymentReference, $"Payment reference '{reference}' is already in use in this tenant.");
        }

        var now = tenant.Clock.UtcNow;
        var planVersion = ScheduleBuilder.Build(1, plan, originated, currency, tenant.Policy.DefaultGrace, tenant.Calendar, now);
        var obligation = new Obligation(
            ObligationId.New(context.Services.TimeProvider),
            account.Id,
            externalId,
            reference,
            currency,
            ObligationStatus.Active,
            [.. originated],
            [planVersion],
            request.Description,
            now);

        await context.Accounts.SaveAsync(account.AddObligation(obligation), cancellationToken).ConfigureAwait(false);
        return new ObligationOpened(ViewMapper.ToView(obligation, tenant.Clock.Today));
    }

    private static List<Component> ValidateComponents(IReadOnlyList<Component> components)
    {
        if (components.Count == 0 || components.Any(c => c is null))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "An obligation needs at least one component amount.");
        }

        if (components.Select(c => c.Key).Distinct().Count() != components.Count)
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "Each component may be listed once.");
        }

        var currency = components[0].Amount.Currency;
        foreach (var component in components)
        {
            if (component.Amount.Currency != currency)
            {
                throw new CurrencyMismatchException(currency, component.Amount.Currency);
            }
        }

        return [.. components.OrderBy(c => c.Key)];
    }
}

internal sealed class PlansService(TenantContext context) : IPlans
{
    public async Task<PlanSuperseded> SupersedeAsync(SupersedePlanRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var plan = Validate.Required(request.Plan, nameof(request.Plan));
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "A reason is required to supersede a plan.");
        }

        var tenant = await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var location = await context.ResolveAsync(Validate.Required(request.Obligation, nameof(request.Obligation)), tenant, cancellationToken).ConfigureAwait(false);
        var account = await context.LoadAccountAsync(location.AccountId, cancellationToken).ConfigureAwait(false);
        var obligation = account.GetObligation(location.ObligationId);
        var previous = obligation.ActivePlan
            ?? throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Obligation {obligation.ExternalId} has no active plan.");

        var now = tenant.Clock.UtcNow;
        var replacement = ScheduleBuilder.Build(
            obligation.Plans.Max(p => p.Version) + 1,
            plan,
            obligation.Originated,
            obligation.Currency,
            tenant.Policy.DefaultGrace,
            tenant.Calendar,
            now);

        var updated = obligation.SupersedePlan(replacement, now, request.Reason);
        await context.Accounts.SaveAsync(account.ReplaceObligation(updated), cancellationToken).ConfigureAwait(false);
        return new PlanSuperseded(obligation.Id, previous.Version, replacement.Version);
    }
}

internal sealed class QueriesService(TenantContext context) : IInstalmentQueries
{
    public async Task<AccountSummary> GetAccountAsync(AccountRef account, DateOnly? asOfDate = null, CancellationToken cancellationToken = default)
    {
        await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var id = await context.ResolveAsync(account, cancellationToken).ConfigureAwait(false);
        var loaded = await context.LoadAccountAsync(id, cancellationToken).ConfigureAwait(false);
        return new AccountSummary(
            loaded.Id,
            loaded.ExternalId,
            loaded.OpenedAt,
            [.. loaded.Obligations.OrderBy(o => o.OpenedAt).Select(o =>
                new ObligationSummary(o.Id, o.ExternalId, o.PaymentReference, o.Status, ViewMapper.Outstanding(o)))]);
    }

    public async Task<ObligationView> GetObligationAsync(ObligationRef obligation, DateOnly? asOfDate = null, CancellationToken cancellationToken = default)
    {
        var (tenant, found) = await LoadObligationAsync(obligation, cancellationToken).ConfigureAwait(false);
        return ViewMapper.ToView(found, asOfDate ?? tenant.Clock.Today);
    }

    public async Task<ScheduleView> GetScheduleAsync(ObligationRef obligation, DateOnly? asOfDate = null, CancellationToken cancellationToken = default)
    {
        var (tenant, found) = await LoadObligationAsync(obligation, cancellationToken).ConfigureAwait(false);
        var asOf = asOfDate ?? tenant.Clock.Today;
        return new ScheduleView(
            found.Id,
            found.ActivePlan?.Version,
            [.. found.Plans.OrderBy(p => p.Version).Select(p => new PlanVersionView(
                p.Version, p.Status, p.Grace, p.Generator, p.CreatedAt, p.EndedAt, p.EndReason,
                [.. p.Instalments.Select(i => ViewMapper.ToView(i, asOf, p.Grace))]))]);
    }

    private async Task<(TenantSnapshot Tenant, Obligation Obligation)> LoadObligationAsync(ObligationRef reference, CancellationToken cancellationToken)
    {
        var tenant = await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var location = await context.ResolveAsync(reference, tenant, cancellationToken).ConfigureAwait(false);
        var account = await context.LoadAccountAsync(location.AccountId, cancellationToken).ConfigureAwait(false);
        return (tenant, account.GetObligation(location.ObligationId));
    }
}

internal static class ViewMapper
{
    public static Money Outstanding(Obligation obligation) =>
        Money.Sum(obligation.Currency, Balances(obligation).Select(b => b.Outstanding));

    public static ObligationView ToView(Obligation obligation, DateOnly asOf)
    {
        var plan = obligation.ActivePlan;
        var instalments = plan?.Instalments ?? ImmutableArray<Instalment>.Empty;
        var balances = Balances(obligation);
        var arrears = Money.Sum(obligation.Currency, instalments.Where(i => i.DueDate < asOf).Select(i => i.Outstanding));

        return new ObligationView(
            obligation.Id,
            obligation.AccountId,
            obligation.ExternalId,
            obligation.PaymentReference,
            obligation.Currency,
            obligation.Status,
            obligation.Description,
            asOf,
            balances,
            Money.Sum(obligation.Currency, balances.Select(b => b.Outstanding)),
            arrears,
            instalments.Select(i => InstalmentEvaluator.DaysPastDue(i, asOf)).DefaultIfEmpty(0).Max(),
            InstalmentEvaluator.PaidThroughDate(instalments),
            plan?.Version,
            plan?.Grace,
            [.. instalments.Select(i => ToView(i, asOf, plan!.Grace))]);
    }

    public static InstalmentView ToView(Instalment instalment, DateOnly asOf, GracePeriod grace) =>
        new(
            instalment.Sequence,
            instalment.DueDate,
            instalment.Due,
            instalment.Allocated,
            instalment.Waived,
            instalment.Outstanding,
            InstalmentEvaluator.PaymentState(instalment),
            InstalmentEvaluator.Timing(instalment, asOf, grace),
            InstalmentEvaluator.DaysPastDue(instalment, asOf),
            [.. instalment.Components.Select(c => new InstalmentComponentView(c.Key, c.Due, c.Allocated, c.Waived, c.Outstanding))]);

    /// <summary>
    /// Component balances. Until the journal lands (M3/M4), paid and waived amounts are summed from the active
    /// plan; afterwards they come from the obligation's component projections.
    /// </summary>
    private static List<ComponentBalanceView> Balances(Obligation obligation)
    {
        var lines = obligation.ActivePlan?.Instalments.SelectMany(i => i.Components).ToList() ?? [];
        return [.. obligation.Originated.Select(c =>
        {
            var paid = Money.Sum(obligation.Currency, lines.Where(l => l.Key == c.Key).Select(l => l.Allocated));
            var waived = Money.Sum(obligation.Currency, lines.Where(l => l.Key == c.Key).Select(l => l.Waived));
            return new ComponentBalanceView(c.Key, c.Amount, paid, waived, c.Amount - paid - waived);
        })];
    }
}
