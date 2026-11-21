using System.Collections.Immutable;
using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Engine;

/// <summary>Runs the allocation engine for one lot and posts its journal lines (Dr unapplied, Cr receivable).</summary>
internal static class Allocator
{
    public static LotAllocation Run(
        TenantContext context,
        TenantSnapshot tenant,
        CustomerAccount account,
        PaymentId lot,
        IReadOnlyCollection<ObligationId> eligible,
        StrategyRef? strategyOverride,
        JournalTxId journalTxId,
        JournalBuilder journal,
        FutureInstalmentPolicy? futureOverride = null)
    {
        var strategy = context.Services.Strategy(strategyOverride ?? tenant.Policy.Allocation);
        var policy = futureOverride is { } future ? tenant.Policy with { FutureInstalments = future } : tenant.Policy;
        var result = AllocationEngine.Allocate(
            account, lot, eligible, strategy, policy, tenant.Tenant.Current.Version, tenant.Clock.Today, journalTxId, tenant.Clock.UtcNow, context.Time);

        foreach (var allocation in result.Allocations)
        {
            journal.Debit(LedgerAccounts.Unapplied(allocation.AccountId, allocation.Amount.Currency), allocation.Amount, LedgerSourceType.Allocation, allocation.Id.Value);
            journal.Credit(LedgerAccounts.Receivable(allocation.ObligationId, allocation.Component), allocation.Amount, LedgerSourceType.Allocation, allocation.Id.Value);
        }

        return result;
    }

    /// <summary>Which of a customer's obligations a lot may be applied to without an explicit instruction (ADR-009).</summary>
    public static ImmutableArray<ObligationId> DefaultEligible(CustomerAccount account, FundsLot lot, TenantPolicy policy, bool allowAcrossObligations)
    {
        var payable = account.Obligations
            .Where(o => ObligationFacts.From(o).IsPayableIn(lot.Remaining.Currency))
            .Select(o => o.Id)
            .ToImmutableArray();

        if (lot.Directive is { } directive)
        {
            return payable.Contains(directive) ? [directive] : [];
        }

        return payable.Length switch
        {
            0 => [],
            1 => policy.SoleObligation == SoleObligationPolicy.AutoAllocate || allowAcrossObligations ? payable : [],
            _ => policy.MultipleObligations == MultipleObligationsPolicy.AllocateByStrategy || allowAcrossObligations ? payable : [],
        };
    }

