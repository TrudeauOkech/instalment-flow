# ADR-011: Plan lifecycle without restructuring

- Status: Accepted (2026-11-18)

## Decision
- A repayment plan is an immutable, versioned fact. An obligation has exactly one **active** plan version at a time. All past versions are kept with status `Superseded` or `Cancelled`.
- Operations allowed in 1.0:
  - `Plans.CreateAsync`: creates the first plan when the obligation is opened. The plan total per component must equal the obligation's originated amounts.
  - `Plans.SupersedeAsync`: **only if no allocation or waiver has ever targeted the current version**. This is for correcting set-up mistakes. A superseded plan with history is rejected with `IF4001 PlanHasFinancialHistory`.
  - `Obligations.CancelAsync`: cancels the obligation and its active plan together, for example a cancelled agreement or a returned device. Unpaid instalments are voided. Outstanding component balances are cleared **only by explicit write-off or waiver lines with a reason code**, so the journal shows how. Allocations already made stay as history. Remaining unapplied funds stay with the customer.
- **Excluded from 1.0:** restructuring (changing amounts or frequency of a plan with payments, term extension, capitalisation, re-allocation onto a revised schedule). The data model already supports it (plan versions, and allocations that reference the plan version), so a future module can add a replacement schedule while keeping the original schedule and its history.

## Consequences
- Historical instalments and allocations are never modified.
