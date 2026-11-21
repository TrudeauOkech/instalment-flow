# ADR-017: Storage ports stay internal; store packages ship in lockstep with the core

- Status: Accepted (2026-11-21)

## Context
The storage ports (`ILedgerStore`, `ITenantStore`, the `Commit` and fact records) work on the domain model: the
customer account aggregate, funds lots, allocation and correction facts. Making them public would freeze that model
as a public API before 1.0, and invite third-party stores that bypass rules the engine relies on (atomic commits,
uniqueness, the reversal-before-payment guard).

## Decision
- The ports and domain records stay `internal` in `InstalmentFlow`. First-party store packages
  (`InstalmentFlow.EntityFrameworkCore` from 0.2) implement them through `InternalsVisibleTo`.
- Store packages and the core are released together with the same version. `InstalmentFlowPostgres.CreateFactoryAsync`
  refuses to start if the two assemblies' versions differ.
- The behavioural test suite runs against every store (`IF_STORE=postgres`), so each store keeps the same contract.
- Third-party stores are not supported in 1.0. Public ports may follow once their shape has survived real
  deployments, as a minor-version addition.

## Consequences
- The domain model can still change before 1.0 without breaking a public contract.
- Hosts must upgrade `InstalmentFlow` and `InstalmentFlow.EntityFrameworkCore` together; NuGet allows a newer core
  with an older store, so the start-up check gives a clear error instead.
