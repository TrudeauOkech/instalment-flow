# ADR-003: The customer account is the consistency boundary

- Status: Accepted (2026-11-18)

## Decision
`CustomerAccount` (scoped to a tenant) is the aggregate root for every command that moves money. It contains:
- unapplied funds per currency;
- active funds lots;
- all open obligations, with their open instalments.

Concurrency control is a `version` column on the account, updated with `WHERE version = @expected`. On conflict the engine reloads and retries, 5 times by default with jitter. A tenant can opt in to pessimistic `SELECT … FOR UPDATE` on the account row. Tenant unidentified funds use per-provider-account system accounts to avoid a single hot row.

## Alternatives rejected
- **A per-loan aggregate with a saga for cross-loan allocation.** Intermediate states would be inconsistent, it is more complex, and it brings no benefit at per-customer contention levels.

## Consequences
- Commands for one customer serialise; commands for different customers run in parallel.
- The aggregate loads only open state. Settled history stays in the journal.
