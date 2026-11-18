# InstalmentFlow — Planning Pack (Overview)

Status: **Decisions locked 2026-11-18** (see [`docs/decisions/`](../decisions/README.md)) · No code yet · Next step: M0.

> **InstalmentFlow is an open-source, multi-tenant .NET library for managing flexible loan repayments, partial payments, instalment schedules and payment allocation.** It helps lenders accept irregular customer payments, track what remains outstanding, and allocate funds correctly across repayment obligations. It is *not* a complete loan management system, payment gateway, accounting platform or interest calculation engine.

| # | Document | Purpose |
|---|---|---|
| 00 | This overview | Locked decisions, MVP |
| 01 | [Product Requirements](01-product-requirements.md) | Problem, users, use cases, scope, prior art |
| 02 | [Domain & Financial Model](02-domain-and-financial-model.md) | Entities, aggregates, ledger, identification, state machines, invariants |
| 03 | [Architecture](03-architecture.md) | Packages, tenancy, persistence, events, provider integration |
| 04 | [API & SDK Design](04-api-and-sdk-design.md) | Public C# surface and usage examples |
| 05 | [Database Design](05-database-design.md) | PostgreSQL schema, RLS, indexes, transactions, concurrency |
| 06 | [Implementation Roadmap](06-implementation-roadmap.md) | Milestones with acceptance criteria and tests |
| 07 | [Risk & Failure Analysis](07-risk-and-failure-analysis.md) | Risks, failure scenarios, test strategy |
| 08 | [Open-Source Launch Plan](08-open-source-launch-plan.md) | Licence, NuGet, docs, governance |

## 1. Locked decisions

| Area | Decision for 1.0 | ADR |
|---|---|---|
| Scope | Source of truth for payments, identification, unapplied funds, allocations, outstanding balances and instalment status. **Not** for the whole loan account. Integrates with an existing loan management system through external IDs, events and queries. | [001](../decisions/ADR-001-product-scope.md) |
| Money flow | Record the confirmed payment first (unconditionally), then allocate. **A confirmed payment is never lost** because allocation fails. | [002](../decisions/ADR-002-receive-then-allocate.md) |
| Consistency | The customer account (per tenant) is the aggregate; optimistic concurrency with retry | [003](../decisions/ADR-003-customer-account-aggregate.md) |
| History | Append-only double-entry journal, immutable facts, corrections only by reversal or compensation | [004](../decisions/ADR-004-append-only-journal.md) |
| Traceability | Funds lots; reversals unwind allocations last-in, first-out | [005](../decisions/ADR-005-funds-lots.md) |
| Allocation | Pure, versioned, engine-validated strategies | [006](../decisions/ADR-006-allocation-strategies.md) |
| **Q4 Tenancy** | Multi-tenant from day one, in a shared database. Composite tenant keys and foreign keys, PostgreSQL row-level security, tenant-scoped engine instances, and versioned per-tenant policy. | [007](../decisions/ADR-007-multi-tenancy.md) |
| **Q2/Q5 Interest & penalties** | No calculation. The host posts interest, fees and penalties as charges. | [008](../decisions/ADR-008-no-interest-or-penalty-calculation.md) |
| **Q8 Identification** | A unique payment reference per loan. Exact match → directed allocation to that loan. Customer has several loans and no reference → **hold and raise an exception**. Missing or invalid reference → unidentified funds. Ambiguous reference → exception queue. Never allocate an ambiguous payment silently. | [009](../decisions/ADR-009-payment-identification.md) |
| **Q6 Refunds** | Refunds and reversals are recorded only. The library initiates no provider refunds, reversals or payouts in 1.0. | [010](../decisions/ADR-010-refunds-and-reversals-recorded-only.md) |
| **Q7 Plans** | Create a plan; supersede it only if it has no financial history; cancel it together with its obligation. Every plan version is kept. No restructuring. | [011](../decisions/ADR-011-plan-lifecycle.md) |
| **Q1 Distribution** | Allocation and distribution (between financier, dealer, insurer…) are separate capabilities. Distribution is deferred, and the data model is ready for it. | [012](../decisions/ADR-012-allocation-vs-distribution.md) |
| Packages | 7 packages, zero-dependency core | [013](../decisions/ADR-013-packages-and-dependencies.md) |
| Runtime | `net10.0` LTS | [014](../decisions/ADR-014-target-framework.md) |
| Licence | Apache-2.0 with the Developer Certificate of Origin (DCO) | [015](../decisions/ADR-015-licence.md) |
| **Q3 Providers** | First adopter target: a Kenyan digital lender or asset financier, using both paybill and STK push. Adapter order: simulator, then M-Pesa, then Airtel Money. | [016](../decisions/ADR-016-provider-integration.md) |

