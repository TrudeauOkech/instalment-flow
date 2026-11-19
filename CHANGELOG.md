# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- M1 money, currency and time:
  - `Money`: exact `decimal` amounts validated against the currency's minor units; same-currency arithmetic only (`IF1001`); explicit rounding modes; largest-remainder `Allocate` and `Split`.
  - `Currency`: a canonical ISO 4217 registry of 19 currencies (East and West African currencies plus USD, EUR and GBP).
  - `TenantClock`: converts instants to and from business dates in an IANA time zone through `TimeProvider`, with defined rules for daylight-saving gaps and overlaps.
  - `ExternalReference` and `IdempotencyKey` value objects.
  - `InstalmentFlowException` with stable error codes (`IF1001`–`IF1004`).
  - 62 unit and CsCheck property tests.
- M0 repository foundation:
  - seven package projects targeting `net10.0`;
  - central package management;
  - analyzers, with warnings treated as errors;
  - public API tracking;
  - bans on floating point and the system clock;
  - architecture tests enforcing ADR-013 package boundaries;
  - CI, CodeQL and Dependabot;
  - licence and community files.
- Planning documents (`docs/planning/`) and ADR-001 to ADR-016 (`docs/decisions/`).
