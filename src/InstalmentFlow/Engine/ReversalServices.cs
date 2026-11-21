using System.Collections.Immutable;
using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Engine;

/// <summary>
/// Applies a payment reversal to the lot that holds the payment's funds (ADR-005): first the lot's remaining funds,
/// then its allocations most recent first, and whatever had already been refunded becomes a reversal receivable.
/// Posts the journal lines into the caller's transaction.
/// </summary>
internal static class Reversals
{
    public sealed record Applied(
        PaymentReversalRecord Record,
        CustomerAccount? Account,
        UnidentifiedFunds? Pool,
        ImmutableArray<AllocationReversalRecord> Unwinds,
        Allocator.Held? Reallocation,
        ImmutableArray<DomainEvent> Events,
        bool FullyReversed);

    /// <summary>Reverses funds held by a customer; allocations are unwound in reverse recording order.</summary>
    public static async Task<Applied> OnAccountAsync(
        TenantContext context,
        TenantSnapshot tenant,
        CustomerAccount account,
        PaymentRecord payment,
        Money amount,
        ReversalId id,
        ExternalReference? reference,
        string reason,
        JournalTxId journalId,
        JournalBuilder journal,
        CancellationToken ct)
    {
        var now = tenant.Clock.UtcNow;
        var lot = account.GetLot(payment.Id);
        RequireReversible(lot, amount, payment);

        var fromFunds = Money.Min(amount, lot.Remaining);
        var unwinds = ImmutableArray<AllocationReversalRecord>.Empty;
        if (amount > fromFunds)
        {
            var allocations = await context.Store.ListAccountAllocationsAsync(context.TenantId, account.Id, ct).ConfigureAwait(false);
            var reversals = await context.Store.ListAccountAllocationReversalsAsync(context.TenantId, account.Id, ct).ConfigureAwait(false);
            var lifo = Corrections.Active(allocations.Where(a => a.PaymentId == payment.Id), reversals.Where(r => r.PaymentId == payment.Id));
            lifo.Reverse();
            (account, unwinds) = Corrections.Unwind(account, lifo, amount - fromFunds, UnwindCause.PaymentReversal, id.Value, journalId, now, context.Time);
            foreach (var unwind in unwinds)
            {
                journal.Debit(LedgerAccounts.Receivable(unwind.ObligationId, unwind.Component), unwind.Amount, LedgerSourceType.AllocationReversal, unwind.Id);
                journal.Credit(LedgerAccounts.Unapplied(account.Id, unwind.Amount.Currency), unwind.Amount, LedgerSourceType.AllocationReversal, unwind.Id);
            }
        }

        var unwound = Money.Sum(amount.Currency, unwinds.Select(u => u.Amount));
        var taken = fromFunds + unwound;
        var deficit = amount - taken;
        lot = account.GetLot(payment.Id);
        lot = lot with { Remaining = lot.Remaining - taken, Reversed = lot.Reversed + taken, Deficit = lot.Deficit + deficit };
        account = account.ReplaceLot(lot);

        if (taken.IsPositive)
        {
            journal.Debit(LedgerAccounts.Unapplied(account.Id, amount.Currency), taken, LedgerSourceType.PaymentReversal, id.Value);
        }

        if (deficit.IsPositive)
        {
            journal.Debit(LedgerAccounts.ReversalReceivable(account.Id, amount.Currency), deficit, LedgerSourceType.PaymentReversal, id.Value);
        }

        journal.Credit(LedgerAccounts.Clearing(payment.Source.Source), amount, LedgerSourceType.PaymentReversal, id.Value);

        // Re-opened instalments may be paid from the customer's other unapplied funds (doc 02 §4).
        Allocator.Held? reallocation = null;
        if (!unwinds.IsEmpty && tenant.Policy.ReallocateAfterReversal)
        {
            reallocation = await Allocator.AllocateHeldAsync(context, tenant, account, null, null, false, null, null, journalId, journal, ct).ConfigureAwait(false);
            account = reallocation.Account;
        }

        var record = new PaymentReversalRecord(id, payment.Id, account.Id, null, reference, amount, fromFunds, unwound, deficit, reason, journalId, now);
        var events = Events(context, record, unwinds).Concat(reallocation?.Events ?? []).ToImmutableArray();
        return new Applied(record, account, null, unwinds, reallocation, events, !account.GetLot(payment.Id).Reversible.IsPositive);
    }

