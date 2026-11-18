# Document 7 — Risk and Failure Analysis

Likelihood (L) and impact (I) are rated H, M or L.

## 1. Financial correctness risks

| ID | Risk | L | I | Impact | Mitigation |
|---|---|---|---|---|---|
| R-F1 | Duplicate crediting from repeated callbacks or reconciliation re-ingestion | H | H | Customer credited twice; loss | Inbox unique key, payment `(tenant, source, ref)` unique, idempotency key with payload hash, and a test for each path |
| R-F2 | Lost update under concurrent payments for one account | M | H | Over-settlement or credit lost | Account version check with retry; `check` constraints on projections; parallel property tests |
| R-F3 | Reversal not unwinding correctly (wrong instalment re-opened, credit leaks) | M | H | Wrong arrears, disputes | Lot-based unwinding; property I-9; golden tables |
| R-F4 | Rounding drift in proportional allocation or schedule generation | M | M | Off-by-a-cent balances that never settle | Largest-remainder method; remainder on last instalment; sum properties |
| R-F5 | Projection diverges from journal (bug or manual SQL) | L | H | Unexplainable balances | Append-only triggers, role grants, scheduled `Verify` with an alert metric, and a `Rebuild` tool |
| R-F6 | Custom strategy bug corrupts state | M | H | Over-allocation | Engine-side plan validation; strategies are pure and cannot write |
| R-F7 | Built-in strategy behaviour change silently alters existing tenants | L | H | Contract breach | Strategy versioning; tenants pin a version |
| R-F8 | Time-zone errors (payment at 23:59 counted the next day; late flags wrong) | M | M | Wrong penalties and arrears | `TenantClock`; adapters convert provider-local time; boundary tests |
| R-F9 | Back-dated payment processed after a later one causes the wrong lateness and penalty | M | M | Customer disputes | Record `value_at`; on-time logic uses it; replay mode post-1.0 |
| R-F10 | Reversal after refund leaves money unrecoverable | L | M | Loss | Explicit reversal-receivable account, event, and report |
| R-F11 | Allocation order contrary to contract or regulation (fees-first where prohibited) | M | H | Regulatory sanction | Strategy is configured per tenant; docs warn; the library never chooses a fees-first default |
| R-F12 | An ambiguous or unreferenced payment is allocated to the wrong loan | M | H | Customer harm, disputes | ADR-009: hold plus exception by default; directed allocations never spill over (I-21); properties I-20 and I-21 |
| R-F13 | Exception queue grows unattended, so funds sit unapplied and customers appear in arrears | H | M | Wrong collections actions, complaints | Open-exception metrics by reason and age; health check thresholds; `AllocationInstructionRequired` events so the host can prompt the customer; ops runbook |
| R-F14 | A tenant policy change alters behaviour retroactively | L | M | Unexplainable history | Policy is versioned; each allocation stores its policy version; changes apply going forward only |

## 2. Provider and integration risks

| ID | Risk | L | I | Mitigation |
|---|---|---|---|---|
| R-I1 | Missing callbacks (endpoint down, provider glitch) | H | H | Inbox plus 200-after-commit; hourly Pull Transactions (48 h window, so the cadence must stay well inside it); daily statement import |
| R-I2 | STK timeout but the customer was debited | H | M | Ambiguous intent state; status query; never auto-retry; duplicate pay becomes credit |
| R-I3 | Forged callbacks (Daraja documents no signature) | M | H | Path secret, IP allowlist, TLS; optional high-value verification by Transaction Status before allocation (`RequireVerificationAbove`); reconciliation catches the rest |
| R-I4 | Wrong account reference / masked MSISDN prevents matching | H | M | Unidentified funds plus exception queue (ADR-009), fuzzy-match hints in the resolver (exposed, never automatic), ops assignment command |
| R-I5 | Provider API changes or deprecation (C2B v1 → v2, Airtel v1 → v2) | M | M | Adapter versioning; contract fixtures; capability flags; verification spike per milestone |
| R-I6 | Our documentation of provider capabilities is wrong | M | M | Mark verified vs to-verify; maintainers confirm against sandbox before release |
| R-I7 | Out-of-order events (reversal before payment) | L | M | Pending-reversal parking keyed by external reference |
| R-I8 | Validation endpoint is slow, so M-Pesa applies the `ResponseType` default | M | M | Internal budget under 8 s; read-only reference check; recommend `Completed` so money is not refused |
| R-I10 | Provider account routed to the wrong tenant (misconfigured shortcode mapping) | L | H | A unique `(provider, provider_account)` constraint; changes to the mapping are audited; reconciliation per tenant flags foreign transactions |
| R-I9 | Sandbox ≠ production behaviour | H | M | Pilot phase; production-recorded (redacted) fixtures contributed by adopters |

