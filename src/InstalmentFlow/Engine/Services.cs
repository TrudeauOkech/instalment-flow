using System.Collections.Immutable;
using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Engine;

internal sealed class AccountsService(TenantContext context) : IAccounts
{
    private readonly CommandRunner _runner = new(context);

    public Task<AccountOpened> OpenAsync(OpenAccountRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var externalId = Validate.ExternalId(request.ExternalId, nameof(request.ExternalId));

        return _runner.RunAsync(
            request,
            "OpenAccount",
            (_, _) => Task.FromResult(new Fingerprint("OpenAccount").Add("externalId", externalId)),
            async (tenant, ct) =>
            {
                if (await context.Store.FindAccountAsync(context.TenantId, externalId, ct).ConfigureAwait(false) is not null)
                {
                    throw new RuleViolationException(ErrorCodes.DuplicateExternalId, $"Account external ID '{externalId}' is already in use in this tenant.");
                }

                var account = CustomerAccount.Open(context.TenantId, AccountId.New(context.Time), externalId, tenant.Clock.UtcNow);
                var commit = new Commit
                {
                    Tenant = context.TenantId,
                    Accounts = [account],
                    Events = [DomainEvent.Create(context.Time, "AccountOpened", context.TenantId, account.Id, ("externalId", externalId))],
                };
                return new Planned<AccountOpened>(commit, new AccountOpened(account.Id, externalId));
            },
            result => result,
            cancellationToken);
    }
}

internal sealed class ObligationsService(TenantContext context) : IObligations
{
    private readonly CommandRunner _runner = new(context);

    public Task<ObligationOpened> OpenAsync(OpenObligationRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var externalId = Validate.ExternalId(request.ExternalId, nameof(request.ExternalId));
        var plan = Validate.Required(request.Plan, nameof(request.Plan));
        var accountRef = Validate.Required(request.Account, nameof(request.Account));
        var originated = ValidateComponents(Validate.Required(request.Components, nameof(request.Components)));
        var currency = originated[0].Amount.Currency;

        return _runner.RunAsync(
            request,
            "OpenObligation",
            (tenant, _) =>
            {
                var schedule = ScheduleBuilder.Build(1, plan, originated, currency, tenant.Policy.DefaultGrace, tenant.Calendar, DateTimeOffset.UnixEpoch);
                var fingerprint = new Fingerprint("OpenObligation")
                    .Add("account", accountRef.ToString())
                    .Add("externalId", externalId)
                    .Add("reference", request.PaymentReference)
                    .Add("description", request.Description);
                foreach (var component in originated)
                {
                    fingerprint.Add($"component:{component.Key}", component.Amount);
                }

                return Task.FromResult(AddSchedule(fingerprint, schedule));
            },
            async (tenant, ct) =>
            {
                if (!tenant.Policy.PaymentReference.TryNormalise(request.PaymentReference, out var reference))
                {
                    throw new RuleViolationException(
                        ErrorCodes.InvalidPaymentReference,
                        $"Payment reference '{request.PaymentReference}' does not match the tenant's format {tenant.Policy.PaymentReference}.");
                }

                var accountId = await context.ResolveAsync(accountRef, ct).ConfigureAwait(false);
                var account = await context.LoadAccountAsync(accountId, ct).ConfigureAwait(false);

                // Friendly early checks; the store re-checks uniqueness atomically on commit.
                if (await context.Store.FindObligationAsync(context.TenantId, new ObligationLookup(null, externalId, null), ct).ConfigureAwait(false) is not null)
                {
                    throw new RuleViolationException(ErrorCodes.DuplicateExternalId, $"Obligation external ID '{externalId}' is already in use in this tenant.");
                }

                if (await context.Store.FindObligationAsync(context.TenantId, new ObligationLookup(null, null, reference), ct).ConfigureAwait(false) is not null)
                {
                    throw new RuleViolationException(ErrorCodes.DuplicatePaymentReference, $"Payment reference '{reference}' is already in use in this tenant.");
                }

                var now = tenant.Clock.UtcNow;
                var planVersion = ScheduleBuilder.Build(1, plan, originated, currency, tenant.Policy.DefaultGrace, tenant.Calendar, now);
                var obligation = new Obligation(
                    ObligationId.New(context.Time), account.Id, externalId, reference, currency, ObligationStatus.Active,
                    [.. originated], [planVersion], request.Description, now);

                var journal = new JournalBuilder(JournalKind.ObligationOpened);
                foreach (var component in originated)
                {
                    journal.Debit(LedgerAccounts.Receivable(obligation.Id, component.Key), component.Amount, LedgerSourceType.Obligation, obligation.Id.Value);
                    journal.Credit(LedgerAccounts.Origination(component.Key), component.Amount, LedgerSourceType.Obligation, obligation.Id.Value);
                }

                var commit = new Commit
                {
                    Tenant = context.TenantId,
                    Accounts = [account.AddObligation(obligation)],
                    Journal = journal.Build(context.TenantId, account.Id, request.IdempotencyKey, tenant.Clock.Today, now, context.Time),
                    Events = [DomainEvent.Create(context.Time, "ObligationOpened", context.TenantId, account.Id,
                        ("obligationId", obligation.Id.ToString()), ("paymentReference", reference))],
                };
                return new Planned<ObligationOpened>(commit, new ObligationOpened(ViewMapper.ToView(obligation, tenant.Clock.Today)));
            },
            result => result,
            cancellationToken);
    }

