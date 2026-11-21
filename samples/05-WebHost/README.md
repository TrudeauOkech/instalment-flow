# Sample 05: ASP.NET Core host

InstalmentFlow inside an ASP.NET Core application. It wires up:
- the engine and payments, created at start-up;
- provider callback endpoints behind a path secret;
- the simulator's endpoints;
- a tenant-scoped API (`IInstalmentFlow` injected per request);
- background workers, `ProblemDetails` and health checks.

`dotnet run --project samples/05-WebHost` runs it against itself. It pays a loan through the simulator's HTTP
endpoints, with a duplicate callback injected, lets the inbox worker record the payment, and checks the loan, health
and ledger. Add `-- --serve` to keep it running on port 5080 and try the endpoints yourself; the start-up message lists
example `curl` commands.

To use PostgreSQL, replace the two in-memory calls:

```csharp
o.UseEngine((sp, ct) => InstalmentFlowPostgres.CreateFactoryAsync(storeOptions, b => b.AddTenant(...), ct));
o.UsePayments((sp, flows, ct) => InstalmentFlowPostgres.CreatePaymentsAsync(storeOptions, flows, p => p.AddProvider(mpesa), ct));
o.Workers.Add("outbox", TimeSpan.FromSeconds(1), (sp, ct) => dispatcher.DispatchAsync(ct));
o.Health.MeasureOutboxLag = (sp, ct) => dispatcher.GetLagAsync(ct);
```

Run `InstalmentFlowPostgres.MigrateAsync` as the schema owner first: migrations are explicit, never run silently at
start-up.
