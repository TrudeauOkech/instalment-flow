using System.Collections.Immutable;
using InstalmentFlow.Domain;

namespace InstalmentFlow.Storage;

// Storage ports are internal until milestone M7, when the PostgreSQL store proves their shape (they change in
// M3 when the journal arrives). Every method takes the tenant explicitly (ADR-007): there is no ambient tenant.

internal sealed record TenantRecord(TenantId Id, Currency BaseCurrency, string TimeZone, ImmutableArray<TenantPolicyVersion> Policies)
{
    public TenantPolicyVersion Current => Policies[^1];
}

internal sealed record ObligationLookup(ObligationId? Id, string? ExternalId, string? PaymentReference);

internal readonly record struct ObligationLocation(AccountId AccountId, ObligationId ObligationId);

internal interface ITenantStore
{
    ValueTask<TenantRecord?> GetAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <exception cref="RuleViolationException">The tenant exists (<c>IF5003</c>).</exception>
    ValueTask CreateAsync(TenantRecord tenant, CancellationToken cancellationToken);

    /// <summary>Atomically appends the policy version produced from the current record.</summary>
    ValueTask<TenantRecord> AppendPolicyAsync(TenantId tenantId, Func<TenantRecord, TenantPolicyVersion> next, CancellationToken cancellationToken);
}

