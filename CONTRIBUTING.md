# Contributing to InstalmentFlow

Thank you for helping. InstalmentFlow is financial infrastructure, so correctness comes before features. This guide explains how to work on it.

## Development setup

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). `global.json` pins the feature band.
- Docker, for PostgreSQL integration tests (from milestone M7).

```bash
dotnet build -c Release
dotnet test -c Release            # Microsoft Testing Platform (configured in global.json)
```

The build treats warnings as errors. These rules are enforced mechanically:

| Rule | Enforced by |
|---|---|
| No floating point (`double`, `float`) in shipped code | `src/BannedSymbols.txt` + `SourceRuleTests` |
| No `DateTime`, and no system clock (`DateTimeOffset.UtcNow`); use `TimeProvider` | `src/BannedSymbols.txt` |
| No implicit `Math.Round(decimal)`; use explicit rounding modes | `src/BannedSymbols.txt` |
| Every public API change is recorded in `PublicAPI.Unshipped.txt` | `Microsoft.CodeAnalysis.PublicApiAnalyzers` |
| Package boundaries ([ADR-013](docs/decisions/ADR-013-packages-and-dependencies.md)); zero third-party dependencies in core | `tests/InstalmentFlow.ArchitectureTests` |
| Package versions are pinned centrally | `Directory.Packages.props` |

## Making a change

1. For anything non-trivial, open an issue first. If it changes a decision, propose an ADR in `docs/decisions/`. Accepted ADRs are never edited; they are superseded by new ones.
2. Keep pull requests small and focused, with tests.
3. Update `CHANGELOG.md` under *Unreleased*.

### Financial change checklist

If your change touches money movement (recording, identification, allocation, reversal, charges, refunds, the journal or projections), the pull request must answer:

- [ ] Which invariants (doc 02 §7, I-1 to I-23) does this touch, and which tests prove they still hold?
- [ ] Is the operation idempotent, and is the replay tested?
- [ ] Is it safe under concurrent commands for the same customer account?
- [ ] Does it edit or delete any fact? (It must not. Use compensating entries.)
- [ ] Is it tenant-scoped, with a cross-tenant test?
- [ ] If allocation behaviour changes, is it a new strategy version rather than a change to an existing one?
- [ ] Are the golden tables or failure-scenario docs updated?

Changes to core financial code need approval from two maintainers.

## Developer Certificate of Origin

Contributions are accepted under the [Developer Certificate of Origin](https://developercertificate.org/) (DCO). There is no CLA. Sign off every commit:

```bash
git commit -s -m "Describe the change"
```

This adds a `Signed-off-by:` line certifying that you wrote the change or have the right to submit it under the project's licence (Apache-2.0).

## Reporting security issues

Do **not** open a public issue. See [SECURITY.md](SECURITY.md).

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md).
