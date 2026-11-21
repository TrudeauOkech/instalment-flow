# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- M8 payments core, `InstalmentFlow.Payments` (ADR-018):
  - Adapter interfaces with capability flags: `IPaymentProviderAdapter`, `ICollectionInitiator`, `ITransactionStatusQuery`, `IStatementSource`, plus `IPayloadProtector`. A provider must implement exactly the capabilities it declares; this is checked at start-up.
  - `NormalisedPaymentEvent` (payment received, payment reversed, collection succeeded or failed), with deduplication keys derived from the event.
  - The provider inbox: `ReceiveAsync` authenticates, normalises and stores each event once, before processing. `ProcessPendingAsync` routes each event to its tenant and hands it to the core, with leases, back-off, a failed state and `RequeueAsync`. Rule violations fail at once.
  - Tenant routing: `Routing.MapAsync` maps a provider account to exactly one tenant (`IF6004`). `ITenantRouter` handles shared accounts.
  - Payment intents: `ForTenant(t).Intents.InitiateAsync` stores the intent before calling the provider. A timeout makes it ambiguous, never failed, and the same key never initiates twice (`IF6003`). `IntentResolver.ResolveDueAsync` checks status with back-off (by merchant reference when the timeout lost the provider's ID) and expires intents after their time to live; a late confirmation still counts.
  - Reconciliation: `Reconciliation.RunAsync` classifies lines as `Matched`, `MissingInEngine` (ingested through the inbox), `MissingAtProvider`, `AmountMismatch`, `ReversedAtProvider` (a discrepancy, or reversed when the tenant uses `AutoReverse`) or `Unroutable`. Runs continue from a cursor with an overlap. Discrepancies are raised once and resolved by a person. `CsvStatementSource` imports RFC 4180 statement files with configurable columns and time zones (`IF6005`).
  - `SimulatedProvider` (ADR-016): paybill and push flows, status queries and a statement, with HMAC-signed callbacks. Faults are set per transaction: duplicate, delay, drop, reorder, timeout-then-success, amount mismatch and statement-only reversal. See [the simulator guide](docs/integrations/simulator.md).
  - PostgreSQL: `InstalmentFlowPostgres.CreatePaymentsAsync` and the `PaymentsSchema` migration (doc 05 §8). Intents and discrepancies are under row-level security. The EF package now checks that it, the core and the payments package are the same version.
- `IInstalmentQueries.ListPaymentsAsync(PaymentQuery)`: the payments from one source, within a value-time window.

### Changed
- The public API of 0.2.0-preview.1 is recorded in `PublicAPI.Shipped.txt`.

## [0.2.0-preview.1] - 2026-11-21

### Added
- M7 PostgreSQL store, `InstalmentFlow.EntityFrameworkCore` (0.2.0-preview):
  - `InstalmentFlowPostgres.CreateFactoryAsync`, `MigrateAsync` (migrations and role grants, run explicitly by the schema owner), `GenerateMigrationScript` (idempotent SQL) and `Enlist` (commands join a host-owned transaction through savepoints).
  - The schema of doc 05, as built (doc 05 §8): composite tenant-first keys and foreign keys, `numeric(19,4)` money, append-only and no-truncate triggers on facts, a deferred balance check on the journal (I-1), and row-level security on every table, forced even for the owner and failing closed without the tenant setting.
  - Optimistic commits with version checks, writing only changed projection rows; `ConcurrencyMode.PessimisticAccountLock`; database refusals mapped to the engine's exceptions (`Duplicate`, `IF1002`, `IF1006`, `IF1007`, `IF2004`…).
  - Command attempts at READ COMMITTED with torn-read detection on aggregate loads and an advisory lock per reversal reference; queries and `VerifyAsync` read one REPEATABLE READ snapshot.
  - Transactional outbox and `OutboxDispatcher`: `FOR UPDATE SKIP LOCKED` with leases, at-least-once delivery and per-account ordering; `IIntegrationEventPublisher` and `IntegrationEvent` in the core.
  - SQL-based `VerifyAsync` (I-1, I-5, I-6, I-8, I-10, I-11, I-12, I-17, I-19, I-22).
  - A schema-version check (missing or newer migrations), and a check that the store and core packages are the same version (ADR-017).
  - The whole core suite runs against PostgreSQL with `IF_STORE=postgres` (Testcontainers); CI runs it, and the store's own tests, on the Linux legs.

### Changed
- CI publishes to NuGet with Trusted Publishing instead of a stored API key.
- XML documentation no longer describes shipped features as future milestones (for example `InstalmentFlowRequest` and `IF1002`).
- `ILedgerStore` gained command scopes and store-provided verification; commands back off before a retry, not after a failed attempt.
- All `DateTimeOffset` values are stored in UTC, so hosts may pass any offset.

## [0.1.0-preview.1] - 2026-11-21

### Added
- M6 queries, history and verification (0.1.0-preview):
  - `AsOf` on `GetAccountAsync`, `GetObligationAsync` and `GetScheduleAsync`: past states are reconstructed by folding the facts of the journal transactions recorded up to the cut-off (`AsOf.Date` = end of a business date, `AsOf.At` = an instant; a `DateOnly` converts implicitly). A past query is unaffected by later commands.
  - `TracePaymentAsync`: receipt, assignment, allocations, unwinds, reversals, deficits, refunds and exceptions, oldest first.
  - `GetStatementAsync`: a customer statement with a running net balance (owed less credit).
  - `ReadJournalAsync`: incremental journal export with a `JournalCursor`, for the host's general ledger.
  - `VerifyAsync(VerifyScope.Tenant)`: the ledger verifier as a public report of `InvariantViolation`s.
  - `InstalmentFlow.Testing`: the scenario DSL (`Scenario.InMemory().Given(…).When(…).Then(…).RunAsync()`), `Plan` helpers, `ScenarioClock`, `ShouldBe` and `ShouldSatisfyAllInvariantsAsync`.
  - Samples 01 (console quickstart), 02 (pay-as-you-go phone financing) and 03 (multiple loans), run in CI.
  - Docs: [concepts](docs/concepts.md) and [financial invariants](docs/financial-invariants.md).
  - CI runs the samples and 10⁵ random histories, and publishes packages to NuGet from `v*` tags (environment `nuget`).

### Changed
- `IInstalmentQueries` takes `AsOf? asOf` instead of `DateOnly? asOfDate`.
- Only `InstalmentFlow` and `InstalmentFlow.Testing` are packed; the other packages stay unpublished until their milestones.

### Added
- M5 corrections:
  - `Payments.ReverseAsync`: full and partial reversals (ADR-005). The amount comes from the payment's unapplied funds first, then from its allocations most recent first; already-refunded value becomes a reversal receivable (`AccountSummary.ReversalReceivable`, `ReversalDeficitRecorded`). Re-opened instalments are re-allocated from other unapplied funds (`TenantPolicy.ReallocateAfterReversal`, default on). I-8 is enforced (`IF2001`).
  - Duplicate reversals by provider reference (`ReversalOutcome.Duplicate`; `IF2004` for different details), and reversal-before-payment parking: a reversal for an unrecorded payment is parked and applied when the payment arrives (`RecordOutcome.Reversed`; `ParkedReversalNotApplied` when it cannot be applied in full).
  - `Charges.PostAsync` for host-calculated interest, fees and penalties with a charge code and a due policy (`ChargeDue`; `TenantPolicy.ChargeDue`); `Charges.ReverseAsync`, which unwinds what paid the charge; `Charges.WaiveAsync`. A charged plan cannot be superseded.
  - `Refunds.RequestAsync`, `CompleteAsync` and `GetAsync`, recorded only (ADR-010): oldest funds first, a failed payout returns the credit (or settles a reversal deficit), `IF2005` and `IF2006`. `Exceptions.MarkForRefundAsync` for customer-held and unidentified funds; a failed refund raises `RefundFailed`.
  - `Obligations.CancelAsync` with explicit write-off lines that must equal what is outstanding (ADR-011; `IF4002`, `IF4003`), and `Obligations.QuoteEarlySettlementAsync`. `AllocateFundsRequest.FutureInstalments` overrides the tenant's future-instalment policy, for example to settle early.
  - `PaymentView.Reversed` and `Refunded`; obligation balances include posted charges and stay visible after cancellation.
  - The ledger verifier also checks I-8, I-9, I-11, I-13 with charges, I-17 (conservation), refund payable, reversal receivable and clearing net of reversals and refunds.
- M4 allocation engine:
  - `IAllocationStrategy`, `AllocationContext`, `AllocationPlan` (with `FillInOrder`) and an engine-side plan validator: a plan that over-settles, overspends the funds, pays a non-eligible target, repeats a target, uses another currency or is negative is rejected (`IF2002`).
  - Built-in strategies `oldest-due-first@1` (default), `current-due-first@1`, `principal-first@1`, `charges-first@1`, `proportional@1` and directed allocation, with deterministic tie-breaking (I-14).
  - Tenant policy: a pinned strategy version (`Allocation`), `ComponentOrder` (default principal → interest → fee → penalty) and `FutureInstalments` (`HoldAsCredit` by default, `ApplyToNextInstalments`, `PrepayPrincipal`).
  - `Payments.RecordAsync` now allocates identified payments in the same command. Directed payments never spill onto another loan (I-21); a customer with several loans and no reference is held with `AllocationInstructionRequired` (I-20).
  - `Allocations.AllocateAsync` for manual or customer-directed allocation of unapplied funds; `Exceptions.AllocateToObligationAsync`; `AssignToCustomerAsync` with an obligation allocates in the same command.
  - `InstalmentFlowBuilder.AddAllocationStrategy` for custom strategies (`IF2003` for an unknown one). A faulty or throwing strategy leaves the funds held with `AllocationFailed` (ADR-002).
  - Allocations record the strategy and the policy version; the ledger verifier also checks I-6, I-12 and I-23.
  - Executable golden tables in `docs/allocation-strategies/*.md`.
- M3 payment recording, identification and exceptions:
  - `Payments.RecordAsync` always records a confirmed payment: a funds lot plus a balanced journal entry, held as customer unapplied funds or tenant unidentified funds.
  - Identification by payment reference, known obligation or known customer, following ADR-009. Ambiguous payments are never directed. Tenant policies `SoleObligation` and `MultipleObligations` default to `Hold`. `IPaymentReferenceResolver` handles legacy references.
  - The payment exception queue: list, get, assign unidentified funds to a customer, and dismiss (blocked while funds are unidentified).
  - Idempotent commands: replay on the same key; `IF1002` for the same key with a different request; `Duplicate` or `IF1011` for a repeated provider reference.
  - Atomic commits with optimistic concurrency and jittered retries (`UseConcurrencyRetries`).
  - Double-entry journal for obligations, payments and assignments; internal invariant verifier.
  - `AccountSummary.Unapplied`, `Queries.GetPaymentAsync`.
- M2 tenants, obligations and schedules:
  - Tenants and versioned tenant policies (default grace, payment-reference format).
  - A tenant-scoped API: `InstalmentFlowFactory.CreateInMemory(...).ForTenant(id)` with Accounts, Obligations, Plans and Queries.
  - Obligations with principal, interest, fee and penalty components.
  - Repayment plans: equal instalments with an optional deposit, custom schedules, custom generators and business calendars. Schedules are validated so their totals equal the obligation (I-13).
  - Plan supersession without financial history (ADR-011), with every version kept.
  - Derived instalment status (payment state and timing), days past due, arrears and paid-through date.
  - Per-tenant uniqueness of external IDs and payment references; cross-tenant isolation.
  - `eng/update-public-api.sh` to record new public API.
- M1 money, currency and time:
  - `Money`: exact `decimal` amounts validated against the currency's minor units; same-currency arithmetic only (`IF1001`); explicit rounding modes; largest-remainder `Allocate` and `Split`.
  - `Currency`: a canonical ISO 4217 registry of 19 currencies (East and West African currencies plus USD, EUR and GBP).
  - `TenantClock`: converts instants to and from business dates in an IANA time zone through `TimeProvider`, with defined rules for daylight-saving gaps and overlaps.
  - `ExternalReference` and `IdempotencyKey` value objects.
  - `InstalmentFlowException` with stable error codes (`IF1001`–`IF1004`).
  - 62 unit and CsCheck property tests.
- M0 repository foundation:
  - seven package projects targeting `net10.0`;
  - central package management;
  - analyzers, with warnings treated as errors;
  - public API tracking;
  - bans on floating point and the system clock;
  - architecture tests enforcing ADR-013 package boundaries;
  - CI, CodeQL and Dependabot;
  - licence and community files.
- Planning documents (`docs/planning/`) and ADR-001 to ADR-016 (`docs/decisions/`).

### Changed
- GitHub Actions: checkout v7, setup-dotnet v6, upload-artifact v7.
- Removed `InvariantGlobalization` from the shared build settings, so IANA time-zone IDs resolve on Windows.

### Fixed
- Tests failed on the .NET 11 runtime. `[InlineData]` decimal values are doubles, and .NET 11 converts 10.01 to 10.009999999999999786837179272. Decimal test data now uses `TheoryData`, and a convention test forbids the pattern.
