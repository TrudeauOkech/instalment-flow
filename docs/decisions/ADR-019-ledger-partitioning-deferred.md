# ADR-019: Ledger partitioning is deferred; performance comes from not reloading what did not change

- Status: Accepted (2026-11-22)

## Context
The roadmap planned range partitioning of `ledger_entries` for M12 (doc 05 §3, R-T2), expecting ledger growth to
slow the hot path. M12's benchmarks ([docs/performance.md](../performance.md)) show it is not the bottleneck. The hot
path was dominated by reloading and re-mapping whole account schedules on every command. With an account cache and
change-only writes, NFR-4 is met with margin: p95 11–18 ms against 25 ms, and 829–912 payments/s against 500. The
ledger is only ever appended to and read by indexed key.

Partitioning tables that 0.2 has already shipped would take a migration that rewrites `ledger_entries` (and
`journal_transactions`, which its foreign key references) under its append-only triggers, row-level security and
deferred balance check. That is real risk to installed databases, for no measured gain.

## Decision
- Do not partition the ledger in 1.0.
- Keep the schema ready for it: every ledger key starts with `tenant_id` and the journal sequence, so a later
  partitioning by `recorded_at` or by sequence range stays possible.
- Revisit when a deployment measures ledger size affecting latency or maintenance (vacuum, backups), or when archival
  needs partitions to detach. That would be an opt-in migration with its own tests and a documented runbook.

## Consequences
- 1.0 ships without a risky rewrite of shipped tables.
- Archival of old ledger data is not built in. Hosts with very large ledgers rely on PostgreSQL's own tooling until
  this is revisited.
- The benchmark command in docs/performance.md is the way to check a deployment before and after any such change.
