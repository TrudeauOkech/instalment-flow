# ADR-016: Provider-agnostic core; simulator first

- Status: Accepted (2026-11-18)

## Decision
- The core accepts only normalised, confirmed payments. It does not know whether money arrived through paybill (customer-initiated) or STK push (merchant-initiated). The Payments package handles initiation, intents and callbacks.
- Adapters are delivered in this order:
  1. **Simulated provider**, in `InstalmentFlow.Payments`. It supports paybill-style and push-style flows, with controllable faults:
     - duplicate callbacks;
     - delayed callbacks;
     - dropped callbacks;
     - out-of-order delivery;
     - timeout-then-success;
     - amount mismatch;
     - statement-only reversals.

     It is registered only explicitly. `InstalmentFlow.AspNetCore` refuses to map simulator endpoints in the `Production` environment unless the host overrides this.
  2. **M-Pesa:** C2B confirmation and validation, STK push and query, Transaction Status query, Pull Transactions and statement import for reconciliation. It does not initiate reversals or B2C (ADR-010).
  3. **Airtel Money:** USSD push collection, callback, enquiry. It does not initiate refunds or disbursements (ADR-010).
- Each real adapter milestone starts with a verification spike against the official provider documentation and sandbox. Unverified capabilities are marked as such in `docs/integrations/`.
