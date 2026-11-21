# The simulated provider

`SimulatedProvider` (in `InstalmentFlow.Payments.Simulation`) is a payment provider that runs in memory (ADR-016). It
supports everything a real adapter can:
- paybill-style payments the customer starts;
- push collections the merchant starts (like an STK push);
- status queries;
- a statement for reconciliation.

It signs its callbacks with HMAC-SHA256, so the inbox authenticates them the way it would a real provider's. Use it to
develop and test the whole flow without a sandbox. Register it explicitly; never in production.

```csharp
var clock = new FakeTimeProvider();                     // any TimeProvider (this one is Microsoft.Extensions.TimeProvider.Testing); share it with the engine
var sim = new SimulatedProvider("callback-secret", clock);
var flows = InstalmentFlowFactory.CreateInMemory(b => b.UseTimeProvider(clock).AddTenant("acme-ke", Currency.KES, "Africa/Nairobi"));
var payments = InstalmentFlowPayments.CreateInMemory(flows, p => p.AddProvider(sim).UseTimeProvider(clock));
sim.ConnectTo(payments.Inbox);
await payments.Routing.MapAsync("sim", "600123", "acme-ke");

sim.Pay(new SimulatedPayment { ProviderAccount = "600123", Amount = Money.Kes(300m), AccountReference = "LN-1" });
await sim.DeliverDueAsync();                            // callbacks arrive when you say so
await payments.Inbox.ProcessPendingAsync();             // recorded, identified and allocated by the engine
```

## Faults

Faults are set per transaction, so each test states exactly what goes wrong:

| Fault | Effect |
|---|---|
| `Duplicate` | The callback is delivered twice. |
| `CallbackDelay` (property) | The callback arrives later; `DeliverDueAsync` delivers it once the clock passes its time. |
| `Drop` | The callback is never delivered; the transaction is still on the statement. On a reversal, this is a statement-only reversal. |
| `Reorder` | The callback is held back and delivered after the next one, for example a reversal before its payment. |
| `TimeoutThenSuccess` | A push request throws a timeout, but the provider accepted it and the customer pays. |
| `AmountMismatch` | The statement shows a different amount than the callback (one major unit more, or `StatementAmount`). |

Push requests follow `NextPush(new SimulatedPush { ... })`: the customer approves (the default), declines, never
answers, or the provider rejects the request. `AnswerAfter`, `CallbackDelay` and `PaidAmount` shape the timing and
the amount actually paid.

`DeliverAllAsync` delivers everything waiting, ignoring delays and holds. `Sign` signs a body, for tests that replay or
forge callbacks.

## What it does not simulate

Provider-specific payloads, authentication schemes, rate limits and error codes. Those belong to each real adapter's
contract tests (M9 for M-Pesa, M11 for Airtel Money).