    /// <summary>Reverses tenant unidentified funds. They have no allocations; any refunded part becomes a receivable.</summary>
    public static Applied OnPool(
        TenantContext context,
        TenantSnapshot tenant,
        UnidentifiedFunds pool,
        PaymentRecord payment,
        Money amount,
        ReversalId id,
        ExternalReference? reference,
        string reason,
        JournalTxId journalId,
        JournalBuilder journal)
    {
        var lot = pool.GetLot(payment.Id);
        RequireReversible(lot, amount, payment);

        var taken = Money.Min(amount, lot.Remaining);
        var deficit = amount - taken;
        lot = lot with { Remaining = lot.Remaining - taken, Reversed = lot.Reversed + taken, Deficit = lot.Deficit + deficit };
        if (taken.IsPositive)
        {
            journal.Debit(LedgerAccounts.Unidentified(pool.Source), taken, LedgerSourceType.PaymentReversal, id.Value);
        }

        if (deficit.IsPositive)
        {
            journal.Debit(LedgerAccounts.ReversalReceivable(pool.Source), deficit, LedgerSourceType.PaymentReversal, id.Value);
        }

        journal.Credit(LedgerAccounts.Clearing(payment.Source.Source), amount, LedgerSourceType.PaymentReversal, id.Value);

        var record = new PaymentReversalRecord(id, payment.Id, null, pool.Source, reference, amount, taken, Money.Zero(amount.Currency), deficit, reason, journalId, tenant.Clock.UtcNow);
        return new Applied(record, null, pool.ReplaceLot(lot), [], null, [.. Events(context, record, [])], !lot.Reversible.IsPositive);
    }

    public static ReversalResult ToResult(Applied applied, ReversalOutcome? outcome = null) =>
        new(
            outcome ?? (applied.FullyReversed ? ReversalOutcome.Reversed : ReversalOutcome.PartiallyReversed),
            applied.Record.Id,
            applied.Record.PaymentId,
            applied.Record.AccountId,
            applied.Record.Amount,
            applied.Record.FromFunds,
            applied.Record.Unwound,
            applied.Record.Deficit,
            [.. applied.Unwinds.Select(Corrections.ToView)],
            [.. (applied.Reallocation?.Allocations ?? []).Select(Allocator.ToView)],
            applied.Account?.Unapplied(applied.Record.Amount.Currency));

    private static void RequireReversible(FundsLot lot, Money amount, PaymentRecord payment)
    {
        if (amount.Currency != lot.Original.Currency)
        {
            throw new CurrencyMismatchException(lot.Original.Currency, amount.Currency);
        }

        if (!amount.IsPositive)
        {
            throw new InvalidAmountException($"Reversals must be positive; got {amount}.");
        }

        if (amount > lot.Reversible)
        {
            throw new RuleViolationException(
                ErrorCodes.ReversalExceedsPayment,
                $"Payment {payment.Source} of {payment.Amount} has {lot.Reversible} left to reverse; {amount} was requested (I-8).");
        }
    }

    private static IEnumerable<DomainEvent> Events(TenantContext context, PaymentReversalRecord record, ImmutableArray<AllocationReversalRecord> unwinds)
    {
        yield return DomainEvent.Create(context.Time, "PaymentReversed", context.TenantId, record.AccountId,
            ("reversalId", record.Id.ToString()), ("paymentId", record.PaymentId.ToString()), ("amount", record.Amount.ToString()));
        foreach (var byObligation in unwinds.GroupBy(u => u.ObligationId))
        {
            yield return DomainEvent.Create(context.Time, "AllocationReversed", context.TenantId, record.AccountId,
                ("obligationId", byObligation.Key.ToString()), ("paymentId", record.PaymentId.ToString()),
                ("amount", Money.Sum(record.Amount.Currency, byObligation.Select(u => u.Amount)).ToString()));
        }

        if (record.Deficit.IsPositive)
        {
            yield return DomainEvent.Create(context.Time, "ReversalDeficitRecorded", context.TenantId, record.AccountId,
                ("reversalId", record.Id.ToString()), ("paymentId", record.PaymentId.ToString()), ("deficit", record.Deficit.ToString()));
        }
    }
}

internal sealed partial class PaymentsService
{
    public Task<ReversalResult> ReverseAsync(ReversePaymentRequest request, CancellationToken cancellationToken = default)
    {
        Validate.Request(request);
        var paymentRef = Validate.Required(request.Payment, nameof(request.Payment));
        var reason = Validate.Text(request.Reason, nameof(request.Reason));

        return _runner.RunAsync(
            request,
            "ReversePayment",
            (_, _) => Task.FromResult(new Fingerprint("ReversePayment")
                .Add("payment", paymentRef.ToString())
                .Add("amount", request.Amount)
                .Add("reference", request.ReversalReference?.ToString())
                .Add("reason", reason)),
            (tenant, ct) => PlanReversalAsync(tenant, request, paymentRef, reason, ct),
            result => result,
            cancellationToken);
    }

