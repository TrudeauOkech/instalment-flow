# Performance

NFR-4 asks that receiving and auto-allocating a payment on PostgreSQL, for an account with 5 obligations × 365
instalments, take less than 25 ms at p95 on a single node, sustaining at least 500 payments per second.

## Results (M12, 2026-11-22)

`benchmarks/InstalmentFlow.Benchmarks -- --postgres 20 <writers>`:
- **Account:** 5 daily loans of 365 instalments each, paid KES 70 at a time by reference. Each payment settles part
  of one instalment and part of the next.
- **Latency:** 500 sequential payments on one such account.
- **Throughput:** 20 s of parallel writers spread over 64 such accounts.
- **After the run:** `Verify`.

| Measure | Result | NFR-4 target |
|---|---|---|
| p50 | 9–10 ms | |
| **p95** | **11–18 ms** | < 25 ms ✅ |
| p99 | 15–25 ms | |
| **Throughput, 16 writers** | **829 payments/s** | ≥ 500 ✅ |
| **Throughput, 32 writers** | **912 payments/s** | ≥ 500 ✅ |
| Concurrency retries, failed payments | 0, 0 | |
| `Verify` after the run | passed (18,797 payments) | passes |

The machine was a laptop with 10 logical cores and .NET 10.0.15. PostgreSQL 17 ran in Docker Desktop with fsync off,
as the test server is configured. A dedicated database server with durable commits adds commit latency; measure on
your hardware with the same command.

In memory (BenchmarkDotNet, `--filter '*'`):

| Benchmark | Mean | Allocated |
|---|---|---|
| Receive + allocate (5 × 365 instalments) | 0.44 ms | 276 KB |
| `Verify`, 10 customers, 50 obligations, 1,000 payments | 9.4 ms | 14.9 MB |

## What it took

The first measurement missed both targets badly: p95 96.7 ms and 80 payments/s. The engine itself took only about
0.5 ms. The rest was the PostgreSQL store doing work in proportion to the whole schedule on every command:

1. **Each command reloaded the whole account:** about 3,700 schedule rows. The store now keeps an **account cache**
   (`PostgresStoreOptions.AccountCacheSize`, 1,024 accounts by default). A cached account is used only after a one-row
   check that its version is current. Every commit bumps the version, so a stale copy can never be used. After a
   commit the cache holds the committed state, with two exceptions where a reload would differ:
   - a lot moved in from unidentified funds keeps its older position in the database;
   - a commit inside a host transaction could be rolled back.

   With this, p95 fell to 15 ms and throughput rose to 376 payments/s.
2. **Each commit mapped the whole account, before and after, to rows** to find the few that changed. The domain
   replaces only the obligations a command touches, so the store now diffs just those. Together with server GC (as
   ASP.NET Core hosts use), throughput rose to 829–912 payments/s.

Measuring also exposed a correctness gap from M7: the store loaded obligations and schedules without `ORDER BY`. They
are now ordered by key.

The cache is checked in tests. With `CheckCacheOnHit`, every cache hit in the PostgreSQL suites is reloaded and
compared row for row with the database. Two tests cover the cases that must not be cached, and removing either guard
fails them.

## Running it

```sh
dotnet run -c Release --project benchmarks/InstalmentFlow.Benchmarks -- --postgres 30 16   # needs Docker
dotnet run -c Release --project benchmarks/InstalmentFlow.Benchmarks -- --filter '*'       # BenchmarkDotNet, in memory
IF_BENCH_STATEMENTS=1 dotnet run -c Release --project benchmarks/InstalmentFlow.Benchmarks -- --postgres 1 1   # SQL per payment
```
