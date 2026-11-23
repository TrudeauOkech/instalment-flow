using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using InstalmentFlow.EntityFrameworkCore;
using InstalmentFlow.TestSupport;

namespace InstalmentFlow.Benchmarks;

/// <summary>
/// NFR-4 on PostgreSQL (in Docker): the latency of receive + auto-allocate on one 5 x 365 account, one payment at a
/// time, and sustained throughput with parallel writers on many such accounts. Prints a Markdown table.
/// </summary>
public static class PostgresLoad
{
    public static async Task RunAsync(int latencyPayments, int accounts, int writers, TimeSpan duration)
    {
        var retries = 0L;
        using var meters = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (i, l) => { if (i.Name == "instalmentflow.command.retries") { l.EnableMeasurementEvents(i); } },
        };
        meters.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref retries, value));
        meters.Start();
        Console.WriteLine("Starting PostgreSQL (Testcontainers) and seeding…");
        var database = await PostgresServer.CreateDatabaseAsync();
        // The test server caps pools at 20 (many databases per test run); a host uses Npgsql's default of 100.
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(database.Application) { MaxPoolSize = 100, ConnectionIdleLifetime = 300 }.ConnectionString;
        var factory = await InstalmentFlowPostgres.CreateFactoryAsync(new PostgresStoreOptions { ConnectionString = connection }, Workload.Configure);
        var flow = factory.ForTenant(Workload.Tenant);

        await Workload.SeedCustomerAsync(flow, "L");
        for (var n = 0; n < 20; n++)
        {
            await flow.Payments.RecordAsync(Workload.Payment("L", -1 - n));   // warm up connections and plans
        }

        var statements = new ConcurrentDictionary<string, int>();
        var durations = new ConcurrentDictionary<string, double>();   // floating-point-allowed: reporting only
        using (var npgsql = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                var sql = Shorten(a.GetTagItem("db.query.text") as string ?? a.GetTagItem("db.statement") as string ?? a.DisplayName);
                statements.AddOrUpdate(sql, 1, (_, n) => n + 1);
                durations.AddOrUpdate(sql, a.Duration.TotalMilliseconds, (_, d) => d + a.Duration.TotalMilliseconds);
            },
        })
        {
            ActivitySource.AddActivityListener(npgsql);
            for (var i = 0; i < 20; i++)
            {
                await flow.Payments.RecordAsync(Workload.Payment("L", 1_000_000 + i));
            }
        }

        if (Environment.GetEnvironmentVariable("IF_BENCH_STATEMENTS") is not null)
        {
            Console.WriteLine($"Statements for 20 receives: {statements.Values.Sum()}, {durations.Values.Sum() / 20:0.0} ms of SQL per payment");
            foreach (var (sql, ms) in durations.OrderByDescending(s => s.Value))
            {
                Console.WriteLine($"  {ms / 20,6:0.00} ms  {statements[sql] / 20}x  {sql}");
            }
        }

        var latencies = new List<long>(latencyPayments);
        for (var n = 0; n < latencyPayments; n++)
        {
            var started = Stopwatch.GetTimestamp();
            await flow.Payments.RecordAsync(Workload.Payment("L", n));
            latencies.Add(Stopwatch.GetTimestamp() - started);
        }

        for (var a = 0; a < accounts; a++)
        {
            await Workload.SeedCustomerAsync(flow, $"T{a}");
        }

        var completed = 0;
        var retriesBefore = Interlocked.Read(ref retries);
        var errors = new ConcurrentBag<string>();
        var stop = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; stop.Elapsed < duration; i++)
            {
                var customer = $"T{Random.Shared.Next(accounts)}";   // writers collide on accounts, as real traffic does
                try
                {
                    await flow.Payments.RecordAsync(Workload.Payment(customer, (w * 1_000_000) + i));
                    Interlocked.Increment(ref completed);
                }
#pragma warning disable CA1031 // A failed payment is counted and reported, not fatal to the measurement.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    errors.Add(ex.GetType().Name);
                }
            }
        })));
        stop.Stop();

        var report = await flow.Queries.VerifyAsync(VerifyScope.Tenant);
        string Ms(double percentile)   // floating-point-allowed: reporting only
        {
            var sorted = latencies.Order().ToList();
            var ticks = sorted[Math.Min(sorted.Count - 1, (int)(percentile * sorted.Count))];
            return (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture);   // floating-point-allowed: reporting only
        }

        var seconds = stop.Elapsed.TotalSeconds;   // floating-point-allowed: reporting only
        Console.WriteLine($"""

            | Measure | Result | NFR-4 target |
            |---|---|---|
            | Receive + allocate, one 5 x 365 account, p50 | {Ms(0.50)} ms | |
            | … p95 | {Ms(0.95)} ms | < 25 ms |
            | … p99 | {Ms(0.99)} ms | |
            | Sustained throughput, {writers} writers on {accounts} accounts, {duration.TotalSeconds:0} s | {(completed / seconds).ToString("0", CultureInfo.InvariantCulture)} payments/s ({completed} payments) | ≥ 500 payments/s |
            | Concurrency retries during the load | {Interlocked.Read(ref retries) - retriesBefore} | |
            | Failed payments | {errors.Count} {string.Join(", ", errors.GroupBy(e => e).Select(g => $"{g.Key} x{g.Count()}"))} | 0 |
            | Verify after the run | {(report.Passed ? "passed" : $"FAILED: {report.Violations.Count} violations")} ({report.Payments} payments) | passes |

            Machine: {Environment.ProcessorCount} logical cores, {Environment.OSVersion}, .NET {Environment.Version}; PostgreSQL 17 in Docker (fsync off).
            """);
    }

    private static string Shorten(string sql)
    {
        var flat = string.Join(' ', sql.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length > 110 ? flat[..110] : flat;
    }
}
