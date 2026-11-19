using System.Collections.Immutable;
using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;

namespace InstalmentFlow.Storage;

// Storage ports are internal until milestone M7, when the PostgreSQL store proves their shape. Every method takes
// the tenant explicitly (ADR-007): there is no ambient tenant.

internal sealed record TenantRecord(TenantId Id, Currency BaseCurrency, string TimeZone, ImmutableArray<TenantPolicyVersion> Policies)
{
    public TenantPolicyVersion Current => Policies[^1];
}

internal sealed record ObligationLookup(ObligationId? Id, string? ExternalId, string? PaymentReference);

internal readonly record struct ObligationLocation(AccountId AccountId, ObligationId ObligationId);

/// <summary>A stored command result, keyed by idempotency key (invariant I-4).</summary>
internal sealed record IdempotencyRecord(string Key, string CommandType, ImmutableArray<byte> PayloadHash, object Result, DateTimeOffset CreatedAt);

/// <summary>
/// Everything one command changes, applied atomically or not at all (doc 03 §5: one command, one transaction,
/// at most one journal transaction). Aggregates carry the version they were loaded at; the store checks and bumps it.
/// </summary>
internal sealed record Commit
{
    public required TenantId Tenant { get; init; }

    public ImmutableArray<CustomerAccount> Accounts { get; init; } = [];

    public ImmutableArray<UnidentifiedFunds> Pools { get; init; } = [];

    public JournalTransaction? Journal { get; init; }

    public ImmutableArray<PaymentRecord> Payments { get; init; } = [];

    public ImmutableArray<PaymentAssignment> Assignments { get; init; } = [];

    public ImmutableArray<PaymentExceptionRecord> NewExceptions { get; init; } = [];

    public ImmutableArray<PaymentExceptionRecord> ResolvedExceptions { get; init; } = [];

    public IdempotencyRecord? Idempotency { get; init; }

    public ImmutableArray<DomainEvent> Events { get; init; } = [];
}

/// <summary>Another command recorded a payment with the same source and external reference first (I-3).</summary>
internal sealed class DuplicatePaymentException(ExternalReference reference)
    : Exception($"A payment with reference {reference} is already recorded.")
{
    public ExternalReference Reference { get; } = reference;
}

/// <summary>Another command committed the same idempotency key first.</summary>
internal sealed class IdempotencyKeyTakenException(string key) : Exception($"Idempotency key '{key}' was committed concurrently.");

internal interface ITenantStore
{
    ValueTask<TenantRecord?> GetAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <exception cref="RuleViolationException">The tenant exists (<c>IF5003</c>).</exception>
    ValueTask CreateAsync(TenantRecord tenant, CancellationToken cancellationToken);

    /// <summary>Atomically appends the policy version produced from the current record.</summary>
    ValueTask<TenantRecord> AppendPolicyAsync(TenantId tenantId, Func<TenantRecord, TenantPolicyVersion> next, CancellationToken cancellationToken);
}

internal interface ILedgerStore
{
    ValueTask<CustomerAccount?> LoadAccountAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken);

    ValueTask<AccountId?> FindAccountAsync(TenantId tenantId, string externalId, CancellationToken cancellationToken);

    ValueTask<ObligationLocation?> FindObligationAsync(TenantId tenantId, ObligationLookup lookup, CancellationToken cancellationToken);

    /// <summary>The unidentified funds for a source; an empty aggregate at version 0 if there are none yet.</summary>
    ValueTask<UnidentifiedFunds> LoadUnidentifiedAsync(TenantId tenantId, string source, CancellationToken cancellationToken);

    ValueTask<PaymentRecord?> GetPaymentAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken);

    ValueTask<PaymentRecord?> FindPaymentAsync(TenantId tenantId, ExternalReference source, CancellationToken cancellationToken);

    ValueTask<PaymentAssignment?> GetAssignmentAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken);

    ValueTask<PaymentExceptionRecord?> GetExceptionAsync(TenantId tenantId, PaymentExceptionId exceptionId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PaymentExceptionRecord>> ListExceptionsAsync(TenantId tenantId, CancellationToken cancellationToken);

    ValueTask<IdempotencyRecord?> GetIdempotencyAsync(TenantId tenantId, string key, CancellationToken cancellationToken);

    /// <summary>Applies a commit atomically.</summary>
    /// <exception cref="ConcurrencyConflictException">An aggregate changed since it was loaded.</exception>
    /// <exception cref="IdempotencyKeyTakenException">The idempotency key was committed concurrently.</exception>
    /// <exception cref="DuplicatePaymentException">The payment's source reference is already recorded.</exception>
    /// <exception cref="RuleViolationException">A uniqueness or state rule would be broken.</exception>
    ValueTask CommitAsync(Commit commit, CancellationToken cancellationToken);
}

