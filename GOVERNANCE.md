# Governance

## Roles

- **Maintainers** review and merge pull requests, triage issues, cut releases and accept ADRs. Changes to core financial code (recording, identification, allocation, reversal, journal) need approval from **two** maintainers once the project has more than one.
- **Contributors** are anyone who opens an issue, a discussion or a pull request.

## Decisions

- Day-to-day decisions are made in pull requests.
- Decisions that change architecture, scope, public API direction or financial semantics are recorded as **Architecture Decision Records** in `docs/decisions/`. An ADR is proposed in a pull request and accepted by maintainer approval. Accepted ADRs are never edited; they are superseded by new ADRs.
- Scope is defined by [ADR-001](docs/decisions/ADR-001-product-scope.md). Proposals outside it need a new ADR, and are usually better as a separate package.

## Becoming a maintainer

Contributors who have made sustained, high-quality contributions (especially reviews and tests of financial behaviour) can be nominated by a maintainer and are accepted by consensus of the existing maintainers. Recruiting a second maintainer is a 1.0 goal.

## Releases

Releases are cut from signed `vX.Y.Z` tags on `main` by CI, which publishes to NuGet through trusted publishing. Versioning follows SemVer. All packages share one version.
