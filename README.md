# InstalmentFlow

**An open-source, multi-tenant .NET library for managing flexible loan repayments, partial payments, instalment schedules and payment allocation.**

Customers in markets such as Kenya often pay small, irregular amounts through mobile money: KES 100 on Monday, 150 on Tuesday, 700 on Friday. InstalmentFlow records every confirmed payment exactly once and identifies which loan it belongs to. It allocates the money according to the lender's rules and keeps an immutable, double-entry history that can explain every shilling, including after reversals.

> **Status: pre-alpha (milestone M0, repository foundation).** There is no usable API yet. The design is complete: see the [planning documents](docs/planning/00-overview.md) and the [architecture decisions](docs/decisions/README.md).

## What it does

- **Flexible instalments:** daily, weekly, monthly and custom schedules; partial payments; grace periods; a paid-through date for pay-as-you-go asset financing.
- **Payment identification:** loan references, unapplied funds, and an exception queue. Ambiguous payments are never silently allocated, and a confirmed payment is never lost.
- **Allocation:** oldest-due-first, current-first, principal-first, charges-first, proportional, directed, or your own strategy.
- **Corrections without rewriting history:** full and partial reversals, waivers and refunds, all handled with compensating entries.
- **Multi-tenant:** many lenders in one database, isolated by composite keys and PostgreSQL row-level security.
- **Provider-agnostic:** the core knows nothing about M-Pesa or Airtel Money. Optional adapters normalise their callbacks. A simulator lets you test duplicates, delays and timeouts.

## What it is not

It is not a complete loan management system, a payment gateway, an accounting platform, or an interest or penalty calculation engine. It integrates with your existing loan system and becomes the source of truth for payments, allocations and outstanding balances ([ADR-001](docs/decisions/ADR-001-product-scope.md)). It is a tool, not legal or regulatory advice.

## Packages (planned)

| Package | Purpose |
|---|---|
| `InstalmentFlow` | Core engine and in-memory store. Zero third-party dependencies. |
| `InstalmentFlow.EntityFrameworkCore` | PostgreSQL persistence, row-level security, transactional outbox |
| `InstalmentFlow.Payments` | Provider SPI, inbox, payment intents, reconciliation, simulator |
| `InstalmentFlow.Payments.Mpesa` | Safaricom M-Pesa (Daraja) adapter |
| `InstalmentFlow.Payments.AirtelMoney` | Airtel Money adapter |
| `InstalmentFlow.AspNetCore` | Callback endpoints, tenant context, health checks |
| `InstalmentFlow.Testing` | Scenario DSL and test helpers for your own test suite |

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