## 2. Assumptions challenged during planning (resolved)

- **"Payment splitting"** turned out to mean two capabilities: allocation (1.0) and distribution (later). See ADR-012.
- **Paid-through date** is supported as a derived view alongside instalments. Pay-as-you-go device lock and unlock depends on it.
- **M-Pesa does not push reversal notifications.** Reversals are discovered through reconciliation (Pull Transactions covers only 48 h; statement import covers the rest), so reconciliation is in the MVP.
- **Masked MSISDN in Daraja C2B v2** means payments are identified by reference, not by phone number (ADR-009).
- **Fewer packages:** 7 instead of 10 (ADR-013).

## 3. MVP (path to 1.0)

**In scope:**
- Tenants and versioned tenant policy.
- Money and currency handling.
- Obligations with principal, interest, fee and penalty components.
- Plans with generators (daily, weekly, monthly, custom); plan supersede and cancel.
- Grace periods, derived instalment status and paid-through date.
- Idempotent payment recording.
- The identification pipeline: payment references, unapplied and unidentified funds, the exception queue and its resolution commands.
- Allocation with 6 built-in strategies, plus custom strategies.
- Host-posted charges.
- Waivers, reversals (full and partial) and recorded refunds.
- Obligation cancellation with explicit write-off.
- Early settlement quote.
- Immutable journal, as-of reconstruction, `Verify` and journal export.
- In-memory store, plus the EF Core/PostgreSQL store with row-level security, outbox and inbox.
- Payment intents (STK push ambiguity handling) and reconciliation.
- Simulated provider.
- M-Pesa adapter: C2B, STK push and query, Transaction Status, Pull Transactions, statement import.
- ASP.NET Core endpoints with tenant routing.
- Airtel Money collection adapter.
- OpenTelemetry, samples and documentation.

**Excluded from 1.0:**
- Interest and penalty calculation.
- Restructuring.
- Payment distribution.
- Provider-initiated refunds, reversals and payouts.
- FX and cross-currency allocation.
- Back-dated replay.
- Machine learning and analytics.
- UI.
- Broker integrations.
- Microservices and Kubernetes.

**Release shape:**
- `0.1` is the core with the in-memory store.
- `0.2` adds PostgreSQL.
- `0.3` adds payments, the simulator and M-Pesa.
- `0.4` adds Airtel and hardening.
- `1.0` comes after an external pilot and API freeze.

## 4. Remaining minor questions (non-blocking; defaults chosen)

| Item | Default chosen | Change if… |
|---|---|---|
| Domain term in API: `Obligation` vs `LoanAccount` | `Obligation`. It covers loans, asset financing and BNPL; docs say "loan obligation" in examples. | You'd rather optimise for lender vocabulary over generality |
| Remainder after a referenced loan is fully paid | Stays as customer unapplied funds. It never spills onto another loan. | A tenant explicitly opts in to spill-over |
| Charge due policy when no instalment is given | Added to the next unpaid instalment | The tenant prefers "due immediately" |
| Shared shortcode across tenants (processor model) | Requires an explicit `ITenantRouter` | — |