## 3. Technical risks

| ID | Risk | Mitigation |
|---|---|---|
| R-T1 | Hot aggregate (unidentified funds, or a business customer with many payments) causes retry storms | Per-provider-account unidentified-funds system accounts; pessimistic mode option; metric on retries |
| R-T2 | Ledger table growth | Partitioning from 0.2; archival of partitions without deletion |
| R-T3 | Over-engineering (abstractions nobody uses) | Seven packages, no messaging layer, and no feature without an adopter or use case |
| R-T4 | EF Core coupling makes hosts adopt our `DbContext` | Separate schema; support host-owned transactions; raw SQL where clearer |
| R-T5 | Licence changes in dependencies (as with MediatR and MassTransit v9) | Core has zero dependencies; optional packages use Microsoft or MIT/Apache libraries only |
| R-T6 | Outbox dispatch stalls | Lag metric, health check, dead-letter state, and an ops runbook |

## 4. Security and compliance risks

| ID | Risk | Mitigation |
|---|---|---|
| R-S1 | Credential leakage (Daraja consumer secret, Initiator password, Airtel keys) | Options binding from secret stores; never logged; redaction tests |
| R-S2 | PII in logs or raw payloads (names, MSISDNs). Kenya Data Protection Act 2019 obligations fall on the host as data controller. | Minimal persisted PII; `IPayloadProtector`; configurable retention; docs section on DPA responsibilities |
| R-S3 | Insider tampering with balances | Append-only DB; separate migration role; `Verify`; journal hash chain (optional, post-1.0: each tx stores a hash of the previous one for tamper evidence) |
| R-S4 | Supply-chain attack on our packages | Signed packages, NuGet trusted publishing from CI, 2FA, SBOM, Dependabot, reproducible builds |
| R-S6 | **Cross-tenant data leakage** (a missing filter, connection-pool leakage of the tenant setting, a worker processing the wrong tenant) | Composite foreign keys; RLS with `FORCE` and fail-closed `current_setting`; `SET LOCAL` (transaction-scoped); no tenant-less engine; a cross-tenant test suite in CI; a separate narrow role for cross-tenant workers |
| R-S5 | Hosts treat the library as regulatory compliance | README and docs disclaimers; the library is a tool, not legal advice; licensing and regulation are the host's responsibility |

## 5. Product and operational risks

| ID | Risk | Mitigation |
|---|---|---|
| R-P1 | An equivalent project already exists | Re-survey GitHub and NuGet before launch; if one exists, consider contributing to it instead |
| R-P2 | No adopters (generic infrastructure with no champion) | Find one pilot (Q3) before M7; build samples from their real flows |
| R-P3 | Maintainer bus factor | Thorough ADRs and invariants docs; recruit a second maintainer by 1.0 |
| R-P4 | Scope creep into a full LMS | The vision statement in README; an "out of scope" list; ADRs for any expansion |

## 6. Hard scenario catalogue (beyond the four in the brief)

Each scenario becomes an executable spec in `tests/` and a page in `docs/failure-scenarios/`.

