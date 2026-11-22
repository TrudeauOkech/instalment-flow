# Compliance notes

**This is not legal advice.** InstalmentFlow is a software library. Licensing, consumer protection and data
protection obligations belong to the lender running it (R-S5). These notes say what the library does and does not do,
so that your own advisers can assess it.

## Data protection (Kenya Data Protection Act 2019, and similar laws)

- **You are the data controller.** InstalmentFlow processes personal data on your behalf, in your database.
- **What it stores:**
  - customer external IDs and payment references you choose;
  - payer identifiers that providers send (M-Pesa may send a masked or hashed MSISDN, Airtel the number);
  - raw provider callbacks in the inbox, which can contain names and phone numbers.
- **What it avoids:**
  - payer names are dropped when callbacks are normalised;
  - telemetry carries no personal data;
  - the core writes no logs.
- **Your choices:**
  - encrypt raw payloads at rest (`IPayloadProtector`);
  - set a retention period for the inbox;
  - restrict database access;
  - handle data-subject requests. The ledger is append-only for auditability, so erase or pseudonymise personal
    data in your own customer records, not by editing financial facts.

## Lending regulation

- **No interest or penalty engine** (ADR-008). Interest and charges are posted by your system, which owns the rules:
  for example caps such as the in duplum rule, disclosure, and the Central Bank of Kenya's digital credit provider
  regulations. InstalmentFlow records them and allocates payments to them.
- **Allocation order is a policy decision.** Some regimes require payments to go to particular components first.
  Choose and document the tenant's strategy (see [allocation strategies](allocation-strategies/README.md)). Every
  allocation records the strategy and policy version used.
- **Customer statements and payment traces** (`GetStatementAsync`, `TracePaymentAsync`) explain every shilling, for
  disputes and complaints.

## Audit

- **Append-only journal:** enforced by the database, even for the schema owner.
- **History:** as-of queries reconstruct any past state.
- **`Verify`:** checks the ledger's invariants on demand.
- **Decisions are recorded:** with who made them (`ResolvedBy`), reasons, and the policy versions used.
