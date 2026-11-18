# ADR-008: No interest or penalty calculation in 1.0

- Status: Accepted (2026-11-18)

## Decision
- The engine **records** amounts owed per component: Principal, Interest, Fee (with a charge code), and Penalty. It does not **calculate** interest, penalties or fees.
- There are two ways amounts enter:
  1. **At origination or with the plan:** the host supplies the component amounts per instalment, for example a schedule with principal and interest columns. Simple generators help with equal splits of host-supplied totals.
  2. **Later, through `Charges.PostAsync`:** the host posts interest, a fee or a penalty against an obligation. The charge can optionally target a specific instalment; by default it is added to the next unpaid instalment or is due immediately, according to the `ChargeDuePolicy` per tenant. A charge carries a type, an amount, a code, a description and an idempotency key.
- Posted charges participate in allocation like any other component, and can be waived or reversed (both are facts).

## Consequences
- Regulatory and contractual pricing logic (CBK rules, in-duplum caps, grace-specific penalties) stays with the host.
- An optional `IChargePolicy` or interest-calculator module can come after 1.0 without changing the core model.