    public static IEnumerable<DomainEvent> Events(TenantContext context, LotAllocation result)
    {
        foreach (var byObligation in result.Allocations.GroupBy(a => a.ObligationId))
        {
            yield return DomainEvent.Create(context.Time, "FundsAllocated", context.TenantId, byObligation.First().AccountId,
                ("obligationId", byObligation.Key.ToString()),
                ("paymentId", byObligation.First().PaymentId.ToString()),
                ("amount", Money.Sum(byObligation.First().Amount.Currency, byObligation.Select(a => a.Amount)).ToString()));
        }

        foreach (var instalment in result.InstalmentsSettled)
        {
            yield return DomainEvent.Create(context.Time, "InstalmentSettled", context.TenantId, result.Account.Id,
                ("obligationId", instalment.ObligationId.ToString()), ("sequence", instalment.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        foreach (var obligation in result.ObligationsSettled)
        {
            yield return DomainEvent.Create(context.Time, "ObligationSettled", context.TenantId, result.Account.Id, ("obligationId", obligation.ToString()));
        }
    }

    /// <summary>What allocating a customer's held funds produced.</summary>
    public sealed record Held(
        CustomerAccount Account,
        ImmutableArray<AllocationRecord> Allocations,
        ImmutableArray<InstalmentRef> InstalmentsSettled,
        ImmutableArray<ObligationId> ObligationsSettled,
        ImmutableArray<DomainEvent> Events);

    /// <summary>
    /// Allocates a customer's unapplied funds, oldest lot first (by value time, then payment ID). Lots with an open
    /// payment exception are skipped unless <paramref name="onlyPayment"/> names one explicitly (the caller checks it).
    /// </summary>
    public static async Task<Held> AllocateHeldAsync(
        TenantContext context,
        TenantSnapshot tenant,
        CustomerAccount account,
        ObligationId? instructed,
        PaymentId? onlyPayment,
        bool allowAcrossObligations,
        StrategyRef? strategy,
        FutureInstalmentPolicy? future,
        JournalTxId journalId,
        JournalBuilder journal,
        CancellationToken ct)
    {
        var allocations = ImmutableArray.CreateBuilder<AllocationRecord>();
        var settled = ImmutableArray.CreateBuilder<InstalmentRef>();
        var obligationsSettled = ImmutableArray.CreateBuilder<ObligationId>();
        var events = ImmutableArray.CreateBuilder<DomainEvent>();

        var lots = account.Lots
            .Where(l => l.Remaining.IsPositive && (onlyPayment is null || l.PaymentId == onlyPayment))
            .OrderBy(l => l.ValueAt)
            .ThenBy(l => l.PaymentId.Value)
            .ToList();

        foreach (var lot in lots)
        {
            if (onlyPayment is null && await HasOpenExceptionAsync(context, lot.PaymentId, ct).ConfigureAwait(false))
            {
                continue;
            }

            var current = account.GetLot(lot.PaymentId);
            var eligible = instructed is { } id
                ? (account.Obligations.Any(o => o.Id == id && ObligationFacts.From(o).IsPayableIn(current.Remaining.Currency)) ? [id] : ImmutableArray<ObligationId>.Empty)
                : DefaultEligible(account, current, tenant.Policy, allowAcrossObligations);

            var result = Run(context, tenant, account, current.PaymentId, eligible, strategy, journalId, journal, future);
            account = result.Account;
            allocations.AddRange(result.Allocations);
            settled.AddRange(result.InstalmentsSettled);
            obligationsSettled.AddRange(result.ObligationsSettled);
            events.AddRange(Events(context, result));
        }

        return new Held(account, allocations.ToImmutable(), settled.ToImmutable(), obligationsSettled.ToImmutable(), events.ToImmutable());
    }

    public static async Task<bool> HasOpenExceptionAsync(TenantContext context, PaymentId paymentId, CancellationToken ct)
    {
        var payment = await context.Store.GetPaymentAsync(context.TenantId, paymentId, ct).ConfigureAwait(false);
        return payment is not null && await HasOpenExceptionAsync(context, payment, ct).ConfigureAwait(false);
    }

    public static async Task<bool> HasOpenExceptionAsync(TenantContext context, PaymentRecord payment, CancellationToken ct) =>
        (await context.Store.ListPaymentExceptionsAsync(context.TenantId, payment.Id, ct).ConfigureAwait(false)).Any(e => e.Status == PaymentExceptionStatus.Open);

    public static AllocationLineView ToView(AllocationRecord record) =>
        new(record.PaymentId, record.ObligationId, record.InstalmentSequence, record.DueDate, record.Component, record.Amount, record.Strategy);
}

internal sealed class AllocationsService(TenantContext context) : IAllocations
{
    private readonly CommandRunner _runner = new(context);

    public Task<AllocationResult> AllocateAsync(AllocateFundsRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var accountRef = Validate.Required(request.Account, nameof(request.Account));

        return _runner.RunAsync(
            request,
            "AllocateFunds",
            (_, _) => Task.FromResult(new Fingerprint("AllocateFunds")
                .Add("account", accountRef.ToString())
                .Add("obligation", request.Obligation?.ToString())
                .Add("payment", request.Payment?.ToString())
                .Add("across", request.AllowAcrossObligations ? 1 : 0)
                .Add("strategy", request.Strategy?.ToString())
                .Add("future", request.FutureInstalments is { } future ? (long)future : -1)),
            (tenant, ct) => PlanAsync(tenant, request, accountRef, ct),
            result => result,
            cancellationToken);
    }

    private async Task<Planned<AllocationResult>> PlanAsync(TenantSnapshot tenant, AllocateFundsRequest request, AccountRef accountRef, CancellationToken ct)
    {
        if (request.Strategy is { } strategy)
        {
            _ = context.Services.Strategy(strategy);
        }

        var accountId = await context.ResolveAsync(accountRef, ct).ConfigureAwait(false);
        var account = await context.LoadAccountAsync(accountId, ct).ConfigureAwait(false);

        ObligationId? instructed = null;
        if (request.Obligation is { } obligationRef)
        {
            var location = await context.ResolveAsync(obligationRef, tenant, ct).ConfigureAwait(false);
            if (location.AccountId != account.Id)
            {
                throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Obligation {obligationRef} does not belong to the customer.");
            }

            instructed = location.ObligationId;
        }

        PaymentId? onlyPayment = null;
        if (request.Payment is { } paymentRef)
        {
            var payment = await PaymentLookup.FindAsync(context, paymentRef, ct).ConfigureAwait(false) ?? throw new NotFoundException("Payment", paymentRef.ToString());
            if (account.Lots.All(l => l.PaymentId != payment.Id))
            {
                throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Payment {paymentRef} is not held by the customer.");
            }

            if (await Allocator.HasOpenExceptionAsync(context, payment, ct).ConfigureAwait(false))
            {
                throw new RuleViolationException(ErrorCodes.ExceptionNotResolvable, $"Payment {paymentRef} has an open exception; resolve it through the exception queue (I-20).");
            }

            onlyPayment = payment.Id;
        }

        var journalId = JournalTxId.New(context.Time);
        var journal = new JournalBuilder(JournalKind.FundsAllocated);
        var held = await Allocator.AllocateHeldAsync(
            context, tenant, account, instructed, onlyPayment, request.AllowAcrossObligations, request.Strategy, request.FutureInstalments, journalId, journal, ct).ConfigureAwait(false);
        account = held.Account;
        var allocations = held.Allocations;

        var commit = new Commit { Tenant = context.TenantId };
        if (allocations.Length > 0)
        {
            commit = commit with
            {
                Accounts = [account],
                Journal = journal.Build(context.TenantId, account.Id, request.IdempotencyKey, tenant.Clock.Today, tenant.Clock.UtcNow, context.Time, journalId),
                Allocations = allocations,
                Events = held.Events,
            };
        }

        var allocationResult = new AllocationResult(
            account.Id,
            [.. allocations.GroupBy(a => a.Amount.Currency).OrderBy(g => g.Key.Code, StringComparer.Ordinal).Select(g => Money.Sum(g.Key, g.Select(a => a.Amount)))],
            account.UnappliedByCurrency(),
            [.. allocations.Select(Allocator.ToView)],
            held.InstalmentsSettled,
            held.ObligationsSettled);
        return new Planned<AllocationResult>(commit, allocationResult);
    }
}
