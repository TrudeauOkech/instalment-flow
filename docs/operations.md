# Operations runbook

What to run, watch and do when running InstalmentFlow in production. Telemetry names are listed in
[observability](observability.md); security settings in the [threat model](security/threat-model.md#hardening-checklist-for-hosts).

## Daily checks

| Check | How | Healthy |
|---|---|---|
| Ledger integrity | `flow.Queries.VerifyAsync(VerifyScope.Tenant)` per tenant, as a scheduled job | `Passed`. Alert on `instalmentflow.verify.violations > 0` |
| Inbox | `/health` (`instalmentflow-payments`) or `payments.GetHealthAsync()` | No backlog older than 5 min; no failed entries |
| Reconciliation | Health check per account in `Workers.ReconcileAccounts` | A run within 3 h (M-Pesa Pull only reaches back 48 h) |
| Exceptions | `flow.Exceptions.ListOpenAsync()` | Worked daily; each one is money waiting for a decision |
| Discrepancies | `payments.ForTenant(t).Discrepancies.ListOpenAsync()` | Explained and resolved |
| Outbox | `instalmentflow-outbox` health check (`OutboxDispatcher.GetLagAsync`) | Lag under 5 min |

## Payment exception queue

Every payment is recorded (ADR-002). One that cannot be applied automatically raises an exception with a reason:

| Reason | Meaning | Usual action |
|---|---|---|
| `UnidentifiedPayment` | No customer could be found (wrong or empty reference) | Find the customer from the payer or phone call, then `AssignToCustomerAsync`, optionally with the obligation |
| `AmbiguousReference` | The reference matches more than one obligation | Ask the customer, then assign or allocate to the right one |
| `AllocationInstructionRequired` | A customer with several loans paid without a reference | `AllocateToObligationAsync` as the customer instructs |
| `ObligationNotPayable` | The obligation is settled or cancelled | Allocate to another obligation, leave as credit, or `MarkForRefundAsync` |
| `NoPayableObligation` | The customer is known but has nothing to pay | Leave as credit for a future loan, or `MarkForRefundAsync` |
| `CurrencyMismatch` | The payment's currency differs from the obligation's | Refund, or dismiss with a note after handling outside the engine |
| `ParkedReversalNotApplied` | A reversal that arrived before its payment could not be applied in full | Check the provider's statement; record the remainder with `Payments.ReverseAsync` |
| `RefundFailed` | A refund payout failed; the money is back with the customer | Retry the payout or keep it as credit |
| `AllocationFailed` | A custom strategy threw or planned something invalid | Fix the strategy, then allocate manually |

Unidentified funds cannot be dismissed (I-22). They must be assigned or refunded. Assignment cannot be undone in 1.0
([failure scenario 16](failure-scenarios.md)), so check the customer before assigning.

## Inbox entries that failed

`payments.Inbox.ListAsync(InboxStatus.Failed)` lists entries a person must look at. `Error` says why:

- **`Held: awaiting statement confirmation`:** a large callback payment (`ConfirmByStatementAbove`). Reconciliation
  releases it when the statement shows the same transaction and amount. If no statement line appears, treat it as a
  possible forgery: do not requeue it, and confirm with the provider first.
- **`Held: M-Pesa amounts are whole shillings…`:** a malformed amount. Confirm with the provider and record it
  manually if it is real. It is never rounded.
- **A rule's error code** (for example `IF2001`, a reversal larger than the payment): the event conflicts with the
  ledger. Investigate, then `RequeueAsync` once fixed, or leave it failed with a note.
- **`No tenant is mapped…`** after the retries ran out: map the provider account (`Routing.MapAsync`), then
  `RequeueAsync`.

## Reconciliation discrepancies

| Kind | Meaning | Action |
|---|---|---|
| `MissingAtProvider` | The engine has a payment the statement does not show | The most serious: possibly a forged callback, or a host recording error. Confirm with the provider (for M-Pesa, Transaction Status). If false, reverse it with `Payments.ReverseAsync` and a reason |
| `AmountMismatch` | The amounts differ | Confirm the true amount; correct with a partial reversal, or record the difference |
| `ReversedAtProvider` | The provider reversed a payment the engine has not | Record it with `Payments.ReverseAsync` (or set `ReversedAtProviderPolicy.AutoReverse` for the tenant) |

Resolve each with `Discrepancies.ResolveAsync(id, note)`. Resolving records the decision; it changes no money.

## Intents

- **`Ambiguous`:** the provider may have taken the money. Tell the customer "checking", not "failed". The resolver
  queries with back-off. Never start a second collection for the same purchase while one is ambiguous.
- **`Expired`:** nothing was confirmed within the time to live. A late confirmation still records the payment.
- M-Pesa cannot match a push whose reply was lost (no merchant reference). Its payment arrives as unidentified funds
  through the exception queue.

## Cross-tenant isolation checks

After every deployment or role change, as the application role:

```sql
select rolsuper, rolbypassrls from pg_roles where rolname = current_user;       -- both false
select count(*) from instalment_flow.payments;                                  -- 0 without the tenant setting
```

## Migrations

- Run `InstalmentFlowPostgres.MigrateAsync(ownerConnectionString, new MigrationOptions { ApplicationRole = …, DispatcherRole = … })`
  as the schema owner during deployment, never from the application at start-up. `GenerateMigrationScript()` gives an
  idempotent script for DBAs.
- The application refuses to start against a schema with a missing or unknown migration.
- Upgrade all InstalmentFlow packages together. The store checks that the core, payments and EF packages are the same
  version.

## Incidents

| Symptom | First steps |
|---|---|
| `Verify` fails | Stop automated corrections. Read each violation (it names the invariant and the difference). Check for manual SQL. Open a [financial discrepancy issue](https://github.com/TrudeauOkech/instalment-flow/issues/new/choose) with a redacted trace if the library is at fault |
| Inbox backlog grows | Check the workers are running and the database is reachable. Entries retry with back-off; nothing is lost |
| Callbacks stop | Check provider status and the endpoint (TLS, path secret, allowlist). Reconciliation will ingest what was missed within the Pull window; run it manually with a wider window if needed |
| `command.retries` spikes | A hot account (often a shared unidentified-funds pool). Consider `ConcurrencyMode.PessimisticAccountLock` |
| Outbox lag grows | Check the publisher (message broker). Events wait in order; nothing is lost |
