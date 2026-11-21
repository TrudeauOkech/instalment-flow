# ADR-018: Payments package: derived deduplication keys, one inbox path, cross-tenant work tables

- Status: Accepted (2026-11-21)

## Context
M8 builds the payments package of doc 03 §6–7: the adapter interface, the provider inbox, payment intents, tenant
routing and reconciliation. A payment can reach the engine by four paths: a callback, a statement line, a status
query and the host itself. Whichever paths report a transaction, and in whatever order, it must be recorded exactly
once. Some payments-package rows also exist before anyone knows which tenant they belong to.

## Decision
- **Deduplication keys are derived from the event, not chosen by adapters.** `NormalisedPaymentEvent` builds the key
  from its kind, merchant account and receipt (`payment:{account}:{receipt}`, `reversal:{account}:{receipt}`,
  `collection:{account}:{correlation or merchant reference}:succeeded|failed`). So a callback and a statement line for the
  same receipt produce the same inbox key and are stored once.
- **Every path goes through the inbox.** Reconciliation queues statement lines the engine lacks. The intent resolver
  queues collections a status query confirms. Only the inbox processor calls `RecordAsync` or `ReverseAsync`, with the
  idempotency key `inbox:{provider}:{dedupe key}`. A collection result and a statement credit for the same payment have
  different keys; the second one to arrive is a `Duplicate` in the core (I-3).
- **A timeout is ambiguous, never a failure.** An intent is stored as `Created` before the provider is called, so a
  crash between storing and calling still leaves it to be checked. The intent ID is sent as the merchant reference, so
  an intent whose request timed out (and so has no provider correlation ID) can still be queried and matched.
  Confirmation is accepted from any state except `Confirmed`, even after expiry, because money that arrived is a fact
  (ADR-002).
- **Reconciliation only changes what the statement proves.** A missing payment is ingested. Anything the statement
  contradicts (`MissingAtProvider`, `AmountMismatch`, and `ReversedAtProvider` unless the tenant opted into
  `AutoReverse`) becomes a discrepancy for a person. A discrepancy is raised once per tenant, transaction and kind,
  even after it is resolved. `MissingAtProvider` uses a new core query, `ListPaymentsAsync`, so it also catches
  payments the host recorded directly.
- **Cross-tenant tables hold no ledger data.** In PostgreSQL, `tenant_provider_accounts`, `provider_inbox`,
  `intent_checks` and `reconciliation_runs` have no row-level security: they are written before an event is routed, or
  read by workers that serve every tenant. `intent_checks` holds only intent IDs, tenants and times, so the resolver
  can find due intents without reading any tenant's intents. `payment_intents` and `reconciliation_discrepancies` are
  tenant tables with forced row-level security, like the ledger. Raw inbox payloads can be encrypted with
  `IPayloadProtector`.
- **The payments store port is internal, as ADR-017 decided for the ledger.** `InstalmentFlow.EntityFrameworkCore` now
  references `InstalmentFlow.Payments` and checks that all three packages are the same version.
- `IStatementSource.ReadAsync` takes the merchant account as well as the window (doc 03 §7.1 omitted it), because one
  provider adapter serves several accounts.

## Consequences
- An adapter cannot pick a bad deduplication key, and needs no knowledge of the other paths.
- A transaction reported by both a callback and a statement line can be stored twice in the inbox only when one is a
  collection result. The core then records it once.
- The inbox is shared by all tenants. Hosts that must keep tenants' raw payloads apart at rest should configure a
  payload protector.
- The migration SQL for the payments tables is `PaymentsSchemaSql.Up`; like the initial migration's SQL, it must not
  change once released.
