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
        JournalBuilder journal)
    {
        var strategy = context.Services.Strategy(strategyOverride ?? tenant.Policy.Allocation);
        var result = AllocationEngine.Allocate(
            account, lot, eligible, strategy, tenant.Policy, tenant.Tenant.Current.Version, tenant.Clock.Today, journalTxId, tenant.Clock.UtcNow, context.Time);

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
                .Add("strategy", request.Strategy?.ToString())),
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

            if (await HasOpenExceptionAsync(payment, ct).ConfigureAwait(false))
            {
                throw new RuleViolationException(ErrorCodes.ExceptionNotResolvable, $"Payment {paymentRef} has an open exception; resolve it through the exception queue (I-20).");
            }

            onlyPayment = payment.Id;
        }

        var journalId = JournalTxId.New(context.Time);
        var journal = new JournalBuilder(JournalKind.FundsAllocated);
        var allocations = ImmutableArray.CreateBuilder<AllocationRecord>();
        var settled = new List<InstalmentRef>();
        var obligationsSettled = new List<ObligationId>();
        var events = new List<DomainEvent>();

        var lots = account.Lots
            .Where(l => l.Remaining.IsPositive && (onlyPayment is null || l.PaymentId == onlyPayment))
            .OrderBy(l => l.ValueAt)
            .ThenBy(l => l.PaymentId.Value)
            .ToList();

        foreach (var lot in lots)
        {
            if (onlyPayment is null && await HasOpenExceptionAsync(lot.PaymentId, ct).ConfigureAwait(false))
            {
                continue;
            }

            var current = account.Lots.Single(l => l.PaymentId == lot.PaymentId);
            var eligible = instructed is { } id
                ? (account.Obligations.Any(o => o.Id == id && ObligationFacts.From(o).IsPayableIn(current.Remaining.Currency)) ? [id] : ImmutableArray<ObligationId>.Empty)
                : Allocator.DefaultEligible(account, current, tenant.Policy, request.AllowAcrossObligations);

            var result = Allocator.Run(context, tenant, account, current.PaymentId, eligible, request.Strategy, journalId, journal);
            account = result.Account;
            allocations.AddRange(result.Allocations);
            settled.AddRange(result.InstalmentsSettled);
            obligationsSettled.AddRange(result.ObligationsSettled);
            events.AddRange(Allocator.Events(context, result));
        }

        var commit = new Commit { Tenant = context.TenantId };
        if (allocations.Count > 0)
        {
            commit = commit with
            {
                Accounts = [account],
                Journal = journal.Build(context.TenantId, account.Id, request.IdempotencyKey, tenant.Clock.Today, tenant.Clock.UtcNow, context.Time, journalId),
                Allocations = allocations.ToImmutable(),
                Events = [.. events],
            };
        }

        var allocationResult = new AllocationResult(
            account.Id,
            [.. allocations.GroupBy(a => a.Amount.Currency).OrderBy(g => g.Key.Code, StringComparer.Ordinal).Select(g => Money.Sum(g.Key, g.Select(a => a.Amount)))],
            account.UnappliedByCurrency(),
            [.. allocations.Select(Allocator.ToView)],
            settled,
            obligationsSettled);
        return new Planned<AllocationResult>(commit, allocationResult);
    }

    private async Task<bool> HasOpenExceptionAsync(PaymentId paymentId, CancellationToken ct)
    {
        var payment = await context.Store.GetPaymentAsync(context.TenantId, paymentId, ct).ConfigureAwait(false);
        return payment is not null && await HasOpenExceptionAsync(payment, ct).ConfigureAwait(false);
    }

    private async Task<bool> HasOpenExceptionAsync(PaymentRecord payment, CancellationToken ct) =>
        payment.ExceptionId is { } exceptionId &&
        await context.Store.GetExceptionAsync(context.TenantId, exceptionId, ct).ConfigureAwait(false) is { Status: PaymentExceptionStatus.Open };
}
