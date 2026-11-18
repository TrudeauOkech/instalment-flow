# ADR-010: Refunds and reversals are recorded, not initiated with providers (1.0)

- Status: Accepted (2026-11-18)

## Decision
- **Reversal** means the financial effect of a previously received payment is negated, fully or partially. The engine records it from any source: a provider result, reconciliation, or an operator. It unwinds allocations as described in ADR-005.
- **Refund** means an instruction to return the customer's unapplied funds. The engine records the lifecycle `Requested → Confirmed | Failed`. The host or a separate integration executes the payout, then calls `Refunds.CompleteAsync`.
- **In 1.0, InstalmentFlow does not call** the M-Pesa Reversal API, M-Pesa B2C, the Airtel refund API or Airtel disbursement. Adapters *do* parse reversal-related **results and statement lines** so they can be recorded.
- A future `IRefundInitiator` / `IReversalInitiator` provider interface is designed in `docs/integrations/future-refund-initiation.md`. It is **not shipped in the 1.0 public API**, to avoid freezing an untested contract.

## Consequences
- The financial core is not coupled to payout approvals, initiator credentials or settlement rules.
- The highest-risk credentials (payout initiators) are never needed by the library in 1.0.
