using System.Runtime.CompilerServices;
using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;

namespace InstalmentFlow.Engine;

internal sealed partial class QueriesService
{
    private const int JournalPage = 500;

    public async Task<PaymentTrace> TracePaymentAsync(PaymentRef payment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payment);
        await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var record = await PaymentLookup.FindAsync(context, payment, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Payment", payment.ToString());
        var view = await PaymentViewAsync(record, cancellationToken).ConfigureAwait(false);
        var holding = await PaymentLookup.HoldingAsync(context, record, cancellationToken).ConfigureAwait(false);
        var store = context.Store;
        var tenant = context.TenantId;

        var steps = new List<PaymentTraceStep>
        {
            new(record.RecordedAt, record.AccountId is null ? PaymentTraceStepKind.Unidentified : PaymentTraceStepKind.Received, record.Amount,
                record.IdentifiedObligation, null, null, $"{record.Source} recorded ({record.TargetKind}{(record.RawReference is { } raw ? $" '{raw}'" : string.Empty)})."),
        };

        if (await store.GetAssignmentAsync(tenant, record.Id, cancellationToken).ConfigureAwait(false) is { } assignment)
        {
            steps.Add(new(assignment.At, PaymentTraceStepKind.Assigned, assignment.Amount, null, null, null, $"Assigned to account {assignment.AccountId}."));
        }

        foreach (var allocation in await store.ListAllocationsAsync(tenant, record.Id, cancellationToken).ConfigureAwait(false))
        {
            steps.Add(new(allocation.CreatedAt, PaymentTraceStepKind.Allocated, allocation.Amount, allocation.ObligationId, allocation.InstalmentSequence,
                allocation.Component, $"Allocated by {allocation.Strategy} under policy version {allocation.PolicyVersion}."));
        }

        if (holding.AccountId is { } holder)
        {
            foreach (var unwind in (await store.ListAccountAllocationReversalsAsync(tenant, holder, cancellationToken).ConfigureAwait(false)).Where(u => u.PaymentId == record.Id))
            {
                steps.Add(new(unwind.At, PaymentTraceStepKind.AllocationReversed, unwind.Amount, unwind.ObligationId, unwind.InstalmentSequence, unwind.Component,
                    unwind.Cause == UnwindCause.PaymentReversal ? "Unwound by a reversal of this payment." : "Unwound because the charge it paid was reversed."));
            }
        }

        foreach (var reversal in await store.ListPaymentReversalsAsync(tenant, record.Id, cancellationToken).ConfigureAwait(false))
        {
            steps.Add(new(reversal.At, PaymentTraceStepKind.Reversed, reversal.Amount, null, null, null,
                $"Reversed{(reversal.Reference is { } r ? $" ({r})" : string.Empty)}: {reversal.FromFunds} from unapplied funds, {reversal.Unwound} unwound. {reversal.Reason}"));
            if (reversal.Deficit.IsPositive)
            {
                steps.Add(new(reversal.At, PaymentTraceStepKind.ReversalDeficit, reversal.Deficit, null, null, null, "Already refunded; owed back as a reversal receivable."));
            }
        }

        foreach (var refund in await store.ListPaymentRefundsAsync(tenant, record.Id, cancellationToken).ConfigureAwait(false))
        {
            var portion = refund.Portions.Single(p => p.PaymentId == record.Id).Amount;
            steps.Add(new(refund.RequestedAt, PaymentTraceStepKind.RefundRequested, portion, null, null, null, $"Refund {refund.Id} requested by {refund.RequestedBy}: {refund.Reason}"));
            if (refund.CompletedAt is { } completed)
            {
                steps.Add(refund.Status == RefundStatus.Confirmed
                    ? new(completed, PaymentTraceStepKind.RefundConfirmed, portion, null, null, null, $"Refund {refund.Id} paid out{(refund.ProviderReference is { } p ? $" ({p})" : string.Empty)}.")
                    : new(completed, PaymentTraceStepKind.RefundFailed, portion, null, null, null, $"Refund {refund.Id} failed: {refund.FailureReason}"));
            }
        }

        foreach (var exception in await store.ListPaymentExceptionsAsync(tenant, record.Id, cancellationToken).ConfigureAwait(false))
        {
            steps.Add(new(exception.OpenedAt, PaymentTraceStepKind.ExceptionRaised, null, null, null, null, $"{exception.Reason}: {exception.Detail}"));
            if (exception.Resolution is { } resolution)
            {
                steps.Add(new(resolution.At, PaymentTraceStepKind.ExceptionResolved, null, null, null, null, $"{resolution.Kind} by {resolution.ResolvedBy}{(resolution.Note is { } n ? $": {n}" : string.Empty)}"));
            }
        }

        return new PaymentTrace(view, holding.Lot?.Deficit ?? Money.Zero(record.Amount.Currency), [.. steps.OrderBy(s => s.At)]);
    }

