# ADR-007: Multi-tenant from day one; isolation enforced by the system

- Status: Accepted (2026-11-18)

## Context
Payment processors and lending platforms serve several lenders. Retrofitting tenancy later is expensive and error-prone. Isolation that depends on developers remembering a filter will eventually fail.

## Decision
1. **Every record carries `tenant_id`.** This covers accounts, obligations, plans, instalments, payments, lots, allocations, charges, waivers, reversals, refunds, exceptions, journal entries, idempotency keys, outbox, inbox and reconciliation.
2. **Database-level isolation:**
   - Every primary key and **every foreign key is composite and includes `tenant_id`**, so a row cannot reference another tenant's row.
   - PostgreSQL **Row-Level Security** is enabled on all tables. The policy is `tenant_id = current_setting('instalment_flow.tenant_id')`. The store sets it per transaction (`SET LOCAL`), and the application role does not bypass RLS.
3. **Library-level isolation:**
   - There is no ambient static tenant. Callers obtain a tenant-scoped engine with `IInstalmentFlowFactory.For(tenantId)` (or a scoped `IInstalmentFlow` resolved through `ITenantContext` in ASP.NET Core).
   - Every store port method takes `TenantId` explicitly.
   - EF Core global query filters cover reads.
   - Aggregates assert that the loaded tenant matches the requested one.
   - Idempotency keys, caches and outbox ordering are scoped per tenant.
4. **Per-tenant configuration** is versioned in `tenant_policy_versions`, so changes are auditable, and allocations record the policy version they used. It includes:
   - base currency;
   - business time zone;
   - allocation strategy and version;
   - component order;
   - future-instalment policy;
   - default grace period;
   - instalment policies;
   - payment reference format and validation;
   - the unreferenced-payment policy (ADR-009);
   - the provider accounts the tenant owns.
5. **Inbound provider routing:** `(provider, provider_account)`, e.g. `("mpesa", "600123")`, maps to exactly one tenant, with a unique constraint. A shared shortcode serving several lenders is supported only through a host-supplied `ITenantRouter`, for example one that uses a reference prefix. It must be configured explicitly.
6. **Provider credentials** are supplied per tenant by the host through `IProviderCredentialSource`, backed by the host's secret store. InstalmentFlow never stores secrets in its tables.
7. **Stronger isolation later:** schema-per-tenant or database-per-tenant are possible without API change, because all access goes through tenant-aware ports.

## Consequences
- Cross-tenant tests are mandatory: every command and query is attempted against another tenant's IDs and must fail.
- A small RLS overhead is accepted.