/// <summary>Everything stored for one tenant, for invariant verification.</summary>
internal sealed record LedgerSnapshot(
    TenantRecord Tenant,
    ImmutableArray<CustomerAccount> Accounts,
    ImmutableArray<UnidentifiedFunds> Pools,
    ImmutableArray<PaymentRecord> Payments,
    ImmutableArray<PaymentAssignment> Assignments,
    ImmutableArray<PaymentExceptionRecord> Exceptions,
    ImmutableArray<JournalTransaction> Journal,
    ImmutableArray<DomainEvent> Events);

/// <summary>
/// Thread-safe in-memory store. It holds immutable snapshots and applies each commit under the tenant's lock after
/// validating all of it, so readers never observe a half-applied command.
/// </summary>
internal sealed class InMemoryStore : ITenantStore, ILedgerStore
{
    private readonly Lock _tenantsLock = new();
    private ImmutableDictionary<TenantId, TenantState> _tenants = ImmutableDictionary<TenantId, TenantState>.Empty;

    // ----- Tenants -----

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

    // ----- Reads -----

    public ValueTask<CustomerAccount?> LoadAccountAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Data.Accounts.GetValueOrDefault(accountId));

    public ValueTask<AccountId?> FindAccountAsync(TenantId tenantId, string externalId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Data.AccountsByExternalId.TryGetValue(externalId, out var id) ? id : (AccountId?)null);

    public ValueTask<ObligationLocation?> FindObligationAsync(TenantId tenantId, ObligationLookup lookup, CancellationToken cancellationToken)
    {
        var data = State(tenantId).Data;
        ObligationLocation? location = null;
        if (lookup.Id is { } id && data.ObligationsById.TryGetValue(id, out var byId))
        {
            location = byId;
        }
        else if (lookup.ExternalId is { } externalId && data.ObligationsByExternalId.TryGetValue(externalId, out var byExternal))
        {
            location = byExternal;
        }
        else if (lookup.PaymentReference is { } reference && data.ObligationsByReference.TryGetValue(reference, out var byReference))
        {
            location = byReference;
        }

        return ValueTask.FromResult(location);
    }

    public ValueTask<UnidentifiedFunds> LoadUnidentifiedAsync(TenantId tenantId, string source, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Data.Pools.GetValueOrDefault(source) ?? UnidentifiedFunds.Empty(tenantId, source));

    public ValueTask<PaymentRecord?> GetPaymentAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Data.Payments.GetValueOrDefault(paymentId));

    public ValueTask<PaymentRecord?> FindPaymentAsync(TenantId tenantId, ExternalReference source, CancellationToken cancellationToken)
    {
        var data = State(tenantId).Data;
        return ValueTask.FromResult(data.PaymentsBySource.TryGetValue(source, out var id) ? data.Payments[id] : null);
    }

    public ValueTask<PaymentAssignment?> GetAssignmentAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Data.Assignments.GetValueOrDefault(paymentId));

    public ValueTask<PaymentExceptionRecord?> GetExceptionAsync(TenantId tenantId, PaymentExceptionId exceptionId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Data.Exceptions.GetValueOrDefault(exceptionId));

    public ValueTask<IReadOnlyList<PaymentExceptionRecord>> ListExceptionsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<PaymentExceptionRecord>>([.. State(tenantId).Data.Exceptions.Values.OrderBy(e => e.OpenedAt).ThenBy(e => e.Id.Value)]);

    public ValueTask<IdempotencyRecord?> GetIdempotencyAsync(TenantId tenantId, string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Data.Idempotency.GetValueOrDefault(key));

    public LedgerSnapshot Snapshot(TenantId tenantId)
    {
        var state = State(tenantId);
        var data = state.Data;
        return new LedgerSnapshot(
            state.Tenant,
            [.. data.Accounts.Values],
            [.. data.Pools.Values],
            [.. data.Payments.Values],
            [.. data.Assignments.Values],
            [.. data.Exceptions.Values],
            [.. data.Journal],
            [.. data.Events]);
    }

    // ----- Writes -----

    public ValueTask CommitAsync(Commit commit, CancellationToken cancellationToken)
    {
        var state = State(commit.Tenant);
        lock (state.Lock)
        {
            var data = state.Data;
            Validate(commit, data);
            state.Data = Apply(commit, data);
        }

        return ValueTask.CompletedTask;
    }

    private static void Validate(Commit commit, TenantData data)
    {
        // Invariant I-18: a commit may only touch its own tenant.
        if (commit.Accounts.Any(a => a.TenantId != commit.Tenant) || commit.Pools.Any(p => p.TenantId != commit.Tenant) ||
            (commit.Journal is not null && commit.Journal.Tenant != commit.Tenant))
        {
            throw new RuleViolationException(ErrorCodes.TenantMismatch, "A commit touched another tenant's data.");
        }

        if (commit.Idempotency is { } idempotency && data.Idempotency.ContainsKey(idempotency.Key))
        {
            throw new IdempotencyKeyTakenException(idempotency.Key);
        }

        foreach (var account in commit.Accounts)
        {
            var stored = data.Accounts.GetValueOrDefault(account.Id);
            if ((stored?.Version ?? CustomerAccount.NewVersion) != account.Version)
            {
                throw new ConcurrencyConflictException(account.Id, account.Version);
            }

            RequireUnique(data.AccountsByExternalId, account.ExternalId, account.Id, ErrorCodes.DuplicateExternalId, "Account external ID");
            foreach (var obligation in account.Obligations)
            {
                var location = new ObligationLocation(account.Id, obligation.Id);
                RequireUnique(data.ObligationsByExternalId, obligation.ExternalId, location, ErrorCodes.DuplicateExternalId, "Obligation external ID");
                RequireUnique(data.ObligationsByReference, obligation.PaymentReference, location, ErrorCodes.DuplicatePaymentReference, "Payment reference");
            }
        }

        foreach (var pool in commit.Pools)
        {
            var stored = data.Pools.GetValueOrDefault(pool.Source);
            if ((stored?.Version ?? CustomerAccount.NewVersion) != pool.Version)
            {
                throw new ConcurrencyConflictException(default, pool.Version);
            }
        }

        foreach (var payment in commit.Payments)
        {
            if (data.PaymentsBySource.ContainsKey(payment.Source))
            {
                throw new DuplicatePaymentException(payment.Source);
            }
        }

        foreach (var resolved in commit.ResolvedExceptions)
        {
            if (!data.Exceptions.TryGetValue(resolved.Id, out var stored) || stored.Resolution is not null || resolved.Resolution is null)
            {
                throw new RuleViolationException(ErrorCodes.ExceptionNotResolvable, $"Payment exception {resolved.Id} is not open.");
            }
        }

        if (commit.Assignments.Any(a => data.Assignments.ContainsKey(a.PaymentId)))
        {
            throw new RuleViolationException(ErrorCodes.ExceptionNotResolvable, "The payment has already been assigned.");
        }
    }

    private static TenantData Apply(Commit commit, TenantData data)
    {
        foreach (var account in commit.Accounts)
        {
            var saved = account with { Version = account.Version + 1 };
            data = data with
            {
                Accounts = data.Accounts.SetItem(saved.Id, saved),
                AccountsByExternalId = data.AccountsByExternalId.SetItem(saved.ExternalId, saved.Id),
            };
            foreach (var obligation in saved.Obligations)
            {
                var location = new ObligationLocation(saved.Id, obligation.Id);
                data = data with
                {
                    ObligationsById = data.ObligationsById.SetItem(obligation.Id, location),
                    ObligationsByExternalId = data.ObligationsByExternalId.SetItem(obligation.ExternalId, location),
                    ObligationsByReference = data.ObligationsByReference.SetItem(obligation.PaymentReference, location),
                };
            }
        }

        foreach (var pool in commit.Pools)
        {
            data = data with { Pools = data.Pools.SetItem(pool.Source, pool with { Version = pool.Version + 1 }) };
        }

        foreach (var payment in commit.Payments)
        {
            data = data with
            {
                Payments = data.Payments.Add(payment.Id, payment),
                PaymentsBySource = data.PaymentsBySource.Add(payment.Source, payment.Id),
            };
        }

        foreach (var assignment in commit.Assignments)
        {
            data = data with { Assignments = data.Assignments.Add(assignment.PaymentId, assignment) };
        }

        foreach (var exception in commit.NewExceptions.Concat(commit.ResolvedExceptions))
        {
            data = data with { Exceptions = data.Exceptions.SetItem(exception.Id, exception) };
        }

        if (commit.Journal is { } journal)
        {
            data = data with { Journal = data.Journal.Add(journal) };
        }

        if (commit.Idempotency is { } idempotency)
        {
            data = data with { Idempotency = data.Idempotency.Add(idempotency.Key, idempotency) };
        }

        return data with { Events = data.Events.AddRange(commit.Events) };
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

        /// <summary>Replaced as a whole on each commit, so readers always see a consistent snapshot.</summary>
        public TenantData Data { get; set; } = TenantData.Empty;
    }

    private sealed record TenantData(
        ImmutableDictionary<AccountId, CustomerAccount> Accounts,
        ImmutableDictionary<string, AccountId> AccountsByExternalId,
        ImmutableDictionary<ObligationId, ObligationLocation> ObligationsById,
        ImmutableDictionary<string, ObligationLocation> ObligationsByExternalId,
        ImmutableDictionary<string, ObligationLocation> ObligationsByReference,
        ImmutableDictionary<string, UnidentifiedFunds> Pools,
        ImmutableDictionary<PaymentId, PaymentRecord> Payments,
        ImmutableDictionary<ExternalReference, PaymentId> PaymentsBySource,
        ImmutableDictionary<PaymentId, PaymentAssignment> Assignments,
        ImmutableDictionary<PaymentExceptionId, PaymentExceptionRecord> Exceptions,
        ImmutableDictionary<string, IdempotencyRecord> Idempotency,
        ImmutableList<JournalTransaction> Journal,
        ImmutableList<DomainEvent> Events)
    {
        public static TenantData Empty { get; } = new(
            ImmutableDictionary<AccountId, CustomerAccount>.Empty,
            ImmutableDictionary.Create<string, AccountId>(StringComparer.Ordinal),
            ImmutableDictionary<ObligationId, ObligationLocation>.Empty,
            ImmutableDictionary.Create<string, ObligationLocation>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, ObligationLocation>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, UnidentifiedFunds>(StringComparer.Ordinal),
            ImmutableDictionary<PaymentId, PaymentRecord>.Empty,
            ImmutableDictionary<ExternalReference, PaymentId>.Empty,
            ImmutableDictionary<PaymentId, PaymentAssignment>.Empty,
            ImmutableDictionary<PaymentExceptionId, PaymentExceptionRecord>.Empty,
            ImmutableDictionary.Create<string, IdempotencyRecord>(StringComparer.Ordinal),
            ImmutableList<JournalTransaction>.Empty,
            ImmutableList<DomainEvent>.Empty);
    }
}
