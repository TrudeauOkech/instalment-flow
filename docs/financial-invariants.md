# Financial invariants

InstalmentFlow keeps money correct by construction and then checks that it did. Every invariant below is
**enforced** where the money moves (the engine refuses a command that would break it) and, where it can be
checked from stored data, **verified** independently of the code that wrote it.

`IInstalmentQueries.VerifyAsync(VerifyScope.Tenant)` runs the verifier. It folds the journal per ledger account,
folds the facts (allocations, reversals, charges, waivers, refunds) per instalment and per funds lot, and compares
both with the projections the engine reads. Use it in operations dashboards, after migrations and at the end of
host integration tests. `InstalmentFlow.Testing` wraps it as `ShouldSatisfyAllInvariantsAsync()`.

The numbering follows the domain model (doc 02 §7). "Store" means the in-memory store today and PostgreSQL
constraints from 0.2.

| # | Invariant | Enforced by | Verified by |
|---|---|---|---|
| I-1 | Every journal transaction balances per currency, and each line is a single positive debit or credit. | `JournalBuilder` refuses to build an unbalanced transaction. | `VerifyAsync` |
| I-2 | Journal transactions, payments, allocations and reversals are never changed or deleted; corrections are new facts. | There is no update or delete API; an allocation is undone by an allocation-reversal fact. | `VerifyAsync` (every unwind references a real allocation, never more than it) |
| I-3 | A payment `(source, reference)` is recorded once per tenant. | The store's atomic uniqueness check; a repeat returns `Duplicate`, a different amount `IF1011`. | `VerifyAsync` |
| I-4 | The same idempotency key returns the original result; with a different request, `IF1002`. | The command runner's payload fingerprint and the store's atomic key check. | Tests |
| I-5 | Per funds lot: active allocations + remaining + reversed + refunded = original, all non-negative. | Allocation, reversal and refund postings move value between these parts and nowhere else. | `VerifyAsync` (lot parts against facts) |
| I-6 | Nothing is over-settled: every instalment component's outstanding amount is ≥ 0, and projections equal the facts. | The plan validator (`IF2002`) and `PlanVersion` refuse over-settlement. | `VerifyAsync` (allocated, waived and charged per component) |
| I-7 | Allocations use confirmed funds in the same currency and tenant. | Money arithmetic refuses mixed currencies (`IF1001`); commands are tenant-scoped. | Tests |
| I-8 | The reversals of a payment never exceed its amount. | `IF2001`. | `VerifyAsync` (reversal splits and lot totals) |
| I-9 | Receiving and then fully reversing a payment leaves every instalment as if it never arrived (apart from re-allocation of other credit). | Reversals unwind the payment's own allocations, most recent first. | Property test |
| I-10 | Projections equal the fold of the journal at every committed state. | Every projection change is posted in the same commit as its journal lines. | `VerifyAsync`; a CI property run of 10⁵ random histories also checks the history fold against the projections |
| I-11 | Unapplied funds are never negative; a deficit exists only as a reversal receivable. | Reversals take refunded value as a deficit instead of overdrawing the lot. | `VerifyAsync` |
| I-12 | An open obligation is settled exactly when nothing is outstanding. | Status is derived after every change, both ways (a reversal or charge re-opens). | `VerifyAsync` |
| I-13 | Per component, the plan's amounts due equal what the obligation owes, including posted charges. | `ScheduleBuilder` checks totals when a plan is created or superseded. | `VerifyAsync` |
| I-14 | Allocation is deterministic. | Strategies are pure with explicit tie-breakers; tenants pin a strategy version. | Property test |
| I-15 | Every ledger line references the fact it comes from. | `LedgerLine.SourceType` and `SourceId` are required. | Journal export |
| I-16 | Amounts fit the currency's minor units and commands use positive amounts. | `Money` and request validation. | Tests |
| I-17 | Conservation per currency: clearing = held funds + applied funds + refunds payable − reversal receivables. | Follows from I-1 and I-5. | `VerifyAsync` |
| I-18 | Tenant isolation. | Tenant-scoped engines; the store refuses a commit that touches another tenant (`IF5001`). | `VerifyAsync`; cross-tenant tests |
| I-19 | No confirmed payment is lost, whatever identification decides. | Recording never depends on identification or allocation (ADR-002). | `VerifyAsync` (one lot per payment); property test |
| I-20 | A payment with zero or several candidate obligations is never allocated without a resolution or a tenant opt-in. | Identification holds it with an exception, and sweeps skip funds with an open exception. | Property test |
| I-21 | Directed funds only pay the directed obligation; leftovers stay unapplied. | Eligibility is the directed obligation only, including re-allocation after a reversal. | Tests |
| I-22 | Unidentified funds always have an open payment exception. | Dismissal is refused while funds are unidentified; a failed refund raises a new exception. | `VerifyAsync` |
| I-23 | A superseded plan version has no allocations, waivers or charges. | `IF4001`. | `VerifyAsync` |

## When verification fails

A failed check means stored data disagrees with itself, which the engine never does on its own: suspect a manual
database edit, a store bug or a restore from inconsistent backups. Each violation names its invariant and the
account, obligation or payment involved. Trace the payment with `TracePaymentAsync` (the financial-discrepancy
issue template asks for a redacted trace), and correct with compensating commands such as a reversal, waiver or
charge. Never edit history.