    public async IAsyncEnumerable<StatementLine> GetStatementAsync(
        AccountRef account,
        DateOnly fromDate,
        DateOnly toDate,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var id = await context.ResolveAsync(account, cancellationToken).ConfigureAwait(false);
        var current = await context.LoadAccountAsync(id, cancellationToken).ConfigureAwait(false);
        var history = await context.Store.LoadHistoryAsync(context.TenantId, id, cancellationToken).ConfigureAwait(false);

        // The customer's own ledger accounts: their obligations' receivables, unapplied funds and reversal receivable.
        var obligations = current.Obligations.ToDictionary(o => $"obl:{o.Id}:", o => o.PaymentReference);
        var unapplied = $"acct:{id}:unapplied:";
        var receivable = $"acct:{id}:reversal-receivable:";
        bool Mine(string ledgerAccount) =>
            ledgerAccount.StartsWith(unapplied, StringComparison.Ordinal) || ledgerAccount.StartsWith(receivable, StringComparison.Ordinal) ||
            obligations.Keys.Any(k => ledgerAccount.StartsWith(k, StringComparison.Ordinal));

        var balances = new Dictionary<Currency, decimal>();
        foreach (var (_, tx) in history.Journal)
        {
            foreach (var lines in tx.Lines.GroupBy(l => l.Currency))
            {
                var currency = lines.Key;
                var change = lines.Where(l => Mine(l.Account)).Sum(l => l.Debit - l.Credit);
                var balance = balances.GetValueOrDefault(currency) + change;
                balances[currency] = balance;
                if (tx.EffectiveDate < fromDate || tx.EffectiveDate > toDate)
                {
                    continue;
                }

                var primary = Primary(tx.Kind);
                yield return new StatementLine(
                    tx.EffectiveDate,
                    tx.RecordedAt,
                    (StatementEntryKind)tx.Kind,
                    Describe(tx, history, obligations),
                    new Money(lines.Where(l => l.SourceType == primary).Sum(l => l.Debit), currency),
                    new Money(lines.Where(l => l.SourceType == LedgerSourceType.Allocation).Sum(l => l.Debit), currency),
                    new Money(change, currency),
                    new Money(balance, currency));
            }
        }
    }

    public async IAsyncEnumerable<JournalTransactionView> ReadJournalAsync(JournalCursor from, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var position = from.Position;
        while (true)
        {
            var page = await context.Store.ReadJournalAsync(context.TenantId, position, JournalPage, cancellationToken).ConfigureAwait(false);
            foreach (var (at, tx) in page)
            {
                yield return new JournalTransactionView(
                    new JournalCursor(at), tx.Id.Value, tx.Kind.ToString(), tx.AccountId, tx.EffectiveDate, tx.RecordedAt,
                    [.. tx.Lines.Select(l => new JournalLineView(l.Account, l.Currency, l.Debit, l.Credit, l.SourceType.ToString(), l.SourceId))]);
                position = at;
            }

            if (page.Count < JournalPage)
            {
                yield break;
            }
        }
    }

    public async Task<VerificationReport> VerifyAsync(VerifyScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await context.LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = await context.Store.VerifyAsync(context.TenantId, cancellationToken).ConfigureAwait(false);
        var violations = result.Violations
            .Select(v =>
            {
                var colon = v.IndexOf(':', StringComparison.Ordinal);
                return colon > 0 ? new InvariantViolation(v[..colon], v[(colon + 1)..].Trim()) : new InvariantViolation("unknown", v);
            })
            .ToList();
        Telemetry.Verified(violations.Count);
        return new VerificationReport(context.Time.GetUtcNow(), violations.Count == 0, violations, result.JournalTransactions, result.Payments, result.Accounts);
    }

    /// <summary>The source type whose debits measure a transaction's amount.</summary>
    private static LedgerSourceType Primary(JournalKind kind) => kind switch
    {
        JournalKind.ObligationOpened => LedgerSourceType.Obligation,
        JournalKind.PaymentReceived or JournalKind.PaymentUnidentified => LedgerSourceType.Payment,
        JournalKind.UnidentifiedAssigned => LedgerSourceType.Assignment,
        JournalKind.FundsAllocated => LedgerSourceType.Allocation,
        JournalKind.PaymentReversed => LedgerSourceType.PaymentReversal,
        JournalKind.ChargePosted or JournalKind.ChargeReversed => LedgerSourceType.Charge,
        JournalKind.AmountWaived or JournalKind.ObligationCancelled => LedgerSourceType.Waiver,
        _ => LedgerSourceType.Refund,
    };

    private static string Describe(JournalTransaction tx, AccountHistory history, Dictionary<string, string> obligations)
    {
        string? Reference(Guid obligationId) => obligations.GetValueOrDefault($"obl:{obligationId}:");
        string Payment(Guid id) => history.Payments.SingleOrDefault(p => p.Id.Value == id)?.Source.ToString() ?? id.ToString();
        var source = tx.Lines.First().SourceId;
        return tx.Kind switch
        {
            JournalKind.ObligationOpened => $"Obligation {Reference(source)} opened",
            JournalKind.PaymentReceived => $"Payment {Payment(source)}",
            JournalKind.UnidentifiedAssigned => $"Payment {Payment(source)} assigned to the account",
            JournalKind.FundsAllocated => "Unapplied funds allocated",
            JournalKind.PaymentReversed => $"Reversal of payment {Payment(history.Reversals.SingleOrDefault(r => r.JournalTxId == tx.Id)?.PaymentId.Value ?? source)}",
            JournalKind.ChargePosted => history.Charges.SingleOrDefault(c => c.Id.Value == source) is { } c ? $"{c.Component} charge on {Reference(c.ObligationId.Value)}: {c.Description}" : "Charge",
            JournalKind.ChargeReversed => "Charge reversed",
            JournalKind.AmountWaived => history.Waivers.FirstOrDefault(w => w.JournalTxId == tx.Id) is { } w ? $"{w.Component} waived on {Reference(w.ObligationId.Value)}: {w.Reason}" : "Waiver",
            JournalKind.ObligationCancelled => history.Waivers.FirstOrDefault(w => w.JournalTxId == tx.Id) is { } x ? $"Obligation {Reference(x.ObligationId.Value)} cancelled: {x.Reason}" : "Obligation cancelled",
            JournalKind.RefundRequested => "Refund requested",
            JournalKind.RefundConfirmed => "Refund paid out",
            JournalKind.RefundFailed => "Refund failed; credit returned",
            _ => tx.Kind.ToString(),
        };
    }
}
