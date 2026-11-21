using System.Collections.Immutable;
using InstalmentFlow.Ledger;

namespace InstalmentFlow.Domain;

// Immutable correction facts (ADR-004). Projections on the aggregate (instalment components, funds lots) are folded
// from these; the ledger verifier recomputes the projections from them independently.

/// <summary>Why an allocation was unwound.</summary>
internal enum UnwindCause
{
    PaymentReversal = 1,
    ChargeReversal = 2,
}

/// <summary>Part or all of an allocation undone (ADR-005). The original allocation is never changed.</summary>
internal sealed record AllocationReversalRecord(
    Guid Id,
    AllocationId AllocationId,
    PaymentId PaymentId,
    AccountId AccountId,
    ObligationId ObligationId,
    int PlanVersion,
    int InstalmentSequence,
    DateOnly DueDate,
    ComponentKey Component,
    Money Amount,
    UnwindCause Cause,
    Guid CauseId,
    JournalTxId JournalTxId,
    DateTimeOffset At);

/// <summary>
/// A recorded reversal of a payment. <c>Amount = FromFunds + Unwound + Deficit</c>, where <c>FromFunds</c> came from
/// the lot's remaining funds, <c>Unwound</c> from its allocations, and <c>Deficit</c> from value already refunded.
/// </summary>
internal sealed record PaymentReversalRecord(
    ReversalId Id,
    PaymentId PaymentId,
    AccountId? AccountId,
    string? UnidentifiedSource,
    ExternalReference? Reference,
    Money Amount,
    Money FromFunds,
    Money Unwound,
    Money Deficit,
    string Reason,
    JournalTxId JournalTxId,
    DateTimeOffset At);

/// <summary>A reversal received before its payment ("reversal before payment"), applied when the payment arrives.</summary>
internal sealed record PendingReversal(
    ReversalId Id,
    ExternalReference PaymentSource,
    ExternalReference? Reference,
    Money? Amount,
    string Reason,
    DateTimeOffset At);

/// <summary>A host-posted charge added to one instalment (ADR-008).</summary>
internal sealed record ChargeRecord(
    ChargeId Id,
    AccountId AccountId,
    ObligationId ObligationId,
    int PlanVersion,
    int InstalmentSequence,
    DateOnly DueDate,
    ComponentKey Component,
    Money Amount,
    string Description,
    JournalTxId JournalTxId,
    DateTimeOffset At);

/// <summary>The full reversal of a charge.</summary>
internal sealed record ChargeReversalRecord(ChargeId ChargeId, string Reason, JournalTxId JournalTxId, DateTimeOffset At);

internal enum WaiverKind
{
    Waiver = 1,
    WriteOff = 2,
}

/// <summary>One line of a waiver or a cancellation write-off: an amount cleared on one instalment component.</summary>
internal sealed record WaiverRecord(
    WaiverId Id,
    AccountId AccountId,
    ObligationId ObligationId,
    int PlanVersion,
    int InstalmentSequence,
    DateOnly DueDate,
    ComponentKey Component,
    Money Amount,
    WaiverKind Kind,
    string Reason,
    JournalTxId JournalTxId,
    DateTimeOffset At);

/// <summary>The part of a refund taken from one payment's lot.</summary>
internal sealed record RefundPortion(PaymentId PaymentId, string Source, Money Amount);

/// <summary>A refund and its lifecycle, Requested → Confirmed | Failed (ADR-010). The status changes exactly once.</summary>
internal sealed record RefundRecord(
    RefundId Id,
    AccountId? AccountId,
    string? UnidentifiedSource,
    Money Amount,
    ImmutableArray<RefundPortion> Portions,
    RefundStatus Status,
    string Reason,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    PaymentExceptionId? ExceptionId,
    DateTimeOffset? CompletedAt,
    ExternalReference? ProviderReference,
    string? FailureReason)
{
    public RefundView ToView() =>
        new(Id, AccountId, UnidentifiedSource, Amount, Status, Reason, RequestedBy, RequestedAt, CompletedAt, ProviderReference, FailureReason,
            [.. Portions.Select(p => new RefundPortionView(p.PaymentId, p.Amount))]);
}

/// <summary>An allocation and the part of it not yet unwound.</summary>
internal sealed record ActiveAllocation(AllocationRecord Allocation, Money Active);

/// <summary>Pure helpers that apply corrections to an account. ID generation is the only side effect.</summary>
internal static class Corrections
{
    /// <summary>Each allocation with the amount not yet unwound, in recording order; fully unwound ones are dropped.</summary>
    public static List<ActiveAllocation> Active(IEnumerable<AllocationRecord> allocations, IEnumerable<AllocationReversalRecord> reversals)
    {
        var unwound = reversals.GroupBy(r => r.AllocationId).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount.Amount));
        return
        [
            .. allocations
                .Select(a => new ActiveAllocation(a, a.Amount - new Money(unwound.GetValueOrDefault(a.Id), a.Amount.Currency)))
                .Where(a => a.Active.IsPositive),
        ];
    }

    /// <summary>
    /// Unwinds up to <paramref name="amount"/> from <paramref name="lifo"/> (most recent first), returning the funds
    /// to each allocation's lot and re-opening its instalment.
    /// </summary>
    public static (CustomerAccount Account, ImmutableArray<AllocationReversalRecord> Unwinds) Unwind(
        CustomerAccount account,
        IEnumerable<ActiveAllocation> lifo,
        Money amount,
        UnwindCause cause,
        Guid causeId,
        JournalTxId journalTxId,
        DateTimeOffset now,
        TimeProvider time)
    {
        var records = ImmutableArray.CreateBuilder<AllocationReversalRecord>();
        var left = amount;
        foreach (var (allocation, active) in lifo)
        {
            if (!left.IsPositive)
            {
                break;
            }

            var take = Money.Min(left, active);
            var obligation = account.GetObligation(allocation.ObligationId);
            var plan = obligation.GetPlan(allocation.PlanVersion).Unapply(allocation.InstalmentSequence, allocation.Component, take, now);
            account = account.ReplaceObligation(obligation.WithPlan(plan));
            var lot = account.GetLot(allocation.PaymentId);
            account = account.ReplaceLot(lot with { Remaining = lot.Remaining + take });
            records.Add(new AllocationReversalRecord(
                Guid.CreateVersion7(time.GetUtcNow()), allocation.Id, allocation.PaymentId, allocation.AccountId, allocation.ObligationId,
                allocation.PlanVersion, allocation.InstalmentSequence, allocation.DueDate, allocation.Component, take, cause, causeId, journalTxId, now));
            left -= take;
        }

        return (account, records.ToImmutable());
    }

    /// <summary>Takes funds out of lots oldest first (by value time, then payment ID), for refunds.</summary>
    public static List<(FundsLot Lot, Money Take)> TakeOldestFirst(IEnumerable<FundsLot> lots, Money amount)
    {
        var takes = new List<(FundsLot, Money)>();
        var left = amount;
        foreach (var lot in lots.Where(l => l.Remaining.IsPositive).OrderBy(l => l.ValueAt).ThenBy(l => l.PaymentId.Value))
        {
            if (!left.IsPositive)
            {
                break;
            }

            var take = Money.Min(left, lot.Remaining);
            takes.Add((lot, take));
            left -= take;
        }

        return left.IsPositive ? throw new RuleViolationException(ErrorCodes.InsufficientFunds, $"Only {amount - left} of {amount} is available.") : takes;
    }

    public static AllocationReversalView ToView(AllocationReversalRecord record) =>
        new(record.PaymentId, record.ObligationId, record.InstalmentSequence, record.DueDate, record.Component, record.Amount);
}
