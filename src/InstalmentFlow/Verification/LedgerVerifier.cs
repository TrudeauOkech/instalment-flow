using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Verification;

/// <summary>
/// Checks the financial invariants (doc 02 §7) against everything stored for a tenant, independently of the code
/// that wrote it: the journal is folded and compared with the projections. Becomes the public <c>VerifyAsync</c>
/// in milestone M6; until then tests run it after every scenario.
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

        // I-19 and I-5: every payment has exactly one lot, held exactly once, within its original amount.
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
            if (lot.Original != payment.Amount || lot.Remaining.IsNegative || lot.Remaining > lot.Original)
            {
                violations.Add($"I-5: lot of payment {payment.Source} has original {lot.Original} and remaining {lot.Remaining} for a payment of {payment.Amount}.");
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
                var payment = snapshot.Payments.SingleOrDefault(p => p.Id == lot.PaymentId);
                var exception = snapshot.Exceptions.SingleOrDefault(e => e.Id == payment?.ExceptionId);
                if (exception is null || exception.Status != PaymentExceptionStatus.Open)
                {
                    violations.Add($"I-22: unidentified funds of payment {payment?.Source} have no open exception.");
                }
            }
        }

        // I-10 (funds and receivables): projections equal the journal folded per ledger account.
        var balances = snapshot.Journal
            .SelectMany(tx => tx.Lines)
            .GroupBy(l => (l.Account, l.Currency))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));

        decimal Balance(string account, Currency currency) => balances.GetValueOrDefault((account, currency));

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
            }

            foreach (var obligation in account.Obligations)
            {
                foreach (var component in obligation.Originated)
                {
                    var journal = Balance(LedgerAccounts.Receivable(obligation.Id, component.Key), obligation.Currency);
                    if (journal != component.Amount.Amount)
                    {
                        violations.Add($"I-10: obligation {obligation.PaymentReference} {component.Key} is {component.Amount} but the journal says {journal}.");
                    }
                }
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
            }
        }

        // Clearing: every payment debited its provider account exactly once (no reversals yet).
        foreach (var bySource in snapshot.Payments.GroupBy(p => (p.Source.Source, p.Amount.Currency)))
        {
            var received = bySource.Sum(p => p.Amount.Amount);
            var journal = Balance(LedgerAccounts.Clearing(bySource.Key.Source), bySource.Key.Currency);
            if (received != journal)
            {
                violations.Add($"I-10: clearing {bySource.Key.Source} {bySource.Key.Currency} is {journal} but payments total {received}.");
            }
        }

        return violations;
    }
}
