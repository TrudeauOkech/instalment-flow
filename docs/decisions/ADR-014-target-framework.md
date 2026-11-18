# ADR-014: Target net10.0

- Status: Accepted (2026-11-18)

## Decision
- Packages target `net10.0` (LTS, supported until 2028-11-14). CI also tests on .NET 11 (STS, GA 2026-11-10).
- .NET 8 reached end of life on 2026-11-10, so it is not targeted.
- When .NET 12 LTS ships, multi-target `net10.0;net12.0`. Drop net10.0 at its end of life.
