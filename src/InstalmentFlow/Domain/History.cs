using System.Collections.Immutable;
using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Domain;

/// <summary>Every fact recorded for one customer account, with its journal transactions in journal order.</summary>
internal sealed record AccountHistory(
    ImmutableArray<PositionedTransaction> Journal,
    ImmutableArray<PaymentRecord> Payments,
    ImmutableArray<PaymentAssignment> Assignments,
    ImmutableArray<AllocationRecord> Allocations,
    ImmutableArray<AllocationReversalRecord> Unwinds,
    ImmutableArray<PaymentReversalRecord> Reversals,
    ImmutableArray<ChargeRecord> Charges,
    ImmutableArray<ChargeReversalRecord> ChargeReversals,
    ImmutableArray<WaiverRecord> Waivers,
    ImmutableArray<RefundRecord> Refunds);

/// <summary>
/// Reconstructs a customer account as it stood at a cut-off ("as-of by journal fold", ADR-004). Every money fact
/// belongs to a journal transaction, so the cut-off selects journal transactions recorded at or before it, and
/// the account's projections are rebuilt from the facts of those transactions, replayed in journal order. Plan
/// versions, which carry no money, are selected by their own timestamps. Pure; the current account supplies only
/// what never changes (identities, schedules, directions).
/// </summary>
internal static class HistoryFold
{
    public static CustomerAccount At(CustomerAccount current, AccountHistory history, DateTimeOffset cut)
    {
        if (current.OpenedAt > cut)
        {
            throw new NotFoundException("Account", $"{current.ExternalId} as of {cut:O}");
        }

        var included = history.Journal.Where(p => p.Transaction.RecordedAt <= cut).Select(p => p.Transaction).ToList();
        var ids = included.Select(t => t.Id).ToHashSet();
        bool In(JournalTxId id) => ids.Contains(id);

        var reversedCharges = history.ChargeReversals.Where(r => In(r.JournalTxId)).Select(r => r.ChargeId).ToHashSet();
        var obligations = current.Obligations
            .Where(o => o.OpenedAt <= cut)
            .Select(o => Rebuild(o, cut, history, In, reversedCharges))
            .ToImmutableArray();

        var lots = Replay(current, history, included);
        return current with
        {
            Obligations = obligations,
            Lots = [.. current.Lots.Where(l => lots.ContainsKey(l.PaymentId)).Select(l => lots[l.PaymentId] with { Directive = l.Directive })],
        };
    }

    private static Obligation Rebuild(Obligation obligation, DateTimeOffset cut, AccountHistory history, Func<JournalTxId, bool> included, HashSet<ChargeId> reversedCharges)
    {
        var currency = obligation.Currency;
        var plans = obligation.Plans
            .Where(p => p.CreatedAt <= cut)
            .Select(p =>
            {
                var status = p.EndedAt is { } ended && ended <= cut ? p.Status : PlanVersionStatus.Active;
                bool Here(int version, int sequence, ComponentKey key, Instalment i, InstalmentComponent c) =>
                    version == p.Version && sequence == i.Sequence && key == c.Key;

                var instalments = p.Instalments.Select(i =>
                {
                    var components = i.Components.Select(c =>
                    {
                        var posted = history.Charges.Where(x => x.ObligationId == obligation.Id && included(x.JournalTxId) &&
                                                                Here(x.PlanVersion, x.InstalmentSequence, x.Component, i, c)).ToList();
                        var charged = posted.Where(x => !reversedCharges.Contains(x.Id)).Select(x => x.Amount);
                        var allocated = history.Allocations.Where(x => x.ObligationId == obligation.Id && included(x.JournalTxId) &&
                                                                       Here(x.PlanVersion, x.InstalmentSequence, x.Component, i, c)).Select(x => x.Amount);
                        var unwound = history.Unwinds.Where(x => x.ObligationId == obligation.Id && included(x.JournalTxId) &&
                                                                 Here(x.PlanVersion, x.InstalmentSequence, x.Component, i, c)).Select(x => x.Amount);
                        var waived = history.Waivers.Where(x => x.ObligationId == obligation.Id && included(x.JournalTxId) &&
                                                                Here(x.PlanVersion, x.InstalmentSequence, x.Component, i, c)).Select(x => x.Amount);
                        var scheduled = c.Due - c.Charged;
                        var charge = Money.Sum(currency, charged);
                        return (Exists: scheduled.IsPositive || posted.Count > 0, Component: new InstalmentComponent(
                            c.Key, scheduled + charge, Money.Sum(currency, allocated) - Money.Sum(currency, unwound), Money.Sum(currency, waived), charge));
                    })
                    .Where(x => x.Exists)
                    .Select(x => x.Component)
                    .ToImmutableArray();
                    var rebuilt = i with { Components = components };
                    return rebuilt with { SettledAt = rebuilt.Outstanding.IsPositive ? null : i.SettledAt };
                }).ToImmutableArray();

                return p with
                {
                    Status = status,
                    EndedAt = status == PlanVersionStatus.Active ? null : p.EndedAt,
                    EndReason = status == PlanVersionStatus.Active ? null : p.EndReason,
                    Instalments = instalments,
                };
            })
            .ToImmutableArray();

        var cancelled = plans.Any(p => p.Status == PlanVersionStatus.Cancelled);
        var active = plans.SingleOrDefault(p => p.Status == PlanVersionStatus.Active);
        var status = cancelled ? ObligationStatus.Cancelled
            : active is not null && active.Instalments.All(i => !i.Outstanding.IsPositive) ? ObligationStatus.Settled
            : ObligationStatus.Active;
        return obligation with { Plans = plans, Status = status };
    }

