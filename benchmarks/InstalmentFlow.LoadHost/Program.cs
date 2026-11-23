// The reference host for benchmarks/k6/callbacks.js. It starts PostgreSQL (Testcontainers), seeds the NFR-4 workload
// (customers T0…T{n-1}, each with 5 obligations of 365 daily instalments) and serves the provider callback endpoint
// for the simulated provider, with the inbox worker recording payments behind it.
//
//   dotnet run -c Release --project benchmarks/InstalmentFlow.LoadHost -- [--accounts 64] [--inbox-workers 8]
//
// /load/stats reports the inbox backlog and the callbacks acknowledged; /load/verify runs Verify on the tenant and
// counts its payments. k6 uses both to measure end-to-end throughput and to check the ledger after the run.
// /load/sql lists the most frequent SQL statements since the last call (for diagnosing a slow run).
using System.Globalization;
using InstalmentFlow;
using InstalmentFlow.AspNetCore;
using InstalmentFlow.Benchmarks;
using InstalmentFlow.EntityFrameworkCore;
using InstalmentFlow.Payments;
using InstalmentFlow.Payments.Simulation;
using InstalmentFlow.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var accounts = Option("--accounts", 64);
var inboxWorkers = Option("--inbox-workers", 8);
const string CallbackSecret = "k6-load-test-callback-secret";
const string PathSecret = "k6-load-test-path-secret";

Console.WriteLine($"Starting PostgreSQL (Testcontainers) and seeding {accounts} customers × {Workload.Obligations} obligations × {Workload.Instalments} instalments…");
var database = await PostgresServer.CreateDatabaseAsync();
var store = new PostgresStoreOptions
{
    ConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(database.Application) { MaxPoolSize = 100 }.ConnectionString,
};
var simulator = new SimulatedProvider(CallbackSecret);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5090");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddInstalmentFlow(o =>
{
    o.UseEngine((_, ct) => InstalmentFlowPostgres.CreateFactoryAsync(store, Workload.Configure, ct));
    o.UsePayments((_, flows, ct) => InstalmentFlowPostgres.CreatePaymentsAsync(store, flows, p => p.AddProvider(simulator), ct));
    o.Callbacks.PathSecret = PathSecret;
    o.Workers.InboxInterval = TimeSpan.FromMilliseconds(50);
    o.Workers.InboxWorkers = inboxWorkers;
});

var app = builder.Build();
var acknowledged = 0L;   // callbacks answered 200: each must become exactly one payment
app.Use(async (context, next) =>
{
    await next(context);
    if (context.Request.Path.StartsWithSegments("/hooks") && context.Response.StatusCode == StatusCodes.Status200OK)
    {
        Interlocked.Increment(ref acknowledged);
    }
});
app.UseExceptionHandler();
app.MapInstalmentFlowCallbacks();
app.MapGet("/load/stats", async (InstalmentFlowPayments payments) =>
{
    var health = await payments.GetHealthAsync();
    return new { pending = health.PendingInbox, failed = health.FailedInbox, acknowledged = Interlocked.Read(ref acknowledged) };
});
app.MapPost("/load/verify", async (InstalmentFlowFactory flows) =>
{
    var report = await flows.ForTenant(Workload.Tenant).Queries.VerifyAsync(VerifyScope.Tenant);
    return new { passed = report.Passed, violations = report.Violations.Count, payments = report.Payments };
});

var sqlCounts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
using var sqlListener = new System.Diagnostics.ActivityListener
{
    ShouldListenTo = s => s.Name == "Npgsql",
    Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
    ActivityStopped = a =>
    {
        var sql = (a.GetTagItem("db.query.text") as string ?? a.DisplayName).ReplaceLineEndings(" ");
        sqlCounts.AddOrUpdate(sql.Length > 140 ? sql[..140] : sql, 1, (_, n) => n + 1);
    },
};
System.Diagnostics.ActivitySource.AddActivityListener(sqlListener);
app.MapPost("/load/sql", () =>
{
    var top = sqlCounts.OrderByDescending(kv => kv.Value).Take(25).Select(kv => $"{kv.Value,7} {kv.Key}").ToList();
    sqlCounts.Clear();
    return string.Join('\n', top);
});

await app.StartAsync();
var payments = app.Services.GetRequiredService<InstalmentFlowPayments>();
await payments.Routing.MapAsync(simulator.Provider, "600123", Workload.Tenant);
var flow = app.Services.GetRequiredService<InstalmentFlowFactory>().ForTenant(Workload.Tenant);
for (var a = 0; a < accounts; a++)
{
    await Workload.SeedCustomerAsync(flow, $"T{a}");
}

Console.WriteLine($"""
    Ready on http://127.0.0.1:5090 with {inboxWorkers} inbox worker(s). In another terminal:
      k6 run -e ACCOUNTS={accounts} benchmarks/k6/callbacks.js
    """);
await app.WaitForShutdownAsync();
return 0;

int Option(string name, int fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1], CultureInfo.InvariantCulture) : fallback;
}