internal interface IAccountStore
{
    ValueTask<CustomerAccount?> LoadAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken);

    ValueTask<AccountId?> FindAccountAsync(TenantId tenantId, string externalId, CancellationToken cancellationToken);

    ValueTask<ObligationLocation?> FindObligationAsync(TenantId tenantId, ObligationLookup lookup, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the account if its stored version still equals <see cref="CustomerAccount.Version"/> (optimistic
    /// concurrency; version 0 means new) and returns it with the next version. Uniqueness of account external IDs,
    /// obligation external IDs and payment references within the tenant is enforced atomically with the save.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">The account changed since it was loaded.</exception>
    /// <exception cref="RuleViolationException">A uniqueness rule would be broken (<c>IF1006</c>, <c>IF1007</c>).</exception>
    ValueTask<CustomerAccount> SaveAsync(CustomerAccount account, CancellationToken cancellationToken);
}

/// <summary>
/// Thread-safe in-memory store. It holds immutable snapshots, so loads can never observe a half-applied command.
/// </summary>
internal sealed class InMemoryStore : ITenantStore, IAccountStore
{
    private readonly Lock _tenantsLock = new();
    private ImmutableDictionary<TenantId, TenantState> _tenants = ImmutableDictionary<TenantId, TenantState>.Empty;

    public ValueTask<TenantRecord?> GetAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_tenants.TryGetValue(tenantId, out var state) ? state.Tenant : null);

    public ValueTask CreateAsync(TenantRecord tenant, CancellationToken cancellationToken)
    {
        Create(tenant);
        return ValueTask.CompletedTask;
    }

    /// <summary>Synchronous creation, used to seed tenants when the factory is built.</summary>
    public void Create(TenantRecord tenant)
    {
        lock (_tenantsLock)
        {
            if (_tenants.ContainsKey(tenant.Id))
            {
                throw new RuleViolationException(ErrorCodes.DuplicateTenant, $"Tenant '{tenant.Id}' already exists.");
            }

            _tenants = _tenants.Add(tenant.Id, new TenantState(tenant));
        }
    }

    public ValueTask<TenantRecord> AppendPolicyAsync(TenantId tenantId, Func<TenantRecord, TenantPolicyVersion> next, CancellationToken cancellationToken)
    {
        var state = State(tenantId);
        lock (state.Lock)
        {
            var version = next(state.Tenant);
            state.Tenant = state.Tenant with { Policies = state.Tenant.Policies.Add(version) };
            return ValueTask.FromResult(state.Tenant);
        }
    }

    public ValueTask<CustomerAccount?> LoadAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Accounts.GetValueOrDefault(accountId));

    public ValueTask<AccountId?> FindAccountAsync(TenantId tenantId, string externalId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).AccountsByExternalId.TryGetValue(externalId, out var id) ? id : (AccountId?)null);

    public ValueTask<ObligationLocation?> FindObligationAsync(TenantId tenantId, ObligationLookup lookup, CancellationToken cancellationToken)
    {
        var state = State(tenantId);
        ObligationLocation? location = null;
        if (lookup.Id is { } id && state.ObligationsById.TryGetValue(id, out var byId))
        {
            location = byId;
        }
        else if (lookup.ExternalId is { } externalId && state.ObligationsByExternalId.TryGetValue(externalId, out var byExternal))
        {
            location = byExternal;
        }
        else if (lookup.PaymentReference is { } reference && state.ObligationsByReference.TryGetValue(reference, out var byReference))
        {
            location = byReference;
        }

        return ValueTask.FromResult(location);
    }

    public ValueTask<CustomerAccount> SaveAsync(CustomerAccount account, CancellationToken cancellationToken)
    {
        var state = State(account.TenantId);
        lock (state.Lock)
        {
            var stored = state.Accounts.GetValueOrDefault(account.Id);
            var storedVersion = stored?.Version ?? CustomerAccount.NewVersion;
            if (storedVersion != account.Version || (stored is null && account.Version != CustomerAccount.NewVersion))
            {
                throw new ConcurrencyConflictException(account.Id, account.Version);
            }

            RequireUnique(state.AccountsByExternalId, account.ExternalId, account.Id, ErrorCodes.DuplicateExternalId, "Account external ID");
            foreach (var obligation in account.Obligations)
            {
                var location = new ObligationLocation(account.Id, obligation.Id);
                RequireUnique(state.ObligationsByExternalId, obligation.ExternalId, location, ErrorCodes.DuplicateExternalId, "Obligation external ID");
                RequireUnique(state.ObligationsByReference, obligation.PaymentReference, location, ErrorCodes.DuplicatePaymentReference, "Payment reference");
            }

            var saved = account with { Version = storedVersion + 1 };
            state.Accounts = state.Accounts.SetItem(saved.Id, saved);
            state.AccountsByExternalId = state.AccountsByExternalId.SetItem(saved.ExternalId, saved.Id);
            foreach (var obligation in saved.Obligations)
            {
                var location = new ObligationLocation(saved.Id, obligation.Id);
                state.ObligationsById = state.ObligationsById.SetItem(obligation.Id, location);
                state.ObligationsByExternalId = state.ObligationsByExternalId.SetItem(obligation.ExternalId, location);
                state.ObligationsByReference = state.ObligationsByReference.SetItem(obligation.PaymentReference, location);
            }

            return ValueTask.FromResult(saved);
        }
    }

    private static void RequireUnique<TOwner>(ImmutableDictionary<string, TOwner> index, string key, TOwner owner, string errorCode, string what)
    {
        if (index.TryGetValue(key, out var existing) && !EqualityComparer<TOwner>.Default.Equals(existing, owner))
        {
            throw new RuleViolationException(errorCode, $"{what} '{key}' is already in use in this tenant.");
        }
    }

    private TenantState State(TenantId tenantId) =>
        _tenants.TryGetValue(tenantId, out var state) ? state : throw new UnknownTenantException(tenantId);

    private sealed class TenantState(TenantRecord tenant)
    {
        public Lock Lock { get; } = new();

        public TenantRecord Tenant { get; set; } = tenant;

        public ImmutableDictionary<AccountId, CustomerAccount> Accounts { get; set; } = ImmutableDictionary<AccountId, CustomerAccount>.Empty;

        public ImmutableDictionary<string, AccountId> AccountsByExternalId { get; set; } = ImmutableDictionary.Create<string, AccountId>(StringComparer.Ordinal);

        public ImmutableDictionary<ObligationId, ObligationLocation> ObligationsById { get; set; } = ImmutableDictionary<ObligationId, ObligationLocation>.Empty;

        public ImmutableDictionary<string, ObligationLocation> ObligationsByExternalId { get; set; } = ImmutableDictionary.Create<string, ObligationLocation>(StringComparer.Ordinal);

        public ImmutableDictionary<string, ObligationLocation> ObligationsByReference { get; set; } = ImmutableDictionary.Create<string, ObligationLocation>(StringComparer.Ordinal);
    }
}
