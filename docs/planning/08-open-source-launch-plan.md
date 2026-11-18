# Document 8 — Open-Source Launch Plan

## 1. Licence: Apache-2.0 (recommended) vs MIT

| | MIT | Apache-2.0 |
|---|---|---|
| Permissiveness | Very permissive | Very permissive |
| Patent grant | None explicit | **Explicit patent licence from contributors, with termination on patent suit** |
| Contribution terms | Implicit | **§5: contributions are licensed under Apache-2.0 by default** |
| NOTICE / attribution | Copyright notice | Copyright notice + NOTICE file + statement of changes |
| Corporate legal comfort (banks, fintechs) | High | **High, often preferred for infrastructure** |
| GPLv2 compatibility | Yes | No (GPLv3 yes). This is irrelevant in the .NET ecosystem. |

**Recommendation: Apache-2.0.** Fintech adopters run legal reviews, and the patent grant removes one objection. Payment-allocation methods are an area where patent claims are plausible. Use the **DCO** (`Signed-off-by`) rather than a CLA, because it is lighter for contributors and needs no legal entity.

## 2. NuGet packaging and releases

- **Package IDs:** `InstalmentFlow`, `InstalmentFlow.EntityFrameworkCore`, `InstalmentFlow.Payments`, `InstalmentFlow.Payments.Mpesa`, `InstalmentFlow.Payments.AirtelMoney`, `InstalmentFlow.AspNetCore`, `InstalmentFlow.Testing`.
- **Reserve the `InstalmentFlow.*` ID prefix on nuget.org** (the prefix reservation programme) early. Check that the name is free and not trademarked before M0.
- **Metadata on every package:**
  - README;
  - icon;
  - SourceLink;
  - symbol packages (`.snupkg`);
  - deterministic builds;
  - `PackageLicenseExpression=Apache-2.0`;
  - repository URL and commit;
  - release notes link.
- **Versioning:** SemVer 2. All packages version together (lock-step), which is simpler for users ("use the same version everywhere"). MinVer or Nerdbank.GitVersioning derives the version from git tags.
- **Channels:**
  - `-preview.N` from main via GitHub Packages, or nightly;
  - `-rc.N`;
  - stable releases on nuget.org from signed tags only.
- **Publishing:**
  - GitHub Actions with NuGet **trusted publishing** (OIDC), so no long-lived API keys;
  - package signing;
  - SBOM (CycloneDX) attached to each GitHub Release.
- **Compatibility policy:**
  - 0.x may break with notes;
  - from 1.0, breaking changes only in a major version;
  - `[Obsolete]` for at least one minor release before removal;
  - schema migrations always forward-compatible within a major version.
- **Support policy:** follow the .NET LTS cadence. When .NET 12 LTS ships (Nov 2027), target `net10.0;net12.0`, and drop net10.0 when it reaches EOL.

## 3. Documentation

| Area | Content | Milestone |
|---|---|---|
| README | Value proposition in three sentences; a 30-line quick start; package table; "what this is NOT" | M0, then each release |
| Concepts | Payment vs allocation vs instalment; the two-step model; lots; journal | M6 |
| Allocation strategies | One page per strategy, with golden tables that double as tests | M4 |
| Financial invariants | I-1…I-17 with how each is enforced and tested | M6 |
| Failure scenarios | The 22 scenarios with expected outcomes | M5–M9 |
| Integration guides | M-Pesa (C2B paybill PAYG, STK push lender), Airtel, custom provider, existing LMS, GL export | M9–M11 |
| Operations | Reconciliation runbook, payment exception queue handling, cross-tenant isolation checks, `Verify` alerting, outbox lag, migrations | M8–M12 |
| ADRs | `docs/decisions/ADR-NNN-*.md` (MADR format) | Ongoing |
| API reference | Generated (DocFX), published to GitHub Pages | M6 |
| Compliance notes | Data Protection Act responsibilities; "not legal advice"; regulatory considerations for allocation order | M10 |

## 4. Samples

Five runnable samples (doc 03 §3), each with its own README, built and executed in CI. Sample 04 uses the Daraja sandbox, with a Docker Compose file for PostgreSQL and an ngrok note for callbacks.

## 5. Community and governance files

- `CONTRIBUTING.md`: dev setup (`dotnet` 10 SDK plus Docker), test commands, DCO, the "financial change" checklist (invariants touched? ADR? golden tables updated?).
- `CODE_OF_CONDUCT.md`: Contributor Covenant 2.1.
- `SECURITY.md`: private vulnerability reporting through GitHub Security Advisories, a response SLA (acknowledge within 72 h), and supported versions.
- `GOVERNANCE.md`: maintainer roles, decision by ADR, how to become a maintainer.
- `.github/ISSUE_TEMPLATE/`: bug (requires version, provider, minimal repro or scenario DSL snippet); **financial discrepancy** (a dedicated template that asks for a redacted payment trace); feature request (requires use case); provider capability report (for correcting verified or unverified claims).
- `PULL_REQUEST_TEMPLATE.md`, `CODEOWNERS` (core financial code needs two maintainer reviews), Dependabot, and a CodeQL workflow.
- Labels: `area:allocation`, `area:ledger`, `provider:mpesa`, `invariant`, `good first issue`, `needs-adr`.

## 6. Launch sequence

1. **Private alpha (M6, 0.1-preview):** share with 2–3 target engineers (PAYG or lender) for API feedback. Expect API changes.
2. **Public preview (M9, 0.3):** blog post "Correct mobile-money repayments in .NET" with a sample walkthrough. Post to the .NET community (r/dotnet, the .NET Foundation community) and Kenyan developer communities (Python/.NET Nairobi meetups, Safaricom Daraja developer forums).
3. **Pilot (M12):** one adopter in shadow mode, running alongside their existing system and comparing balances daily. Publish a case study if they agree.
4. **1.0:** API freeze, documentation completeness review, a security review, and a second maintainer.
5. **Post-1.0:** consider applying to the .NET Foundation for neutral stewardship once there are external contributors.

## 7. Success metrics

- First-success time under 10 minutes (measured with new users).
- At least 2 production adopters within 6 months of 1.0.
- Zero open invariant-violation bugs older than 7 days.
- Median issue first-response time under 3 days.
