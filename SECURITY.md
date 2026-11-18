# Security Policy

InstalmentFlow handles financial records, so we take security reports seriously.

## Reporting a vulnerability

Please report vulnerabilities **privately** using GitHub's [private vulnerability reporting](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability) ("Report a vulnerability" under the repository's **Security** tab). Do not open a public issue or pull request.

Please include:
- the affected package and version;
- a description of the issue and its impact (for example: duplicate crediting, cross-tenant data access, forged callbacks, loss of audit history);
- steps or a minimal scenario to reproduce it;
- any suggested fix.

Please do **not** include real customer data, phone numbers or provider credentials.

## What to expect

- Acknowledgement within **72 hours**.
- An initial assessment within **7 days**.
- A coordinated disclosure: we agree a timeline with you, publish a GitHub Security Advisory with the fix, and credit you unless you prefer otherwise.

## Supported versions

| Version | Supported |
|---|---|
| Pre-1.0 (`0.x`) | The latest minor release only |
| 1.x and later | The latest minor of each supported major release (policy finalised at 1.0) |

## Scope notes

- **Callback authenticity:** Safaricom Daraja callbacks carry no signature. InstalmentFlow mitigates this with network controls and reconciliation (see `docs/planning/03-architecture.md` §7). Weaknesses in those mitigations are in scope.
- **Tenant isolation:** any way to read or affect another tenant's data is treated as critical.
- **Host responsibilities:** issues in the host application's own authentication, secret storage or infrastructure are out of scope. Kenya Data Protection Act obligations rest with the host as data controller.
