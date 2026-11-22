# General-ledger export

InstalmentFlow keeps a double-entry journal (ADR-004). Every money movement is a balanced journal transaction whose
lines name a ledger account and the fact that caused them. Export it incrementally to your accounting system.

```csharp
var cursor = await myStore.LoadCursorAsync() ?? JournalCursor.Start;
await foreach (var transaction in flow.Queries.ReadJournalAsync(cursor))
{
    await myGl.PostAsync(Map(transaction));      // idempotently: key on transaction.TransactionId
    await myStore.SaveCursorAsync(transaction.Cursor);
}
```

- The journal is append-only and ordered. A cursor resumes exactly after the last transaction you posted.
- Corrections (reversals, refunds, waivers, write-offs) are new transactions, never edits. Post them as they come.
- Post idempotently, keyed on `TransactionId`. A crash between posting and saving the cursor then repeats
  harmlessly.

## Ledger accounts

The account names are listed in doc 02 §5. Map them to your chart of accounts:

| InstalmentFlow account | Meaning | Typical GL mapping |
|---|---|---|
| `clearing:{source}`, for example `clearing:mpesa:600123` | Money held at the provider (a paybill float) | Bank or mobile-money float |
| `obl:{obligation}:{component}` | What customers owe, per obligation and component | Loans receivable (principal), interest receivable, fees receivable |
| `origination:{component}` | The counterpart when an obligation is opened or a charge posted | Loan principal, sales, income or deferred income |
| `acct:{account}:unapplied:{currency}` | Customer credit not yet allocated | Customer deposits (liability) |
| `unidentified:{source}` | Money received from unknown payers | Suspense (liability) |
| `refund-payable:{account}` (or `refund-payable:unidentified:{source}`) | Refunds requested, not yet paid out | Refunds payable |
| `acct:{account}:reversal-receivable:{currency}` | Amounts customers owe back after a reversal of refunded money | Other receivables |
| `waiver:{component}` | Amounts waived or written off | Expense or allowance accounts |

Component codes are `principal`, `interest`, `fee` and `penalty`, followed by `.{chargeCode}` for a charge posted
with a code (for example `fee.late`).

InstalmentFlow does not calculate interest or penalties (ADR-008). Post them with `Charges.PostAsync` and they flow
through the same journal.
