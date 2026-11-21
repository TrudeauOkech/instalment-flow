# Concepts

InstalmentFlow is the source of truth for **what a customer paid, where each payment went, and what is still
owed** on loans and financing agreements. It is not a loan management system: the host keeps the loan record,
pricing and collections, and drives InstalmentFlow through commands and queries. This page explains the model in
the order money moves through it. The full specification is in [doc 02](planning/02-domain-and-financial-model.md).

## Tenants

Every engine instance is scoped to one tenant (`factory.ForTenant("acme-ke")`) and can only see that tenant's
data (ADR-007). A tenant has a base currency, a time zone that defines its **business date**, and a versioned
**policy**: grace periods, the payment-reference format, the allocation strategy, what to do with unreferenced
payments, and so on. Changing the policy creates a new version. Commands record the version they used, so a policy
change never rewrites history.

## Customers, obligations and plans

A **customer account** holds a customer's obligations and funds. It is the consistency boundary: every command
that moves money changes exactly one account, atomically, with optimistic concurrency (ADR-003).

An **obligation** is what the customer owes under one agreement (a loan, a phone, a school-fees plan), broken into
**components**: principal, interest, fees and penalties, each optionally with a charge code. The host calculates
every amount. InstalmentFlow never calculates interest or penalties (ADR-008); later charges are posted with
`Charges.PostAsync`. Each obligation has a unique **payment reference** that customers quote when paying.

A **repayment plan** spreads the amounts over **instalments**. Plans are versioned. A plan can be replaced only
before any payment, waiver or charge has touched it, and every version is kept (ADR-011). An instalment's status
(paid, overdue, in grace) is never stored: it is derived from balances and a date, so it cannot go stale.

## Payments and funds

**Recording comes first, allocation second** (ADR-002). A confirmed payment is always recorded, whatever happens
next, as a **funds lot**: the payment's value, and where each part of it went (ADR-005).

**Identification** decides who holds the lot (ADR-009). A reference that matches one obligation directs the funds
to it. A known customer without a reference, or an ambiguous or unknown reference, is never guessed at: the funds
are held as the customer's **unapplied funds** or the tenant's **unidentified funds**, and a **payment exception**
asks an operator to resolve it.

## Allocation

**Allocation** applies held funds to instalment components. A versioned, pure **strategy** chooses the order, for
example oldest due first or proportional across loans (ADR-006; see [allocation strategies](allocation-strategies/)).
The engine validates every plan a strategy returns, so even a faulty custom strategy cannot over-settle anything or
pay an obligation it should not. Funds directed to a loan never spill onto another one. What is left after
everything due is paid stays as credit, or buys future instalments if the tenant chooses (pay-as-you-go).

## Corrections

History is never edited. Mistakes and real-world reversals are new facts:

- a **reversal** takes a payment back: first from its unapplied funds, then by unwinding its allocations, most
  recent first. Value that was already refunded becomes a **reversal receivable** the customer owes;
- a **charge** adds an amount the host calculated, and a **charge reversal** removes it;
- a **waiver** forgives part of an amount; **cancellation** writes off what is outstanding, with explicit lines;
- a **refund** returns unapplied funds. It is recorded only (ADR-010): the host pays it out and reports the result.

## The ledger

Every money-moving command posts one balanced, append-only **journal transaction** in a double-entry subledger
(ADR-004): clearing accounts per provider account, unapplied and unidentified funds, receivables per obligation
component, refunds payable and reversal receivables. `Queries.ReadJournalAsync` exports it incrementally with a
cursor, so the host's general ledger can map these accounts to its own.

## History and verification

Because every fact is kept and linked to its journal transaction, any past state can be **reconstructed**:
`Queries.GetObligationAsync(loan, AsOf.Date(day))` answers "what did this loan look like at the end of that day",
and later commands never change the answer. `GetStatementAsync` gives a customer statement with a running
balance, and `TracePaymentAsync` shows where every part of one payment went.

`Queries.VerifyAsync` checks the [financial invariants](financial-invariants.md) against stored data. It folds the
journal and the facts independently of the code that wrote them and reports any disagreement.

## Testing your integration

`InstalmentFlow.Testing` provides a scenario DSL on an in-memory engine:

```csharp
var run = await Scenario.InMemory()
    .Given(a => a.Customer("C1").Loan("LN-100001", Plan.Daily(500m, count: 30)))
    .When(a => a.PaybillPaymentDelivered(times: 5, 500m, billRef: "LN-100001", transId: "TX1"))
    .Then(a => a.Loan("LN-100001").Outstanding.ShouldBe(Money.Kes(14_500m)))
    .RunAsync();
await run.ShouldSatisfyAllInvariantsAsync();
```

See the [samples](../samples/) for runnable programs.
