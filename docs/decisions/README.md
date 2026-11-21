# Architecture Decision Records

Format: lightweight MADR. An ADR is never edited after it is accepted, except for its status line. It is superseded by a new ADR.

| ADR | Title | Status | Origin |
|---|---|---|---|
| [001](ADR-001-product-scope.md) | Product scope: payment allocation engine, not an LMS, gateway, accounting platform or interest engine | Accepted | Overview D1; owner decision "source of truth for allocation" |
| [002](ADR-002-receive-then-allocate.md) | Two-step model: record funds, then allocate. A confirmed payment is never lost. | Accepted | D2; owner decision "never lose a payment" |
| [003](ADR-003-customer-account-aggregate.md) | Customer account is the consistency boundary; optimistic concurrency | Accepted | D3 |
| [004](ADR-004-append-only-journal.md) | Append-only double-entry journal plus projections; immutable history | Accepted | D4; owner decision "immutable history" |
| [005](ADR-005-funds-lots.md) | Funds lots and LIFO reversal unwinding | Accepted | D6 |
| [006](ADR-006-allocation-strategies.md) | Pure, versioned allocation strategies validated by the engine | Accepted | D5 |
| [007](ADR-007-multi-tenancy.md) | Multi-tenant from day one; isolation enforced by the system | Accepted | Q4 |
| [008](ADR-008-no-interest-or-penalty-calculation.md) | No interest or penalty calculation in 1.0; host posts charges | Accepted | Q2, Q5 |
| [009](ADR-009-payment-identification.md) | Payment identification, unapplied funds and the exception queue | Accepted | Q8 |
| [010](ADR-010-refunds-and-reversals-recorded-only.md) | Refunds and reversals are recorded, not initiated with providers | Accepted | Q6 |
| [011](ADR-011-plan-lifecycle.md) | Plan lifecycle: create, supersede, cancel; no restructuring in 1.0 | Accepted | Q7 |
| [012](ADR-012-allocation-vs-distribution.md) | Allocation and distribution are separate capabilities; distribution deferred | Accepted | Q1 |
| [013](ADR-013-packages-and-dependencies.md) | Seven packages; zero-dependency core | Accepted | D8 |
| [014](ADR-014-target-framework.md) | Target net10.0 (LTS) | Accepted | D9 |
| [015](ADR-015-licence.md) | Apache-2.0 with DCO | Accepted | D10 |
| [016](ADR-016-provider-integration.md) | Provider-agnostic core; simulator first, then M-Pesa, then Airtel | Accepted | Q3 |
| [017](ADR-017-internal-storage-ports.md) | Storage ports stay internal; store packages ship in lockstep with the core | Accepted | M7 |
