# ADR-004: Append-only double-entry journal; immutable financial history

- Status: Accepted (2026-11-18)

## Decision
- Every command produces exactly one balanced journal transaction (Σ debits = Σ credits per currency). The journal is append-only.
- The following are **facts**: payments, allocations, allocation reversals, payment reversals, charges, waivers, refund events, plan versions and exception resolutions. Facts are never updated or deleted. Mistakes are corrected with reversals and compensating entries.
- Balances are **projections** updated in the same database transaction. They can always be rebuilt from facts. `VerifyAsync` checks projections against the journal.
- Defence in depth in PostgreSQL:
  - triggers reject UPDATE and DELETE on fact tables;
  - a deferred constraint trigger rejects unbalanced transactions;
  - the application role has no UPDATE, DELETE or TRUNCATE grants on fact tables.
- This is not full event sourcing. Aggregates are loaded from projections, not by replaying events.

## Consequences
- State as of any instant can be reconstructed by folding the journal.
- The ledger grows without bound, so partitioning is planned for 0.2.
- Correcting a mistake always leaves a visible trail. That is intended.
