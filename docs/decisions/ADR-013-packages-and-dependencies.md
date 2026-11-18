# ADR-013: Packages and dependency rules

- Status: Accepted (2026-11-18)

## Decision
These seven packages version together (lock-step):

| Package | Contents |
|---|---|
| `InstalmentFlow` | Core: domain, allocation, journal, engine, queries, in-memory store, ports |
| `InstalmentFlow.EntityFrameworkCore` | PostgreSQL: mappings, RLS, triggers, outbox, inbox and exception store |
| `InstalmentFlow.Payments` | Adapter SPI, inbox, intents, identification pipeline, reconciliation, **simulated provider** |
| `InstalmentFlow.Payments.Mpesa` | M-Pesa adapter |
| `InstalmentFlow.Payments.AirtelMoney` | Airtel Money adapter |
| `InstalmentFlow.AspNetCore` | Callback endpoints, tenant context, health checks |
| `InstalmentFlow.Testing` | Scenario DSL, fake clock, payload builders, invariant assertions |

- Core depends only on the BCL and `Microsoft.Extensions.*.Abstractions`. Optional packages may use only Microsoft libraries or MIT/Apache-2.0 libraries. No MediatR, AutoMapper or MassTransit (all moved to commercial licensing in 2025–26).
- Architecture tests in CI enforce these dependency rules.
