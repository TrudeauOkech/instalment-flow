# Versioning and compatibility

InstalmentFlow follows [Semantic Versioning](https://semver.org/). From 1.0, a minor or patch release never breaks
code, data or operations that followed this page. A breaking change waits for the next major version and is listed
in the [changelog](https://github.com/TrudeauOkech/instalment-flow/blob/main/CHANGELOG.md) with a migration note.

## What is frozen at 1.0

The compiler enforces the frozen surface. Each package's `PublicAPI.Shipped.txt` lists every public symbol, and
removing or changing one fails the build. Every entry has been reviewed for 1.0.

| Package | 1.0 status |
|---|---|
| `InstalmentFlow`, `.EntityFrameworkCore`, `.Payments`, `.AspNetCore`, `.Testing` | Frozen |
| `.Payments.Mpesa`, `.Payments.AirtelMoney` | **Not frozen yet.** Their APIs may still change after the sandbox runs (UAT). They will be frozen in a minor release once verified, and this page will say so |

All packages share one version and must be upgraded together. The PostgreSQL store refuses to start with
mismatched versions.

## Two kinds of interface

**Service interfaces** are implemented by InstalmentFlow; you call them. Examples: `IInstalmentFlow`,
`IObligations`, `IPayments`, `IAllocations`, `ICharges`, `IRefunds`, `IPaymentExceptions`, `IInstalmentQueries`,
`ITenantAdmin`, `ITenantPayments`, `IProviderInbox`, `IPaymentIntents`, `IReconciliation`, `IDiscrepancies` and
`IProviderRouting`.

- They **may gain members in minor versions**.
- Don't implement them yourself. For tests, use `InstalmentFlow.Testing` (an in-memory engine and scenarios)
  rather than hand-written fakes, which would stop compiling when a member is added.

**Extension points** are implemented by you: `IAllocationStrategy`, `IScheduleGenerator`, `IBusinessCalendar`,
`IPaymentReferenceResolver`, `ITenantContext`, `ITenantRouter`, `IIntegrationEventPublisher`,
`IProviderCredentialSource`, `IPayloadProtector`, `IPaymentProviderAdapter` and its capability interfaces
(`ICollectionInitiator`, `ITransactionStatusQuery`, `IStatementSource`, `IPaymentValidation`, `IProviderReplies`).

- They **never gain a member you must implement** in a minor version. New behaviour arrives as a new, optional
  interface, or as a member with a default implementation.
- The records passed to them (`AllocationContext`, `AllocationTarget`, `ProviderCallback`, `PaymentValidationRequest`, …) may gain properties.
  Existing constructors and `Deconstruct` overloads are kept.

## Enums

- **Names and values are persisted.** The database stores enum values in columns and names in JSON, and ledger
  account names include component names. An existing member is never renamed or renumbered.
- **New members may be added in minor versions.** Code that switches on an InstalmentFlow enum needs a default branch:
  log and route the unknown case to a person, never assume.

## Behaviour

- **Allocation strategies are versioned** (`StrategyRef`: name and version). A built-in strategy's behaviour never
  changes within a version; a change ships as a new version, and tenants pinned to the old one keep it (ADR-006).
- **Money rules don't change in minor versions:** rounding, residual placement, allocation validation and the
  invariants checked by `Verify`. A fix to a wrong result is the exception. It ships in a patch with a changelog entry
  that names the affected cases.
- **Error codes** (`ErrorCodes`, `IFnnnn`) keep their meaning. New codes may be added.

## Database

- **Migrations are forward-only and additive** within a major version. Every release upgrades the schema of every
  earlier release of the same major version. No release needs a manual data fix.
- **Your queries:** the tables are not a public API. Read through `flow.Queries` and the outbox. If you query the
  `instalment_flow` schema directly for reporting, expect columns to be added.

## Operations

- **Telemetry names** (`InstalmentFlow` source and meter, the span, metric and attribute names in
  [observability](observability.md)) are stable. New ones may be added.
- **Health check names, the callback route shape and ProblemDetails `errorCode` values** are stable.
- **Integration events** in the outbox: fields may be added; existing fields keep their name and meaning. Consumers
  must ignore unknown fields.

## Before 1.0

The `0.x` previews made no compatibility promise. The [changelog](https://github.com/TrudeauOkech/instalment-flow/blob/main/CHANGELOG.md)
lists what changed between them.
