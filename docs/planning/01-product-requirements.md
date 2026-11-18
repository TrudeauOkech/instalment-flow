# Document 1 — Product Requirements

> **Product definition (ADR-001):** an open-source, multi-tenant .NET library for managing flexible loan repayments, partial payments, instalment schedules and payment allocation. It is the source of truth for payments, allocations and outstanding balances. It is not a complete loan management system, payment gateway, accounting platform or interest calculation engine, and it integrates with the lender's existing loan management system rather than replacing it.

## 1. Problem

Customers in markets such as Kenya often earn daily and irregularly, and pay through mobile money. Lenders and asset financiers increasingly accept **any amount, at any time**: KES 100 on Monday, 150 on Tuesday, nothing on Wednesday, 700 on Friday. Each of those receipts must be:

- **received exactly once**, even though providers send duplicate, delayed, missing or ambiguous notifications;
- **attributed** to the right customer, even when the account reference is mistyped and the phone number is masked;
- **applied** to the right obligation and component, in an order that follows the contract and the regulations;
- **reversible** without destroying history, even after the money has already settled an instalment;
- **explainable** later, so that "Why does this customer owe KES 340?" can be answered by tracing every shilling.

Most teams build this inside a CRUD loan table with a `balance` column that gets updated. That is where duplicate credits, lost updates under concurrency, irreversible reversals and unexplainable balances come from. The logic is hard to get right and the same everywhere, which makes it a good candidate for shared infrastructure.

## 2. Prior art: what exists and what InstalmentFlow adds