1. **Duplicate with different amount:** the same `TransID` arrives with 500 and then 5000. The second is rejected with `IF1002` and an alert. The first stands.
2. **Concurrent duplicate callbacks:** two identical confirmations arrive in parallel. Both miss the idempotency cache, one wins the unique index, and the other returns `Duplicate`.
3. **Retry after commit, before response:** the host times out and resubmits. The same result is returned.
4. **STK timeout, then success, then the customer pays again manually:** one intent is confirmed and one C2B receipt lands. Both are recorded, and the second becomes credit.
5. **Partial reversal of a payment split across 2 obligations:** reverse 300 of 500 (where 200 went to L1 and 300 to L2). Allocations are unwound LIFO, so L2 is re-opened by 300 and L1 is untouched. Check the documented order.
6. **Reverse a payment whose credit funded a later allocation after the instalment was waived:** the waiver stays, and the unwind skips waived amounts.
7. **Reversal notification arrives twice:** the second is ignored through `provider_ref`.
8. **Reversal arrives before the payment** (reconciliation imports a reversal line first): it is parked, applied on arrival, and the net effect is zero.
9. **Reversal after refund:** a deficit is recorded in reversal-receivable.
10. **Payment to a closed or settled obligation:** it is held as customer unapplied funds and raises `ObligationNotPayable`. The obligation never goes negative.
11. **Payment exactly at a grace boundary** (23:59:59 EAT on the last grace day, delivered 00:00:05): it counts as on time, using `value_at`.
12. **Monthly schedule starting 31 Jan:** due dates fall on 28/29 Feb, 31 Mar and 30 Apr.
13. **Proportional allocation of KES 100 over three equal obligations:** 33.34, 33.33, 33.33 using deterministic tie-breaking. (For whole-shilling tenants: 34, 33, 33.)
14. **Fractional M-Pesa amount** (malformed payload, 100.5): the adapter rejects it to the inbox error state. It is never rounded.
15. **Currency mismatch** (a USD payment to a KES account): recorded as unidentified funds in USD with a `CurrencyMismatch` exception. It is never lost and never allocated.
16. **Unknown account reference, then ops assigns it to the wrong customer, then corrects it:** reassignment is a reversal plus a new assignment. Full history remains.
17. **Early settlement quote, then a payment arrives before settlement:** the quote is invalidated by the account version. A re-quote is required.
18. **Crash between inbox insert and processing:** the processor picks up the row on restart, with exactly one payment.
19. **Outbox publisher fails for an hour:** events are delivered later, in per-account order. No event is lost.
20. **Long history:** 3 years of daily micro-payments (about 2,000 payments, 1,100 instalments). Load and allocation stay within latency, thanks to open-only loading.
21. **Strategy version change:** a tenant pinned to `OldestDueFirst@1` is unaffected when `@2` ships.
22. **Manual SQL tamper:** an UPDATE on `ledger_entries` is rejected. A direct projection update is detected by `Verify`.
23. **Customer with two loans pays by STK push for loan A and by paybill with no reference:** the STK payment is directed to A. The paybill payment is held with `AllocationInstructionRequired`. Neither touches loan B.
24. **Exact reference, overpayment beyond the loan's total outstanding:** the loan is settled and the remainder stays as customer unapplied funds. Another open loan is **not** credited.
25. **The same payment reference exists in two tenants,** and a callback arrives on tenant A's shortcode: it is identified within tenant A only.
26. **Tenant policy changes from `SoleObligation = AutoAllocate` to `Hold` mid-day:** payments before the change keep policy v1 behaviour, payments after use v2, and every allocation records its policy version.
27. **Plan supersede attempted after one KES 50 payment:** rejected with `IF4001`. Cancel or restructure (post-1.0) is the only route.
28. **Obligation cancelled with outstanding principal:** unpaid instalments are voided, write-off lines are posted with a reason, prior allocations remain, and customer unapplied funds are untouched.
29. **Simulator drops a callback, then reconciliation imports the statement line:** the payment is recorded once and identified normally.

## 7. Testing strategy

| Layer | Tooling | What it proves |
|---|---|---|
| Unit | xUnit v3 | Value objects, generators, each strategy's tables |
| Property-based | CsCheck | Invariants I-1…I-17 over random command histories; determinism; rounding |
| Model-based concurrency | CsCheck `SampleParallel` on the in-memory store | Linearizability of commands for one account |
| Database integration | Testcontainers PostgreSQL | Mappings, triggers, unique constraints, outbox, migrations |
| Concurrency (real DB) | Parallel tasks + `Verify` | No lost updates or duplicates under contention |
| Failure injection | Interceptors that throw at chosen points (before or after commit, mid-dispatch); `Toxiproxy` for network faults | Exactly-once effect after crashes and retries |
| Contract | WireMock.Net with recorded Daraja and Airtel payloads; schema snapshot tests | Adapters parse real payloads; breaking provider changes are detected |
| End-to-end | `WebApplicationFactory` with sample hosts | Callback → inbox → engine → outbox |
| Mutation | Stryker.NET (Money, Allocation, Reversal) | Tests actually catch bugs |
| Benchmarks | BenchmarkDotNet, tracked in CI (regressions over 10% fail the nightly run) | Hot path performance |
| Load | k6 against the reference host + PostgreSQL | Throughput, p95, retry rate, outbox lag |
| Nightly soak | 10⁶ random operations with `Verify` | Long-tail invariant violations |
