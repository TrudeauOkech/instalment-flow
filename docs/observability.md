# Observability

InstalmentFlow emits OpenTelemetry traces and metrics through the .NET base library (`System.Diagnostics`), so the core
needs no extra dependency (NFR-6, NFR-9). Every package emits under one name:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(InstalmentFlowTelemetry.SourceName))
    .WithMetrics(m => m.AddMeter(InstalmentFlowTelemetry.MeterName));
```

**No personal data.** Attributes carry tenants, providers, commands, outcomes, statuses and error codes. They never carry
payers, phone numbers, receipts, payment references or account numbers. Amounts appear only as a sum per currency.

## Traces

| Span | Emitted by | Attributes |
|---|---|---|
| `instalmentflow.command {CommandType}`, for example `instalmentflow.command RecordPayment` | Every engine command | `instalmentflow.tenant`, `.command`, `.attempts`, `.outcome` (`committed`, `replayed`, `failed`), `.error_code`; `.payment_outcome` on payments. Status `Error` on failure |
| `instalmentflow.inbox.receive` | `Inbox.ReceiveAsync` | `instalmentflow.provider`, `.callback_kind`, `.outcome` (`Stored`, `Duplicate`, `Rejected`, `Empty`) |
| `instalmentflow.inbox.process` | Each inbox entry processed | `instalmentflow.provider`, `.event_kind`, `.status` (`Processed`, `Pending` for a retry, `Failed`), `.attempts` |
| `instalmentflow.provider.status_query` | The intent resolver's status queries | `instalmentflow.provider`, `.status` |
| `instalmentflow.reconciliation.run` | `Reconciliation.RunAsync` | `instalmentflow.provider`, `.lines`, `.discrepancies` |
| `instalmentflow.outbox.publish` (producer) | `OutboxDispatcher` | `instalmentflow.event_type`, `.tenant` |

Engine commands run inside the caller's span (for example an ASP.NET Core request), so a callback's trace shows the
receive, the processing and the command it caused.

## Metrics

| Metric | Type | Unit | Attributes | Use |
|---|---|---|---|---|
| `instalmentflow.command.duration` | histogram | µs | `command`, `outcome` | Latency per command (NFR-4) |
| `instalmentflow.command.retries` | counter | | `command` | Optimistic-concurrency contention; consider pessimistic mode if it climbs |
| `instalmentflow.payments.recorded` | counter | | `provider`, `outcome` | Payments by outcome: allocated, held, unidentified, duplicate… |
| `instalmentflow.payments.amount` | counter | currency units | `currency` | Money received (duplicates excluded) |
| `instalmentflow.verify.violations` | histogram | | | Alert on any value above zero |
| `instalmentflow.inbox.received` | counter | | `provider`, `outcome` (`Stored`, `Duplicate`, `Held`, `Rejected`) | Callback volume; rejected spikes may be forgery attempts |
| `instalmentflow.inbox.processed` | counter | | `provider`, `status` (`Processed`, `Retrying`, `Failed`) | Failed entries need a person |
| `instalmentflow.intents` | counter | | `provider`, `status` | Intent transitions; watch `Ambiguous` and `Expired` |
| `instalmentflow.provider.status_queries` | counter | | `provider`, `status` | Status query answers |
| `instalmentflow.reconciliation.lines` | counter | | `provider`, `classification` | `MissingInEngine` above zero means callbacks are being lost |
| `instalmentflow.reconciliation.discrepancies` | counter | | `provider`, `kind` | Each needs a person |
| `instalmentflow.outbox.events` | counter | | `outcome` (`published`, `failed`) | Publisher health |

Attribute names are prefixed `instalmentflow.`, for example `instalmentflow.provider`.

Current state, rather than flows, comes from health checks (`AddInstalmentFlowChecks`, from
`InstalmentFlowPayments.GetHealthAsync` and `OutboxDispatcher.GetLagAsync`):
- inbox backlog and failed entries;
- ambiguous intents;
- reconciliation age;
- outbox lag.

## Suggested alerts

- `instalmentflow.verify.violations` above 0: page someone. The ledger disagrees with itself.
- Failed inbox entries, or open discrepancies, older than a day.
- `reconciliation.lines{classification="MissingInEngine"}` rising: callbacks are being lost (R-I1).
- `command.retries` above a few per cent of commands: a hot account.
- An unhealthy reconciliation check: Pull Transactions only reaches back 48 hours.
