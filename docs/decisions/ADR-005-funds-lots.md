# ADR-005: Funds lots and reversal unwinding

- Status: Accepted (2026-11-18)

## Decision
- Each payment creates one funds lot (`original`, `remaining`).
- Allocations consume lots first-in, first-out by `(value_at, payment_id)`.
- Each allocation references exactly one lot and one obligation target: a component plus an instalment.
- Reversing amount *R* of payment *P*:
  1. Take up to *R* from P's lot remaining.
  2. If more is needed, unwind P's allocations, most recent first, until *R* has been freed. Each unwind is an allocation-reversal fact.
  3. Remove *R* from unapplied funds.
  4. Optionally, depending on tenant policy, re-allocate other remaining funds.
- If P's value has already left the business (a refund was recorded), the shortfall is posted to a `reversal-receivable` account and a `ReversalDeficitRecorded` event is raised.

## Consequences
- The answer to "where did this shilling go?" is always exact.
- Partial reversals are deterministic and documented.
