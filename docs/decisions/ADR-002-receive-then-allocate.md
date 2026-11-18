# ADR-002: Record funds first, then allocate. Never lose a payment.

- Status: Accepted (2026-11-18)

## Context
Allocation can fail or be undecidable: an unknown reference, an ambiguous reference, a customer with several loans, a currency mismatch, a closed loan. If recording a payment depended on allocating it, confirmed money could be rejected or dropped.

## Decision
1. Recording a confirmed payment is **unconditional** once the payment is authenticated and well formed. It always produces a Payment fact, a funds lot and a journal transaction. The funds land in exactly one of:
   - **customer unapplied funds**, when the customer is known;
   - **tenant unidentified funds** (suspense), when the customer is unknown.
2. Allocation is a separate step that only moves money **from** unapplied funds **to** obligation components. It can run in the same database transaction (auto-allocate) or later (manual, directed, or after an exception is resolved).
3. Any failure in allocation leaves the payment recorded and the funds unapplied, and raises a payment exception (ADR-009).

## Consequences
- Overpayment is just "funds left after allocation".
- Concurrency reduces to "a lot's remaining amount is never negative" (ADR-003, ADR-005).
- An allocation bug can never cause a payment to be lost. At worst, funds sit unapplied and visible.