    internal static Fingerprint AddSchedule(Fingerprint fingerprint, PlanVersion schedule)
    {
        fingerprint.Add("grace", schedule.Grace.Days).Add("generator", schedule.Generator);
        foreach (var instalment in schedule.Instalments)
        {
            fingerprint.Add($"due:{instalment.Sequence}", instalment.DueDate);
            foreach (var component in instalment.Components)
            {
                fingerprint.Add($"amount:{instalment.Sequence}:{component.Key}", component.Due);
            }
        }

        return fingerprint;
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
    private readonly CommandRunner _runner = new(context);

    public Task<PlanSuperseded> SupersedeAsync(SupersedePlanRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var plan = Validate.Required(request.Plan, nameof(request.Plan));
        var obligationRef = Validate.Required(request.Obligation, nameof(request.Obligation));
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "A reason is required to supersede a plan.");
        }

        return _runner.RunAsync(
            request,
            "SupersedePlan",
            async (tenant, ct) =>
            {
                var (_, obligation) = await LoadAsync(tenant, obligationRef, ct).ConfigureAwait(false);
                var schedule = ScheduleBuilder.Build(0, plan, obligation.Originated, obligation.Currency, tenant.Policy.DefaultGrace, tenant.Calendar, DateTimeOffset.UnixEpoch);
                var fingerprint = new Fingerprint("SupersedePlan").Add("obligation", obligation.Id.ToString()).Add("reason", request.Reason);
                return ObligationsService.AddSchedule(fingerprint, schedule);
            },
            async (tenant, ct) =>
            {
                var (account, obligation) = await LoadAsync(tenant, obligationRef, ct).ConfigureAwait(false);
                var previous = obligation.ActivePlan
                    ?? throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Obligation {obligation.ExternalId} has no active plan.");
                var now = tenant.Clock.UtcNow;
                var replacement = ScheduleBuilder.Build(
                    obligation.Plans.Max(p => p.Version) + 1, plan, obligation.Originated, obligation.Currency, tenant.Policy.DefaultGrace, tenant.Calendar, now);
                var updated = obligation.SupersedePlan(replacement, now, request.Reason);

                var commit = new Commit
                {
                    Tenant = context.TenantId,
                    Accounts = [account.ReplaceObligation(updated)],
                    Events = [DomainEvent.Create(context.Time, "PlanSuperseded", context.TenantId, account.Id,
                        ("obligationId", obligation.Id.ToString()), ("previousVersion", previous.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)))],
                };
                return new Planned<PlanSuperseded>(commit, new PlanSuperseded(obligation.Id, previous.Version, replacement.Version));
            },
            result => result,
            cancellationToken);
    }

    private async Task<(CustomerAccount Account, Obligation Obligation)> LoadAsync(TenantSnapshot tenant, ObligationRef reference, CancellationToken ct)
    {
        var location = await context.ResolveAsync(reference, tenant, ct).ConfigureAwait(false);
        var account = await context.LoadAccountAsync(location.AccountId, ct).ConfigureAwait(false);
        return (account, account.GetObligation(location.ObligationId));
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
            loaded.UnappliedByCurrency(),
            [.. loaded.Obligations.OrderBy(o => o.OpenedAt).Select(o =>
                new ObligationSummary(o.Id, o.ExternalId, o.PaymentReference, o.Status, ViewMapper.Outstanding(o)))]);
    }

    public async Task<ObligationView> GetObligationAsync(ObligationRef obligation, DateOnly? asOfDate = null, CancellationToken cancellationToken = default)
    {
        var (tenant, found) = await LoadObligationAsync(obligation, cancellationToken).ConfigureAwait(false);
        return ViewMapper.ToView(found, asOfDate ?? tenant.Clock.Today);
    }

    public async Task<PaymentView> GetPaymentAsync(PaymentRef payment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payment);
        await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var record = await PaymentLookup.FindAsync(context, payment, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Payment", payment.ToString());
        var holding = await PaymentLookup.HoldingAsync(context, record, cancellationToken).ConfigureAwait(false);
        return new PaymentView(
            record.Id, record.Source, record.Amount, record.ValueAt, record.RecordedAt, record.TargetKind, record.RawReference,
            holding.AccountId, holding.Lot?.Directive ?? record.IdentifiedObligation, holding.Lot?.Remaining ?? Money.Zero(record.Amount.Currency),
            record.ExceptionId, record.Metadata);
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

/// <summary>Finds a payment and where its funds are held now.</summary>
internal static class PaymentLookup
{
    public sealed record Holding(AccountId? AccountId, FundsLot? Lot);

    public static async Task<PaymentRecord?> FindAsync(TenantContext context, PaymentRef reference, CancellationToken ct) =>
        reference.Id is { } id
            ? await context.Store.GetPaymentAsync(context.TenantId, id, ct).ConfigureAwait(false)
            : await context.Store.FindPaymentAsync(context.TenantId, reference.Source!, ct).ConfigureAwait(false);

    public static async Task<Holding> HoldingAsync(TenantContext context, PaymentRecord payment, CancellationToken ct)
    {
        var assignment = await context.Store.GetAssignmentAsync(context.TenantId, payment.Id, ct).ConfigureAwait(false);
        var accountId = assignment?.AccountId ?? payment.AccountId;
        if (accountId is { } holder)
        {
            var account = await context.LoadAccountAsync(holder, ct).ConfigureAwait(false);
            return new Holding(holder, account.Lots.SingleOrDefault(l => l.PaymentId == payment.Id));
        }

        var pool = await context.Store.LoadUnidentifiedAsync(context.TenantId, payment.Source.Source, ct).ConfigureAwait(false);
        return new Holding(null, pool.Lots.SingleOrDefault(l => l.PaymentId == payment.Id));
    }

    public static async Task<PaymentExceptionView> ToViewAsync(TenantContext context, PaymentExceptionRecord exception, CancellationToken ct)
    {
        var payment = await context.Store.GetPaymentAsync(context.TenantId, exception.PaymentId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Payment {exception.PaymentId} of exception {exception.Id} is missing.");
        var assignment = await context.Store.GetAssignmentAsync(context.TenantId, payment.Id, ct).ConfigureAwait(false);
        return ViewMapper.ToView(exception, payment, assignment?.AccountId ?? payment.AccountId);
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

    public static PaymentExceptionView ToView(PaymentExceptionRecord exception, PaymentRecord payment, AccountId? heldBy) =>
        new(
            exception.Id,
            exception.PaymentId,
            exception.Reason,
            exception.Detail,
            exception.RawReference,
            exception.Candidates,
            payment.Amount,
            heldBy,
            exception.OpenedAt,
            exception.Status,
            exception.Resolution is { } r ? new ResolutionView(r.Kind, r.ResolvedBy, r.Note, r.At) : null);

    /// <summary>
    /// Component balances. Until allocation lands (M4), paid and waived amounts are summed from the active plan;
    /// afterwards they come from the obligation's component projections.
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
