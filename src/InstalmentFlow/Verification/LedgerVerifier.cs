using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Verification;

/// <summary>
/// Checks the financial invariants (doc 02 §7) against everything stored for a tenant, independently of the code
/// that wrote it: facts are folded and compared with the projections, and the journal is folded per ledger account
/// and compared with both. Becomes the public <c>VerifyAsync</c> in milestone M6; until then tests run it after
/// every scenario.
/// </summary>
internal static class LedgerVerifier
{
    public static IReadOnlyList<string> Verify(LedgerSnapshot snapshot)
    {
        var violations = new List<string>();
        var tenant = snapshot.Tenant.Id;

        // I-1: every journal transaction balances per currency; lines are positive and one-sided.
        foreach (var tx in snapshot.Journal)
        {
            if (tx.Lines.Length < 2)
            {
                violations.Add($"I-1: journal tx {tx.Id} has fewer than two lines.");
            }

            foreach (var line in tx.Lines.Where(l => l.Debit < 0 || l.Credit < 0 || (l.Debit > 0) == (l.Credit > 0)))
            {
                violations.Add($"I-1: journal tx {tx.Id} line on {line.Account} is not a single positive debit or credit.");
            }

            foreach (var group in tx.Lines.GroupBy(l => l.Currency).Where(g => g.Sum(l => l.Debit) != g.Sum(l => l.Credit)))
            {
                violations.Add($"I-1: journal tx {tx.Id} is unbalanced in {group.Key}.");
            }

            if (tx.Tenant != tenant)
            {
                violations.Add($"I-18: journal tx {tx.Id} belongs to tenant {tx.Tenant}.");
            }
        }

        // I-3: one payment per provider reference.
        foreach (var duplicate in snapshot.Payments.GroupBy(p => p.Source).Where(g => g.Count() > 1))
        {
            violations.Add($"I-3: {duplicate.Count()} payments share reference {duplicate.Key}.");
        }

        var unwoundByAllocation = snapshot.AllocationReversals.GroupBy(r => r.AllocationId).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount.Amount));
        decimal Active(AllocationRecord a) => a.Amount.Amount - unwoundByAllocation.GetValueOrDefault(a.Id);

        foreach (var reversal in snapshot.AllocationReversals)
        {
            if (snapshot.Allocations.All(a => a.Id != reversal.AllocationId))
            {
                violations.Add($"I-2: allocation reversal {reversal.Id} undoes unknown allocation {reversal.AllocationId}.");
            }
        }

        foreach (var allocation in snapshot.Allocations.Where(a => Active(a) < 0))
        {
            violations.Add($"I-9: allocation {allocation.Id} of {allocation.Amount} was unwound by more than its amount.");
        }

        // Refunds: requested and confirmed refunds hold value away from the lot; failed ones returned it.
        var refundedByPayment = snapshot.Refunds.Where(r => r.Status != RefundStatus.Failed).SelectMany(r => r.Portions)
            .GroupBy(p => p.PaymentId).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount.Amount));
        var reversedByPayment = snapshot.PaymentReversals.GroupBy(r => r.PaymentId).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount.Amount));

        foreach (var reversal in snapshot.PaymentReversals.Where(r => r.Amount.Amount != r.FromFunds.Amount + r.Unwound.Amount + r.Deficit.Amount || !r.Amount.IsPositive))
        {
            violations.Add($"I-8: reversal {reversal.Id} of {reversal.Amount} does not split into funds {reversal.FromFunds}, unwound {reversal.Unwound} and deficit {reversal.Deficit}.");
        }

        // I-19, I-5 and I-8: every payment has exactly one lot, held exactly once, whose parts add up.
        var lots = snapshot.Accounts.SelectMany(a => a.Lots.Select(l => (Holder: $"account {a.Id}", Lot: l)))
            .Concat(snapshot.Pools.SelectMany(p => p.Lots.Select(l => (Holder: $"unidentified {p.Source}", Lot: l))))
            .ToList();
        foreach (var payment in snapshot.Payments)
        {
            var held = lots.Where(l => l.Lot.PaymentId == payment.Id).ToList();
            if (held.Count != 1)
            {
                violations.Add($"I-19: payment {payment.Source} has {held.Count} funds lots (expected 1).");
                continue;
            }

            var lot = held[0].Lot;
            var active = snapshot.Allocations.Where(a => a.PaymentId == payment.Id).Sum(Active);
            if (lot.Original != payment.Amount || lot.Remaining.IsNegative || lot.Reversed.IsNegative || lot.Refunded.IsNegative || lot.Deficit.IsNegative ||
                active + lot.Remaining.Amount + lot.Reversed.Amount + lot.Refunded.Amount != lot.Original.Amount)
            {
                violations.Add($"I-5: lot of payment {payment.Source}: original {lot.Original}, active allocations {active}, remaining {lot.Remaining}, " +
                               $"reversed {lot.Reversed}, refunded {lot.Refunded}.");
            }

            var reversed = reversedByPayment.GetValueOrDefault(payment.Id);
            if (reversed > payment.Amount.Amount || lot.Reversed.Amount + lot.Deficit.Amount != reversed)
            {
                violations.Add($"I-8: payment {payment.Source} of {payment.Amount} has {reversed} of reversals; the lot shows reversed {lot.Reversed} and deficit {lot.Deficit}.");
            }

            if (lot.Refunded.Amount != refundedByPayment.GetValueOrDefault(payment.Id) || lot.Deficit > lot.Refunded)
            {
                violations.Add($"I-5: lot of payment {payment.Source} shows refunded {lot.Refunded} and deficit {lot.Deficit}; refunds total {refundedByPayment.GetValueOrDefault(payment.Id)}.");
            }
        }

        foreach (var orphan in lots.Where(l => snapshot.Payments.All(p => p.Id != l.Lot.PaymentId)))
        {
            violations.Add($"I-19: {orphan.Holder} holds a lot for unknown payment {orphan.Lot.PaymentId}.");
        }

        // I-11: unapplied funds are never negative (implied by I-5, checked explicitly).
        foreach (var account in snapshot.Accounts)
        {
            foreach (var unapplied in account.UnappliedByCurrency().Where(u => u.IsNegative))
            {
                violations.Add($"I-11: account {account.Id} has negative unapplied funds {unapplied}.");
            }

            if (account.TenantId != tenant)
            {
                violations.Add($"I-18: account {account.Id} belongs to tenant {account.TenantId}.");
            }
        }

        // I-22: unidentified funds always have an open exception.
        foreach (var pool in snapshot.Pools)
        {
            foreach (var lot in pool.Lots.Where(l => l.Remaining.IsPositive))
            {
                if (!snapshot.Exceptions.Any(e => e.PaymentId == lot.PaymentId && e.Status == PaymentExceptionStatus.Open))
                {
                    violations.Add($"I-22: unidentified funds of payment {lot.PaymentId} have no open exception.");
                }
            }
        }

        // I-10: projections equal the journal folded per ledger account.
        var balances = snapshot.Journal
            .SelectMany(tx => tx.Lines)
            .GroupBy(l => (l.Account, l.Currency))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));

        decimal Balance(string account, Currency currency) => balances.GetValueOrDefault((account, currency));

        var reversedCharges = snapshot.ChargeReversals.Select(r => r.ChargeId).ToHashSet();
        foreach (var account in snapshot.Accounts)
        {
            foreach (var currency in account.Lots.Select(l => l.Remaining.Currency).Distinct())
            {
                var projected = account.Unapplied(currency).Amount;
                var journal = -Balance(LedgerAccounts.Unapplied(account.Id, currency), currency);
                if (projected != journal)
                {
                    violations.Add($"I-10: account {account.Id} unapplied {currency} is {projected} but the journal says {journal}.");
                }

                var deficit = account.Lots.Where(l => l.Deficit.Currency == currency).Sum(l => l.Deficit.Amount);
                var receivable = Balance(LedgerAccounts.ReversalReceivable(account.Id, currency), currency);
                if (deficit != receivable)
                {
                    violations.Add($"I-11: account {account.Id} reversal deficit {currency} is {deficit} but the reversal receivable says {receivable}.");
                }
            }

            foreach (var byCurrency in snapshot.Refunds.Where(r => r.AccountId == account.Id).GroupBy(r => r.Amount.Currency))
            {
                var pending = byCurrency.Where(r => r.Status == RefundStatus.Requested).Sum(r => r.Amount.Amount);
                var journal = -Balance(LedgerAccounts.RefundPayable(account.Id), byCurrency.Key);
                if (pending != journal)
                {
                    violations.Add($"I-10: account {account.Id} has {pending} {byCurrency.Key} of pending refunds but refund payable says {journal}.");
                }
            }

            foreach (var obligation in account.Obligations)
            {
                VerifyObligation(snapshot, obligation, Active, reversedCharges, Balance, violations);
            }
        }

        foreach (var pool in snapshot.Pools)
        {
            foreach (var currency in pool.Lots.Select(l => l.Remaining.Currency).Distinct()
                         .Union(snapshot.Payments.Where(p => p.Source.Source == pool.Source).Select(p => p.Amount.Currency)))
            {
                var projected = pool.Lots.Where(l => l.Remaining.Currency == currency).Sum(l => l.Remaining.Amount);
                var journal = -Balance(LedgerAccounts.Unidentified(pool.Source), currency);
                if (projected != journal)
                {
                    violations.Add($"I-10: unidentified funds {pool.Source} {currency} are {projected} but the journal says {journal}.");
                }

                var deficit = pool.Lots.Where(l => l.Deficit.Currency == currency).Sum(l => l.Deficit.Amount);
                if (deficit != Balance(LedgerAccounts.ReversalReceivable(pool.Source), currency))
                {
                    violations.Add($"I-11: unidentified funds {pool.Source} {currency} have a deficit of {deficit} that the reversal receivable does not show.");
                }

                var pending = snapshot.Refunds.Where(r => r.UnidentifiedSource == pool.Source && r.Status == RefundStatus.Requested && r.Amount.Currency == currency).Sum(r => r.Amount.Amount);
                if (pending != -Balance(LedgerAccounts.RefundPayable(pool.Source), currency))
                {
                    violations.Add($"I-10: unidentified funds {pool.Source} have {pending} {currency} of pending refunds that refund payable does not show.");
                }
            }
        }

        // Clearing: what each provider account received, less reversals and confirmed refund payouts.
        var confirmedBySource = snapshot.Refunds.Where(r => r.Status == RefundStatus.Confirmed).SelectMany(r => r.Portions)
            .GroupBy(p => (p.Source, p.Amount.Currency)).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount.Amount));
        foreach (var bySource in snapshot.Payments.GroupBy(p => (p.Source.Source, p.Amount.Currency)))
        {
            var received = bySource.Sum(p => p.Amount.Amount);
            var reversed = bySource.Sum(p => reversedByPayment.GetValueOrDefault(p.Id));
            var expected = received - reversed - confirmedBySource.GetValueOrDefault(bySource.Key);
            var journal = Balance(LedgerAccounts.Clearing(bySource.Key.Source), bySource.Key.Currency);
            if (expected != journal)
            {
                violations.Add($"I-10: clearing {bySource.Key.Source} {bySource.Key.Currency} is {journal} but payments less reversals and refunds total {expected}.");
            }
        }

        // I-17: conservation per tenant and currency.
        foreach (var currency in snapshot.Payments.Select(p => p.Amount.Currency).Distinct())
        {
            var clearing = balances.Where(b => b.Key.Currency == currency && b.Key.Account.StartsWith("clearing:", StringComparison.Ordinal)).Sum(b => b.Value);
            var held = lots.Where(l => l.Lot.Remaining.Currency == currency).Sum(l => l.Lot.Remaining.Amount);
            var applied = snapshot.Allocations.Where(a => a.Amount.Currency == currency).Sum(Active);
            var payable = snapshot.Refunds.Where(r => r.Status == RefundStatus.Requested && r.Amount.Currency == currency).Sum(r => r.Amount.Amount);
            var deficit = lots.Where(l => l.Lot.Deficit.Currency == currency).Sum(l => l.Lot.Deficit.Amount);
            if (clearing != held + applied + payable - deficit)
            {
                violations.Add($"I-17: clearing {currency} is {clearing}; held {held} + applied {applied} + refund payable {payable} - reversal receivable {deficit} = {held + applied + payable - deficit}.");
            }
        }

        return violations;
    }

    private static void VerifyObligation(
        LedgerSnapshot snapshot,
        Obligation obligation,
        Func<AllocationRecord, decimal> active,
        HashSet<ChargeId> reversedCharges,
        Func<string, Currency, decimal> balance,
        List<string> violations)
    {
        var allocations = snapshot.Allocations.Where(a => a.ObligationId == obligation.Id).ToList();
        var charges = snapshot.Charges.Where(c => c.ObligationId == obligation.Id && !reversedCharges.Contains(c.Id)).ToList();
        var waivers = snapshot.Waivers.Where(w => w.ObligationId == obligation.Id).ToList();
        var keys = obligation.Originated.Select(c => c.Key).Union(charges.Select(c => c.Component)).Union(snapshot.Charges.Where(c => c.ObligationId == obligation.Id).Select(c => c.Component));

        foreach (var key in keys)
        {
            var owed = (obligation.Originated.SingleOrDefault(c => c.Key == key)?.Amount.Amount ?? 0m) + charges.Where(c => c.Component == key).Sum(c => c.Amount.Amount);
            var paid = allocations.Where(a => a.Component == key).Sum(active);
            var waived = waivers.Where(w => w.Component == key).Sum(w => w.Amount.Amount);
            var journal = balance(LedgerAccounts.Receivable(obligation.Id, key), obligation.Currency);
            if (journal != owed - paid - waived)
            {
                violations.Add($"I-10: obligation {obligation.PaymentReference} {key} owes {owed} less {paid} paid and {waived} waived, but the journal says {journal}.");
            }

            // I-13: the current plan's amounts due per component equal what the obligation owes, charges included.
            if (obligation.CurrentPlan is { } current)
            {
                var due = current.Instalments.SelectMany(i => i.Components).Where(c => c.Key == key).Sum(c => c.Due.Amount);
                if (due != owed)
                {
                    violations.Add($"I-13: obligation {obligation.PaymentReference} plan v{current.Version} has {due} of {key} due; the obligation owes {owed}.");
                }
            }
        }

        foreach (var plan in obligation.Plans)
        {
            foreach (var instalment in plan.Instalments)
            {
                foreach (var component in instalment.Components)
                {
                    bool Here(int version, int sequence, ComponentKey key) => version == plan.Version && sequence == instalment.Sequence && key == component.Key;

                    // I-6: nothing over-settled; projections equal facts.
                    var paid = allocations.Where(a => Here(a.PlanVersion, a.InstalmentSequence, a.Component)).Sum(active);
                    var waived = waivers.Where(w => Here(w.PlanVersion, w.InstalmentSequence, w.Component)).Sum(w => w.Amount.Amount);
                    var charged = charges.Where(c => Here(c.PlanVersion, c.InstalmentSequence, c.Component)).Sum(c => c.Amount.Amount);
                    if (component.Outstanding.IsNegative || component.Allocated.Amount != paid || component.Waived.Amount != waived || component.Charged.Amount != charged)
                    {
                        violations.Add($"I-6: {obligation.PaymentReference} v{plan.Version} #{instalment.Sequence} {component.Key}: due {component.Due}, " +
                                       $"allocated {component.Allocated} (facts {paid}), waived {component.Waived} (facts {waived}), charged {component.Charged} (facts {charged}).");
                    }
                }

                if (instalment.Outstanding.IsPositive != (instalment.SettledAt is null))
                {
                    violations.Add($"I-6: {obligation.PaymentReference} v{plan.Version} #{instalment.Sequence} has outstanding {instalment.Outstanding} and SettledAt {instalment.SettledAt}.");
                }
            }
        }

        // I-12: an open obligation is settled exactly when nothing is outstanding.
        if (obligation.IsOpen)
        {
            var outstanding = obligation.ActivePlan?.Instalments.Any(i => i.Outstanding.IsPositive) ?? false;
            if ((obligation.Status == ObligationStatus.Settled) == outstanding)
            {
                violations.Add($"I-12: obligation {obligation.PaymentReference} is {obligation.Status} with outstanding = {outstanding}.");
            }
        }
        else if (obligation.Status == ObligationStatus.Cancelled && (obligation.ActivePlan is not null || obligation.CurrentPlan?.Status != PlanVersionStatus.Cancelled))
        {
            violations.Add($"ADR-011: cancelled obligation {obligation.PaymentReference} must have its plan cancelled and none active.");
        }

        // I-23: allocations, waivers and charges target a plan version of their obligation, and superseded plans have none.
        foreach (var (what, version) in allocations.Select(a => ("allocation", a.PlanVersion))
                     .Concat(waivers.Select(w => ("waiver", w.PlanVersion)))
                     .Concat(snapshot.Charges.Where(c => c.ObligationId == obligation.Id).Select(c => ("charge", c.PlanVersion))))
        {
            var plan = obligation.Plans.SingleOrDefault(p => p.Version == version);
            if (plan is null || plan.Status == PlanVersionStatus.Superseded)
            {
                violations.Add($"I-23: a {what} of {obligation.PaymentReference} targets plan version {version}.");
            }
        }
    }
}
