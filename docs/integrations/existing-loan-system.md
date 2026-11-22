# Inside an existing loan system

Most lenders already have a loan management system: customers, applications, disbursement and collections. Add
InstalmentFlow to it for repayments, allocation and the ledger, without replacing it.

## Share one database transaction

When your system disburses a loan, write your own records and open the repayment schedule in **one** transaction.
That way a failure on either side leaves neither:

```csharp
await using var transaction = await lmsDb.Database.BeginTransactionAsync();
lmsDb.Applications.Add(application);
await lmsDb.SaveChangesAsync();

using (InstalmentFlowPostgres.Enlist(transaction.GetDbTransaction()))
{
    await flow.Obligations.OpenAsync(new OpenObligationRequest { … });
}

await transaction.CommitAsync();   // or roll back: the schedule disappears with your record
```

- Each InstalmentFlow command runs in a savepoint of your transaction; nothing is durable until you commit.
- Keep the transaction READ COMMITTED (PostgreSQL's default).
- Both sides must use the same PostgreSQL database. InstalmentFlow's tables live in the `instalment_flow` schema.
- [Sample 07](https://github.com/TrudeauOkech/instalment-flow/tree/main/samples/07-ExistingLoanSystem) shows a commit and a rollback.

## Map your identifiers

| Your system | InstalmentFlow |
|---|---|
| Customer ID | `OpenAccountRequest.ExternalId` (unique per tenant); refer to it with `AccountRef.ByExternalId` |
| Loan number | `OpenObligationRequest.ExternalId`; refer to it with `ObligationRef.ByExternalId` |
| The reference customers pay with | `PaymentReference` (unique per tenant, normalised). Use what customers already type |
| Lender (if you serve several) | A tenant (`TenantId`) with its own currency, time zone, policies and provider accounts |

## Keep idempotency keys meaningful

Derive keys from your own identifiers (`$"open-{loanNumber}"`, `$"mpesa:{shortcode}:{receipt}"`). A retry after a
timeout then returns the original result instead of acting twice. A key reused for a different request is refused
(`IF1002`).

## Read back into your system

- Balances, schedules and arrears: `flow.Queries` (`GetObligationAsync`, `GetScheduleAsync`, `GetAccountAsync`), as
  of any date.
- Events: subscribe through the outbox (`OutboxDispatcher` and your `IIntegrationEventPublisher`). Delivery is at
  least once, in per-account order.
- Your general ledger: see [general-ledger export](gl-export.md).

## Migrating existing loans

Open each loan with its remaining schedule (`PlanSpec.Custom`) and record past payments only if you need their
history in InstalmentFlow. Otherwise start from the outstanding balance on a cut-over date, and keep the earlier
history in your system.