    private async Task<Planned<ReversalResult>> PlanReversalAsync(TenantSnapshot tenant, ReversePaymentRequest request, PaymentRef paymentRef, string reason, CancellationToken ct)
    {
        var commit = CommitFor();
        var payment = await PaymentLookup.FindAsync(context, paymentRef, ct).ConfigureAwait(false);

        if (request.ReversalReference is { } reference &&
            await context.Store.FindReversalAsync(context.TenantId, reference, ct).ConfigureAwait(false) is { } existing)
        {
            return new Planned<ReversalResult>(commit, Duplicate(existing, paymentRef, payment, request, tenant));
        }

        var id = ReversalId.New(context.Time);
        if (payment is null)
        {
            if (paymentRef.Source is not { } source)
            {
                throw new NotFoundException("Payment", paymentRef.ToString());
            }

            // Reversal before payment: park it; recording the payment applies it.
            var pending = new PendingReversal(id, source, request.ReversalReference, request.Amount, reason, tenant.Clock.UtcNow);
            commit = commit with
            {
                NewPendingReversals = [pending],
                Events = [DomainEvent.Create(context.Time, "PaymentReversalParked", context.TenantId, null, ("reversalId", id.ToString()), ("source", source.ToString()))],
            };
            var zero = Money.Zero(request.Amount?.Currency ?? tenant.Tenant.BaseCurrency);
            return new Planned<ReversalResult>(commit, new ReversalResult(ReversalOutcome.Parked, id, null, null, request.Amount ?? zero, zero, zero, zero, [], [], null));
        }

        var journalId = JournalTxId.New(context.Time);
        var journal = new JournalBuilder(JournalKind.PaymentReversed);
        var holding = await PaymentLookup.HoldingAsync(context, payment, ct).ConfigureAwait(false);
        Reversals.Applied applied;
        if (holding.AccountId is { } holder)
        {
            var account = await context.LoadAccountAsync(holder, ct).ConfigureAwait(false);
            var amount = request.Amount ?? account.GetLot(payment.Id).Reversible;
            applied = await Reversals.OnAccountAsync(context, tenant, account, payment, amount, id, request.ReversalReference, reason, journalId, journal, ct).ConfigureAwait(false);
            commit = commit with { Accounts = [applied.Account!] };
        }
        else
        {
            var pool = await context.Store.LoadUnidentifiedAsync(context.TenantId, payment.Source.Source, ct).ConfigureAwait(false);
            var amount = request.Amount ?? pool.GetLot(payment.Id).Reversible;
            applied = Reversals.OnPool(context, tenant, pool, payment, amount, id, request.ReversalReference, reason, journalId, journal);
            commit = commit with { Pools = [applied.Pool!] };
        }

        commit = commit with
        {
            Journal = journal.Build(context.TenantId, holding.AccountId, request.IdempotencyKey, tenant.Clock.Today, tenant.Clock.UtcNow, context.Time, journalId),
            PaymentReversals = [applied.Record],
            AllocationReversals = applied.Unwinds,
            Allocations = applied.Reallocation?.Allocations ?? [],
            Events = applied.Events,
        };
        return new Planned<ReversalResult>(commit, Reversals.ToResult(applied));
    }

    private static ReversalResult Duplicate(ReversalLookup existing, PaymentRef paymentRef, PaymentRecord? payment, ReversePaymentRequest request, TenantSnapshot tenant)
    {
        if (existing.Recorded is { } recorded)
        {
            if (payment?.Id != recorded.PaymentId || (request.Amount is { } amount && amount != recorded.Amount))
            {
                throw Conflict(request.ReversalReference!);
            }

            return new ReversalResult(ReversalOutcome.Duplicate, recorded.Id, recorded.PaymentId, recorded.AccountId, recorded.Amount,
                recorded.FromFunds, recorded.Unwound, recorded.Deficit, [], [], null);
        }

        var pending = existing.Pending!;
        if (paymentRef.Source != pending.PaymentSource || request.Amount != pending.Amount)
        {
            throw Conflict(request.ReversalReference!);
        }

        var zero = Money.Zero(pending.Amount?.Currency ?? tenant.Tenant.BaseCurrency);
        return new ReversalResult(ReversalOutcome.Duplicate, pending.Id, null, null, pending.Amount ?? zero, zero, zero, zero, [], [], null);
    }

    private static RuleViolationException Conflict(ExternalReference reference) =>
        new(ErrorCodes.ReversalConflict, $"Reversal {reference} was already recorded with different details.");
}
