# ADR-006: Pure, versioned allocation strategies

- Status: Accepted (2026-11-18)

## Decision
- `IAllocationStrategy.Plan(AllocationContext) → AllocationPlan` is a pure function. It has no I/O and does not read the clock (the clock comes in through the context). It must be deterministic, with explicit tie-breakers.
- **The engine validates every plan before applying it.** A plan is rejected unless:
  - every target is open;
  - no target goes below zero;
  - Σ lines ≤ the available amount;
  - every line is in the same currency and tenant;
  - every line is positive.
- Every allocation row stores `strategy_name` and `strategy_version`. Built-in behaviour changes ship as a new version. Tenants pin a version through their tenant policy.
- Built-in strategies: `OldestDueFirst` (default), `CurrentDueFirst`, `PrincipalFirst`, `ChargesFirst`, `Proportional` (largest remainder), and `Directed`. Each has future-instalment policies: `HoldAsCredit`, `ApplyToNextInstalments`, `PrepayPrincipal`.
- Strategies operate **within the target set that ADR-009 permits**. Identification decides *which* obligations are eligible; the strategy decides *how* funds are spread within them.

## Consequences
- Strategies are property-testable and safe to extend.
- A buggy custom strategy cannot violate an invariant.