| Project | What it is | Relevance | Gap that InstalmentFlow fills |
|---|---|---|---|
| **Apache Fineract** (Java) | A full core-banking platform. Since 1.9.0 it has an *Advanced Payment Allocation* strategy that orders past-due, due and in-advance instalments and principal, interest, fees and penalties per transaction type. | **The best prior art for allocation semantics.** We should study its rule model and its "reverse-replay" approach to back-dated transactions. | It is a whole platform (server, database schema, UI ecosystem), not an embeddable .NET library. It is heavy for a PAYG financier or payment processor. |
| **TigerBeetle** | A purpose-built financial transactions database with an official .NET client. Its docs list Linux as the only supported production platform. | A possible future high-throughput journal backend. | It has accounts and transfers only, with no obligations, schedules, allocation or provider handling. |
| **Formance Ledger, Blnk** | Open-source double-entry ledger services, accessed over HTTP. | Shows that a ledger alone is a solved category. | No obligation or instalment model, and a separate service to operate. |
| **M-Pesa SDKs for .NET** (several community packages, plus Safaricom's older `Mpesa.Net`) | Transport wrappers around Daraja. | We may reuse or learn from their request signing and models. | Transport only, with no idempotency, inbox, reconciliation or allocation. |
| **PAYG platforms** (commercial, e.g. Angaza; in-house at M-KOPA, Watu) | End-to-end PAYG systems. | Confirms the paid-through-date model. | Proprietary. |

**Positioning:** *"The correctness layer between mobile-money events and your loan book. Install a NuGet package instead of building a subledger."* My research did not find an existing embeddable .NET library combining obligations, allocation and payment lifecycle. That should be re-checked before launch (risk R-P1).

## 3. Target users

| Persona | Context | What they need from InstalmentFlow |
|---|---|---|
| **PAYG / asset-financing engineer** | Phones, solar, motorbikes; daily micro-payments through paybill | Paid-through date, flexible accumulation, reliable C2B ingestion, unidentified-payment handling |
| **Digital lender engineer** | Short-tenor loans; STK push repayment; multiple loans per customer | Multi-obligation allocation, fees-first strategies, reversal correctness |
| **Payment processor / aggregator** | Receives mobile money on behalf of many merchants or lenders | Multi-tenant support, normalised provider events, reconciliation |
| **Existing loan management system (LMS) maintainer** | A legacy loan system with fragile repayment code | Embeddable engine that keeps their database and domain; journal export |
| **Finance / operations staff** (indirect) | Reconciliation, disputes, audit | Statements, payment traces, as-of balances, discrepancy reports |

## 4. Use cases

1. **UC-01** Define an obligation (KES 15,000 phone financing, KES 1,500 deposit, daily KES 50 over 270 days) and generate its schedule.
2. **UC-02** Receive a confirmed payment idempotently and credit the customer's unapplied balance.
3. **UC-03** Auto-allocate a receipt across obligations and instalments using the configured strategy.
4. **UC-04** Accumulate several small payments towards one instalment, keeping a link from each receipt to each allocation.
5. **UC-05** Keep an overpayment as unapplied credit, and apply it later (for example, when the next instalment becomes due) or refund it.
6. **UC-06** Customer-directed allocation ("apply this to loan 2").
7. **UC-07** Reverse a payment, fully or partially, after allocation, restoring outstanding balances and re-opening instalments.
8. **UC-08** Record a refund of unapplied credit (requested, then confirmed or failed). The host executes the payout (ADR-010).
9. **UC-09** Post interest, a fee or a penalty computed by the host, or waive an amount, with a reason code (ADR-008).
10. **UC-10** Quote and execute an early settlement.
11. **UC-11** Query the statement, instalment status, arrears, days past due and paid-through date for an obligation, at any point in time.
12. **UC-12** Trace a payment: where did each shilling of receipt `QK7XYZ…` go?
13. **UC-13** Identify a payment by its loan reference. Handle a missing, invalid or ambiguous reference, or a multi-loan customer without a reference, by holding the funds (as unidentified or customer unapplied funds) and raising a payment exception. Ops then resolve it by assigning or allocating (ADR-009).
14. **UC-14** Ingest provider callbacks (M-Pesa C2B, STK, Airtel) through an idempotent inbox and normalise them.
15. **UC-15** Recover from missing callbacks through status queries and statement or pull reconciliation, and report discrepancies.
16. **UC-16** Export journal entries to an external general ledger.
17. **UC-17** Subscribe to domain events (InstalmentSettled, ObligationSettled, PaymentReversed…) through the outbox.
18. **UC-18** Operate several lenders (tenants) in one database. Each has its own currency, allocation rules, grace periods, reference format and provider accounts, and no tenant can see another's data (ADR-007).
19. **UC-19** Correct a mis-configured plan before any payment touches it (supersede), or cancel an obligation and its plan with explicit write-off, keeping every plan version (ADR-011).
20. **UC-20** Develop and test the whole flow against a simulated provider that can inject duplicates, delays, drops and timeouts (ADR-016).

## 5. Core capabilities

**A. Plans and obligations**
- Schedule generators: equal instalments (daily, weekly, monthly, every N days), deposit plus instalments, custom list, and remainder-on-last rounding.
- Business calendar: time zone (default `Africa/Nairobi`), with a hook for non-business-day shifting.
- Grace period per plan: an instalment is not *overdue* until `due + grace`.
- Derived status: `Upcoming`, `Due`, `PartiallyPaid`, `Paid`, `Overdue`, `Waived`.
- Paid-through date (PAYG view).

**B. Allocation**
- Built-in strategies:
  - `OldestDueFirst`
  - `CurrentDueFirst`
  - `PrincipalFirst`
  - `ChargesFirst` (penalty → fee → interest → principal, configurable order)
  - `Proportional`, using the largest-remainder method
  - `Directed`
- A composition model: *obligation order* × *instalment order* × *component order*, plus an explicit **future-instalment policy** (hold as credit, apply to the next instalment, or prepay principal).
- `IAllocationStrategy` for custom rules, registered by name and version.

**C. Identification, unapplied funds and exceptions**
- A unique payment reference per obligation, in a tenant-defined format.
- Identification rules that never allocate an ambiguous payment (ADR-009).
- Customer unapplied funds per currency; tenant unidentified funds.
- A payment exception queue with resolution commands.
- Refund lifecycle (recorded only).

**G. Tenancy**
- Tenant-scoped engine.
- Versioned per-tenant policy.
- Composite tenant keys and row-level security.
- Provider-account-to-tenant routing.

**D. Corrections**
- Full and partial reversal, chargeback-style deficit handling, waivers, adjustments, and journal-level compensation only (never edits).

**E. Integrity**
- Idempotency keys, optimistic concurrency, an append-only journal, `Verify()` invariant checks, and as-of reconstruction.

**F. Integration**
- Persistence port with in-memory and EF Core/PostgreSQL stores.
- Transactional outbox.
- Provider adapter SPI.
- M-Pesa and Airtel adapters.
- ASP.NET Core endpoints.
- Reconciliation engine.

## 6. Non-functional requirements

| ID | Requirement | Target |
|---|---|---|
| NFR-1 Correctness | All invariants in doc 02 §7 hold under property-based and concurrency tests | 0 violations across ≥ 10⁶ generated cases per CI nightly run |
| NFR-2 Idempotency | Any command or callback can be replayed any number of times with the same outcome | Covered by tests for every command |
| NFR-3 Arithmetic | No `double`/`float` anywhere. Amounts use `decimal`, with scale validated against the currency's minor units | Roslyn analyzer bans float in monetary code |
| NFR-4 Performance | Receive plus auto-allocate on PostgreSQL, for an account with 5 obligations × 365 instalments | p95 < 25 ms on a single node; ≥ 500 payments/s sustained on modest hardware (benchmark-verified, not promised up front) |
| NFR-5 Auditability | Every balance is explainable by journal lines with source references; state can be rebuilt as of any instant | A `Verify` job passes on production-sized data |
| NFR-6 Dependencies | The core has no third-party package dependencies | Enforced by a build check |
| NFR-7 Portability | The core runs without ASP.NET Core, EF Core, PostgreSQL or a message broker | In-memory sample runs in a console app |
| NFR-8 Security | No secrets in logs; callback authenticity checked as far as each provider allows; personal data (PII) minimised | Security review before 1.0 |
| NFR-9 Observability | OpenTelemetry `ActivitySource` and `Meter`; structured logs with correlation IDs | Metrics catalogue documented |
| NFR-10 Compatibility | SemVer; public API tracked with `PublicApiAnalyzers`; database migrations forward-only | API diff checked in CI |
| NFR-11 Determinism | The same inputs give the same allocation, with explicit tie-breakers and no reliance on dictionary ordering | Property test |

## 7. MVP scope and exclusions

See [overview §3](00-overview.md#3-mvp-path-to-10) and the ADRs. The rationale for each exclusion:

- **Payment distribution** (dividing collections between financier, dealer and insurer): a separate capability. The 1.0 data model keeps the component and charge code on every allocation so distribution can be added later (ADR-012).
- **Provider-initiated refunds and reversals:** recorded only in 1.0 (ADR-010).

- **Interest engine:** regulatory variance (CBK Digital Credit Provider regulations, the in-duplum rule, Consumer Protection Act disclosure), and accounting-standard implications (IFRS 9 effective interest rate). Getting it wrong is worse than not having it.
- **Automatic penalties:** these need a time-driven job and contract rules, and they are legally sensitive. The host can post them in the MVP.
- **Restructures:** these change the schedule while allocations exist, which needs a versioned plan plus re-allocation semantics. Plan the data model for it now (a plan version column), and build it in 1.1.
- **Payouts and B2C:** separate products with separate approvals and higher fraud risk.
- **Machine learning:** see §8.

## 8. Future: analytics and machine learning (kept separate)

The journal and receipt history are a clean, typed behavioural dataset. Future **optional, read-only** packages could add:

- **Repayment behaviour features:** payment frequency, mean gap, amount variance, weekday seasonality, streaks. Exposed as a feature-extraction API over the journal.
- **Adaptive repayment recommendations:** suggesting schedule frequency or amount for *new* plans, based on cohort behaviour. The output is a *proposal* that the host must accept as a normal plan.
- **Delinquency prediction and collections prioritisation:** scores consumed by the host's collections tooling.

**Hard rule:** no model output ever changes an allocation, balance or journal entry directly. Analytics read projections; deterministic accounting writes them. Packaged as `InstalmentFlow.Analytics` (post-1.0), with no dependency from core.
