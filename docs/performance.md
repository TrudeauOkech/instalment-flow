# Performance

NFR-4 asks that receiving and auto-allocating a payment on PostgreSQL, for an account with 5 obligations × 365
instalments, take less than 25 ms at p95 on a single node, sustaining at least 500 payments per second.

## Results (M12, 2026-11-23)

The workload, in both measurements:
- **Accounts:** 64 customers, each with 5 daily loans of 365 instalments.
- **Payments:** KES 70 at a time, by reference. Each payment settles part of one instalment and part of the next.
- **After the run:** `Verify`.

### Through the engine

`benchmarks/InstalmentFlow.Benchmarks -- --postgres 20 16` calls `Payments.RecordAsync` directly:
- **Latency:** 500 sequential payments on one account.
- **Throughput:** 16 writers for 20 s, each payment to a random account, so writers collide as real traffic does.

| Measure | Result | NFR-4 target |
|---|---|---|
| p50 | 9 ms | |
| **p95** | **11–18 ms** | < 25 ms ✅ |
| p99 | 15–25 ms | |
| **Throughput** | **979 payments/s** | ≥ 500 ✅ |
| Concurrency retries | 2,663 (14% of payments; each retried and recorded) | |
| Failed payments | 0 | 0 |
| `Verify` after the run | passed | passes |

### Through the callback endpoint (k6)

`benchmarks/k6/callbacks.js` measures what a provider sees. It runs against `benchmarks/InstalmentFlow.LoadHost`, an
ASP.NET Core host with the payments package on PostgreSQL and the default settings:
- It posts HMAC-signed simulator callbacks to `/hooks/{secret}/sim/payment` at a constant 500/s for 60 s.
- The inbox workers record them.
- Then it checks that every acknowledged callback became exactly one payment, and that `Verify` passes.

| Measure | Result | Target |
|---|---|---|
| Callback acknowledged (stored in the inbox), p95 | 3.1 ms | < 25 ms ✅ |
| Callbacks accepted at the offered rate | 30,001 of 30,001, none dropped | all ✅ |
| **Recorded end to end** | **500/s sustained:** the inbox was empty 0.2 s after the last callback | ≥ 500/s ✅ |
| Payments per acknowledged callback | exactly 1 (30,001 of 30,001) | 1 ✅ |
| Failed inbox entries | 0 | 0 ✅ |
| `Verify` after the run | passed | passes ✅ |

The callback path scales with `WorkerOptions.InboxWorkers`. Each inbox worker records one payment at a time and is
bound by database round trips (about 20 statements per payment):

| Inbox workers | Offered | Recorded | Inbox after the last callback |
|---|---|---|---|
| 1 | 500/s | 186/s | 101 s of backlog |
| 4 | 500/s | 455/s | 6 s of backlog |
| **8 (default)** | **500/s** | **500/s** | **empty in 0.2 s** |
| 8 | 1,000/s | about 590/s (this machine's ceiling) | 42 s of backlog |

Above one host's ceiling, run more instances: workers in every instance share the inbox under leases.

The machine was a laptop with 10 logical cores and .NET 10.0.15. PostgreSQL 17 ran in Docker Desktop with fsync off,
as the test server is configured, and k6 ran on the same machine. A dedicated database server with durable commits
adds commit latency; measure on your hardware with the same commands.

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

The k6 test (2026-11-23) found three more problems:
1. **One inbox worker recorded only 186 payments/s,** because it processes one entry at a time. Hosts now run eight
   by default (`WorkerOptions.InboxWorkers`).
2. **Losing a race evicted the winner's cache entry.** When two commands raced on one account, the loser's failed
   commit removed the entry the winner had just cached, and `Set` could replace a newer entry with an older one. Every
   conflict then cost a full reload of the account. Now a newer entry is never replaced or removed by an older one.
   With colliding writers, engine throughput rose from 704 to 979 payments/s.
3. **A correctness bug: uncommitted state could be cached.** A read inside a host transaction (`Enlist`) cached the
   account at an uncommitted version. If the host rolled back and another instance then committed the same version
   number, this instance would use the wrong state. Loads inside a host transaction are no longer cached. A test
   reproduces the sequence with two instances.

The earlier engine benchmark gave each writer its own accounts, so writers never collided. It now picks accounts at
random.

The cache is checked in tests. With `CheckCacheOnHit`, every cache hit in the PostgreSQL suites is reloaded and
compared row for row with the database. Two tests cover the cases that must not be cached, and removing either guard
fails them.

## Running it

```sh
dotnet run -c Release --project benchmarks/InstalmentFlow.Benchmarks -- --postgres 30 16   # needs Docker
dotnet run -c Release --project benchmarks/InstalmentFlow.Benchmarks -- --filter '*'       # BenchmarkDotNet, in memory
IF_BENCH_STATEMENTS=1 dotnet run -c Release --project benchmarks/InstalmentFlow.Benchmarks -- --postgres 1 1   # SQL per payment

# The callback path: start the host (needs Docker), then run k6 (https://k6.io) in another terminal
dotnet run -c Release --project benchmarks/InstalmentFlow.LoadHost -- [--accounts 64] [--inbox-workers 8]
k6 run benchmarks/k6/callbacks.js                                   # 500/s for 60 s
k6 run -e RATE=1000 -e DURATION=120s benchmarks/k6/callbacks.js     # find your ceiling
curl -X POST localhost:5090/load/sql                                # the most frequent SQL since the last call
```

The k6 run fails if a threshold is missed:
- a callback p95 of 25 ms or more;
- any dropped or failed callback;
- a backlog that takes 5 s or more to drain after the load;
- an acknowledged callback that was not recorded exactly once;
- a failed inbox entry;
- a `Verify` failure.
