---
_layout: landing
---

# InstalmentFlow

**A multi-tenant .NET library for flexible loan repayments.** It records every mobile-money payment exactly once,
allocates it according to the lender's rules, and keeps an append-only, double-entry history that explains every
shilling, including after reversals.

- [Concepts](docs/concepts.md): payments, allocation, instalments and the journal.
- [Inside an existing loan system](docs/integrations/existing-loan-system.md): the integration most lenders need.
- [M-Pesa](docs/integrations/mpesa.md) and [Airtel Money](docs/integrations/airtel.md) adapters, and the
  [simulated provider](docs/integrations/simulator.md) for development.
- [Operations runbook](docs/operations.md), [observability](docs/observability.md) and the
  [threat model](docs/security/threat-model.md).
- [API reference](api/index.md).

```sh
dotnet add package InstalmentFlow --prerelease
```

Source, samples and issues: [github.com/TrudeauOkech/instalment-flow](https://github.com/TrudeauOkech/instalment-flow).