    /// <summary>Replays every lot movement of the included transactions, in journal order (ADR-005).</summary>
    private static Dictionary<PaymentId, FundsLot> Replay(CustomerAccount current, AccountHistory history, List<JournalTransaction> included)
    {
        var lots = new Dictionary<PaymentId, FundsLot>();
        void Change(PaymentId payment, Func<FundsLot, FundsLot> change) => lots[payment] = change(lots[payment]);

        foreach (var tx in included)
        {
            foreach (var payment in history.Payments.Where(p => p.JournalTxId == tx.Id && p.AccountId == current.Id))
            {
                lots[payment.Id] = FundsLot.Received(payment.Id, payment.Amount, payment.IdentifiedObligation, payment.ValueAt);
            }

            foreach (var assignment in history.Assignments.Where(a => a.JournalTxId == tx.Id))
            {
                // Assignment moves a pool lot that can only have been reduced by reversals (doc 02 §6.7).
                var payment = history.Payments.Single(p => p.Id == assignment.PaymentId);
                lots[payment.Id] = FundsLot.Received(payment.Id, payment.Amount, null, payment.ValueAt) with
                {
                    Remaining = assignment.Amount,
                    Reversed = payment.Amount - assignment.Amount,
                };
            }

            foreach (var unwind in history.Unwinds.Where(u => u.JournalTxId == tx.Id))
            {
                Change(unwind.PaymentId, l => l with { Remaining = l.Remaining + unwind.Amount });
            }

            foreach (var reversal in history.Reversals.Where(r => r.JournalTxId == tx.Id))
            {
                var taken = reversal.FromFunds + reversal.Unwound;
                Change(reversal.PaymentId, l => l with { Remaining = l.Remaining - taken, Reversed = l.Reversed + taken, Deficit = l.Deficit + reversal.Deficit });
            }

            if (tx.Kind is JournalKind.RefundRequested or JournalKind.RefundFailed)
            {
                var refundId = tx.Lines.First(l => l.SourceType == LedgerSourceType.Refund).SourceId;
                var refund = history.Refunds.Single(r => r.Id.Value == refundId);
                foreach (var portion in refund.Portions)
                {
                    Change(portion.PaymentId, l => tx.Kind == JournalKind.RefundRequested
                        ? l with { Remaining = l.Remaining - portion.Amount, Refunded = l.Refunded + portion.Amount }
                        : Return(l, portion.Amount));
                }
            }

            foreach (var allocation in history.Allocations.Where(a => a.JournalTxId == tx.Id))
            {
                Change(allocation.PaymentId, l => l with { Remaining = l.Remaining - allocation.Amount });
            }
        }

        return lots;
    }

    /// <summary>The lot side of <c>RefundPostings.ReturnPortion</c>: a failed refund settles a deficit before it becomes credit.</summary>
    private static FundsLot Return(FundsLot lot, Money amount)
    {
        var settles = Money.Min(amount, lot.Deficit);
        return lot with
        {
            Remaining = lot.Remaining + amount - settles,
            Refunded = lot.Refunded - amount,
            Reversed = lot.Reversed + settles,
            Deficit = lot.Deficit - settles,
        };
    }
}
