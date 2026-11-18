## What and why

<!-- Link the issue or ADR. -->

## Checklist

- [ ] Tests added or updated; `dotnet test -c Release` passes
- [ ] `PublicAPI.Unshipped.txt` updated for any public API change
- [ ] `CHANGELOG.md` updated under *Unreleased*
- [ ] Commits are signed off (`git commit -s`, DCO)

### Financial change checklist (if this touches money movement)

- [ ] Invariants touched (I-x) and the tests that prove them:
- [ ] Idempotent and replay-tested
- [ ] Safe under concurrent commands on one customer account
- [ ] No fact is edited or deleted (compensating entries only)
- [ ] Tenant-scoped, with a cross-tenant test
- [ ] Allocation behaviour changes ship as a new strategy version
