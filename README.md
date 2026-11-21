# InstalmentFlow

**An open-source, multi-tenant .NET library for managing flexible loan repayments, partial payments, instalment schedules and payment allocation.**

Customers in markets such as Kenya often pay small, irregular amounts through mobile money: KES 100 on Monday, 150 on Tuesday, 700 on Friday. InstalmentFlow records every confirmed payment exactly once and identifies which loan it belongs to. It allocates the money according to the lender's rules and keeps an immutable, double-entry history that can explain every shilling, including after reversals.

> **Status: 0.1 preview.** The core engine works end to end with the in-memory store: identification, allocation, reversals, charges, waivers, refunds, cancellation, as-of history and verification. PostgreSQL (0.2) and the M-Pesa and Airtel Money adapters (0.3, 0.4) come next; the API may still change before 1.0. See the [roadmap](docs/planning/06-implementation-roadmap.md).

## Quickstart

```csharp
var flow = InstalmentFlowFactory.CreateInMemory(b => b.AddTenant("acme-ke", Currency.KES, "Africa/Nairobi"))
    .ForTenant("acme-ke");

var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct-C-1001", ExternalId = "C-1001" });
await flow.Obligations.OpenAsync(new OpenObligationRequest
{
    IdempotencyKey = "loan-L-2041", Account = customer.AccountId, ExternalId = "L-2041", PaymentReference = "LN-100001",
    Components = [Component.Principal(Money.Kes(3_000m))],
    Plan = PlanSpec.Equal(Frequency.Monthly, 3, firstDue: new DateOnly(2026, 12, 1)),
});

var paid = await flow.Payments.RecordAsync(new RecordPaymentRequest
{
    IdempotencyKey = "mpesa:600123:TJ3K9XYZ", Source = new ExternalReference("mpesa:600123", "TJ3K9XYZ"),
    Amount = Money.Kes(1_200m), ValueAt = DateTimeOffset.UtcNow, Target = PaymentTarget.ForReference("LN-100001"),
});
// On or after 1 December: PartiallyAllocated, KES 1,000 to the instalment due and KES 200 held as unapplied funds.
```

Read the [concepts](docs/concepts.md), the [allocation strategies](docs/allocation-strategies/README.md) and the [financial invariants](docs/financial-invariants.md), and run the [samples](samples/README.md). The design is in the [planning documents](docs/planning/00-overview.md) and the [architecture decisions](docs/decisions/README.md).

## What it does

- **Flexible instalments:** daily, weekly, monthly and custom schedules; partial payments; grace periods; a paid-through date for pay-as-you-go asset financing.
- **Payment identification:** loan references, unapplied funds, and an exception queue. Ambiguous payments are never silently allocated, and a confirmed payment is never lost.
- **Allocation:** oldest-due-first, current-first, principal-first, charges-first, proportional, directed, or your own strategy.
- **Corrections without rewriting history:** full and partial reversals, waivers and refunds, all handled with compensating entries.
- **Multi-tenant:** many lenders in one database, isolated by composite keys and PostgreSQL row-level security.
- **Provider-agnostic:** the core knows nothing about M-Pesa or Airtel Money. Optional adapters normalise their callbacks. A simulator lets you test duplicates, delays and timeouts.

## What it is not

It is not a complete loan management system, a payment gateway, an accounting platform, or an interest or penalty calculation engine. It integrates with your existing loan system and becomes the source of truth for payments, allocations and outstanding balances ([ADR-001](docs/decisions/ADR-001-product-scope.md)). It is a tool, not legal or regulatory advice.

## Packages

| Package | Purpose |
|---|---|
| `InstalmentFlow` | Core engine and in-memory store. Zero third-party dependencies. (0.1 preview) |
| `InstalmentFlow.EntityFrameworkCore` | PostgreSQL persistence, row-level security, transactional outbox |
| `InstalmentFlow.Payments` | Provider SPI, inbox, payment intents, reconciliation, simulator |
| `InstalmentFlow.Payments.Mpesa` | Safaricom M-Pesa (Daraja) adapter |
| `InstalmentFlow.Payments.AirtelMoney` | Airtel Money adapter |
| `InstalmentFlow.AspNetCore` | Callback endpoints, tenant context, health checks |
| `InstalmentFlow.Testing` | Scenario DSL and test helpers for your own test suite (0.1 preview) |

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet build -c Release
dotnet test -c Release
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Changes that affect money movement follow the financial change checklist there.

## Licence

[Apache-2.0](LICENSE).
