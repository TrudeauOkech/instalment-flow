# ADR-009: Payment identification, unapplied funds and the exception queue

- Status: Accepted (2026-11-18)

## Context
Customers pay by paybill with a typed account reference, which can be wrong, or through STK push, where the host knows the loan. Daraja C2B v2 masks the MSISDN, so matching by phone number is unreliable. Ambiguous payments must never be allocated to an arbitrary loan.

## Decision
- Each obligation has a **payment reference**: unique per tenant, in a tenant-configured format (e.g. `LN-100001`), and normalised before matching (trimmed, upper-cased, spaces and dashes handled by the format rules).
- Identification runs **before** allocation and produces a `PaymentIdentification`. The host can extend it through `IPaymentReferenceResolver`, for example to look up legacy references. The table shows each outcome.

| # | Situation | Where funds land | Allocation | Exception raised |
|---|---|---|---|---|
| 1 | Reference exactly matches one obligation (paybill), or the host supplied the obligation (STK) | That customer's unapplied funds | **Directed** to that obligation, using the tenant strategy *within* it. Any amount beyond what is payable stays unapplied, handled by the future-instalment policy. **Never spills onto another loan.** | none |
| 2 | Customer known, no obligation given, customer has **one** eligible obligation | Customer unapplied | If tenant policy `SoleObligation = AutoAllocate`, allocate to it. Otherwise hold. | none, or `AllocationInstructionRequired` when holding |
| 3 | Customer known, no obligation given, customer has **several** eligible obligations | Customer unapplied | **Hold**. Cross-loan allocation happens only if the tenant has explicitly configured `MultipleObligations = AllocateByStrategy` (for lenders whose contracts allow it). The default is hold. | `AllocationInstructionRequired` |
| 4 | Reference missing or invalid (no match) | **Tenant unidentified funds** (customer unknown) | none | `UnidentifiedPayment` |
| 5 | Reference ambiguous (matches more than one obligation or customer) | Customer unapplied if all candidates belong to one customer, otherwise tenant unidentified funds | none | `AmbiguousReference`, with candidates |
| 6 | Reference matches an obligation that is settled, closed or cancelled | Customer unapplied | none | `ObligationNotPayable` |
| 7 | Currency differs from the obligation or tenant | Tenant unidentified funds (in the payment's currency) | none | `CurrencyMismatch` |

- **Exception queue:** each `PaymentException` records the payment, the reason, the candidates, its status (Open, Resolved or Dismissed), who resolved it, when, and the resolution. Resolution is **a normal journal command**:
  - `AssignToCustomer` moves funds from unidentified to the customer's unapplied funds;
  - `AllocateToObligation` is a directed allocation;
  - `MarkForRefund` requests a refund (ADR-010).

  Wrong resolutions are corrected by compensating commands, never by edits.
- Manual allocation of any unapplied funds is always available: `Allocations.AllocateAsync` with a directive.

## Consequences
- Every confirmed payment is recorded (ADR-002), and no ambiguous payment is allocated silently.
- Operations staff need a work queue. The library exposes queries and commands for it; the UI is the host's responsibility.
- Exception metrics (count and age by reason) are part of the health checks.
