# ADR-012: Allocation and distribution are separate capabilities

- Status: Accepted (2026-11-18)

## Decision
- **Allocation** (1.0) applies a customer's funds to *their* obligation components.
- **Distribution** (post-1.0) divides *collected* funds between beneficiaries (financier, dealer, insurer, service provider) according to rules. It never initiates bank or mobile-money transfers itself. Executing transfers would be a separate integration.
- Constraints that 1.0 must satisfy so that distribution can be added later **without changing the payment model**:
  1. Every allocation line carries tenant, obligation, plan version, instalment, component, **charge code**, lot and payment. That is enough to derive "insurance premium collected" or "dealer commission collected".
  2. Clearing accounts are per provider account (`clearing:mpesa:600123`).
  3. Distribution will be a consumer of allocation facts and events. It will post its own journal transactions, moving `collected:{component}` to `payable:{beneficiary}`, in a separate module. Core does not reference it.

## Consequences
- 1.0 contains no beneficiary concepts. The charge-code field on fees is kept specifically so future distribution rules have something to key on.
