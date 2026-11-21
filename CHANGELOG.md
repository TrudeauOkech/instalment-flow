# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
