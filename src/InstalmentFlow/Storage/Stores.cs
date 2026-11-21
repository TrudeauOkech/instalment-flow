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

    public ImmutableArray<AllocationRecord> Allocations { get; init; } = [];

    public ImmutableArray<AllocationReversalRecord> AllocationReversals { get; init; } = [];

    public ImmutableArray<PaymentReversalRecord> PaymentReversals { get; init; } = [];

    /// <summary>Reversals parked until their payment arrives.</summary>
    public ImmutableArray<PendingReversal> NewPendingReversals { get; init; } = [];

    /// <summary>Parked reversals applied by this commit; each must still be pending.</summary>
    public ImmutableArray<ReversalId> AppliedPendingReversals { get; init; } = [];

    public ImmutableArray<ChargeRecord> Charges { get; init; } = [];

    public ImmutableArray<ChargeReversalRecord> ChargeReversals { get; init; } = [];

    public ImmutableArray<WaiverRecord> Waivers { get; init; } = [];

    /// <summary>New refunds (status Requested), or completed ones replacing a stored Requested refund.</summary>
    public ImmutableArray<RefundRecord> Refunds { get; init; } = [];

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

/// <summary>Another command recorded a reversal with the same provider reference first.</summary>
internal sealed class DuplicateReversalException(ExternalReference reference)
    : Exception($"A reversal with reference {reference} is already recorded.");

/// <summary>A recorded or parked reversal found by its provider reference.</summary>
internal sealed record ReversalLookup(PaymentReversalRecord? Recorded, PendingReversal? Pending);

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

    /// <summary>Every exception raised for a payment, oldest first.</summary>
    ValueTask<IReadOnlyList<PaymentExceptionRecord>> ListPaymentExceptionsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken);

    ValueTask<IdempotencyRecord?> GetIdempotencyAsync(TenantId tenantId, string key, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<AllocationRecord>> ListAllocationsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken);

    /// <summary>An account's allocations in the order they were recorded (LIFO unwinding depends on it).</summary>
    ValueTask<IReadOnlyList<AllocationRecord>> ListAccountAllocationsAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<AllocationReversalRecord>> ListAccountAllocationReversalsAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PaymentReversalRecord>> ListPaymentReversalsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken);

    ValueTask<ReversalLookup?> FindReversalAsync(TenantId tenantId, ExternalReference reference, CancellationToken cancellationToken);

    /// <summary>Parked reversals waiting for the payment with this provider reference, oldest first.</summary>
    ValueTask<IReadOnlyList<PendingReversal>> ListPendingReversalsAsync(TenantId tenantId, ExternalReference paymentSource, CancellationToken cancellationToken);

    ValueTask<ChargeRecord?> GetChargeAsync(TenantId tenantId, ChargeId chargeId, CancellationToken cancellationToken);

    ValueTask<ChargeReversalRecord?> GetChargeReversalAsync(TenantId tenantId, ChargeId chargeId, CancellationToken cancellationToken);

    ValueTask<RefundRecord?> GetRefundAsync(TenantId tenantId, RefundId refundId, CancellationToken cancellationToken);

    /// <summary>Refunds that use a payment's funds, oldest first.</summary>
    ValueTask<IReadOnlyList<RefundRecord>> ListPaymentRefundsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken);

    /// <summary>Every fact recorded for one account, with its journal transactions in journal order.</summary>
    ValueTask<AccountHistory> LoadHistoryAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="max"/> journal transactions after position <paramref name="after"/>, in order.</summary>
    ValueTask<IReadOnlyList<PositionedTransaction>> ReadJournalAsync(TenantId tenantId, long after, int max, CancellationToken cancellationToken);

    /// <summary>Everything stored for the tenant, for verification.</summary>
    ValueTask<LedgerSnapshot> SnapshotAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Applies a commit atomically.</summary>
    /// <exception cref="ConcurrencyConflictException">An aggregate changed since it was loaded.</exception>
    /// <exception cref="IdempotencyKeyTakenException">The idempotency key was committed concurrently.</exception>
    /// <exception cref="DuplicatePaymentException">The payment's source reference is already recorded.</exception>
    /// <exception cref="DuplicateReversalException">The reversal's provider reference is already recorded.</exception>
    /// <exception cref="RuleViolationException">A uniqueness or state rule would be broken.</exception>
    ValueTask CommitAsync(Commit commit, CancellationToken cancellationToken);
}

/// <summary>A journal transaction and its position in the tenant's journal (1 for the first).</summary>
internal sealed record PositionedTransaction(long Position, JournalTransaction Transaction);

/// <summary>Everything stored for one tenant, for invariant verification.</summary>
internal sealed record LedgerSnapshot(
    TenantRecord Tenant,
    ImmutableArray<CustomerAccount> Accounts,
    ImmutableArray<UnidentifiedFunds> Pools,
    ImmutableArray<PaymentRecord> Payments,
    ImmutableArray<PaymentAssignment> Assignments,
    ImmutableArray<AllocationRecord> Allocations,
    ImmutableArray<AllocationReversalRecord> AllocationReversals,
    ImmutableArray<PaymentReversalRecord> PaymentReversals,
    ImmutableArray<PendingReversal> PendingReversals,
    ImmutableArray<ChargeRecord> Charges,
    ImmutableArray<ChargeReversalRecord> ChargeReversals,
    ImmutableArray<WaiverRecord> Waivers,
    ImmutableArray<RefundRecord> Refunds,
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

    public ValueTask<IReadOnlyList<PaymentExceptionRecord>> ListPaymentExceptionsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<PaymentExceptionRecord>>(
            [.. State(tenantId).Data.Exceptions.Values.Where(e => e.PaymentId == paymentId).OrderBy(e => e.OpenedAt).ThenBy(e => e.Id.Value)]);

    public ValueTask<IdempotencyRecord?> GetIdempotencyAsync(TenantId tenantId, string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Data.Idempotency.GetValueOrDefault(key));

    public ValueTask<IReadOnlyList<AllocationRecord>> ListAllocationsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AllocationRecord>>([.. State(tenantId).Data.Allocations.Where(a => a.PaymentId == paymentId)]);

    public ValueTask<IReadOnlyList<AllocationRecord>> ListAccountAllocationsAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AllocationRecord>>([.. State(tenantId).Data.Allocations.Where(a => a.AccountId == accountId)]);

    public ValueTask<IReadOnlyList<AllocationReversalRecord>> ListAccountAllocationReversalsAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AllocationReversalRecord>>([.. State(tenantId).Data.AllocationReversals.Where(r => r.AccountId == accountId)]);

    public ValueTask<IReadOnlyList<PaymentReversalRecord>> ListPaymentReversalsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<PaymentReversalRecord>>([.. State(tenantId).Data.PaymentReversals.Where(r => r.PaymentId == paymentId)]);

    public ValueTask<ReversalLookup?> FindReversalAsync(TenantId tenantId, ExternalReference reference, CancellationToken cancellationToken)
    {
        var data = State(tenantId).Data;
        if (!data.ReversalsByReference.TryGetValue(reference, out var id))
        {
            return ValueTask.FromResult<ReversalLookup?>(null);
        }

        return ValueTask.FromResult<ReversalLookup?>(data.Pending.TryGetValue(id, out var pending)
            ? new ReversalLookup(null, pending)
            : new ReversalLookup(data.PaymentReversals.Single(r => r.Id == id), null));
    }

    public ValueTask<IReadOnlyList<PendingReversal>> ListPendingReversalsAsync(TenantId tenantId, ExternalReference paymentSource, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<PendingReversal>>(
            [.. State(tenantId).Data.Pending.Values.Where(p => p.PaymentSource == paymentSource).OrderBy(p => p.At).ThenBy(p => p.Id.Value)]);

    public ValueTask<ChargeRecord?> GetChargeAsync(TenantId tenantId, ChargeId chargeId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Data.Charges.GetValueOrDefault(chargeId));

    public ValueTask<ChargeReversalRecord?> GetChargeReversalAsync(TenantId tenantId, ChargeId chargeId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Data.ChargeReversals.GetValueOrDefault(chargeId));

    public ValueTask<RefundRecord?> GetRefundAsync(TenantId tenantId, RefundId refundId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(State(tenantId).Data.Refunds.GetValueOrDefault(refundId));

    public ValueTask<IReadOnlyList<RefundRecord>> ListPaymentRefundsAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<RefundRecord>>(
            [.. State(tenantId).Data.Refunds.Values.Where(r => r.Portions.Any(p => p.PaymentId == paymentId)).OrderBy(r => r.RequestedAt).ThenBy(r => r.Id.Value)]);

    public ValueTask<AccountHistory> LoadHistoryAsync(TenantId tenantId, AccountId accountId, CancellationToken cancellationToken)
    {
        var data = State(tenantId).Data;
        var charges = data.Charges.Values.Where(c => c.AccountId == accountId).ToImmutableArray();
        var assignments = data.Assignments.Values.Where(a => a.AccountId == accountId).ToImmutableArray();
        var assigned = assignments.Select(a => a.PaymentId).ToHashSet();
        return ValueTask.FromResult(new AccountHistory(
            [.. data.Journal.Select((tx, i) => new PositionedTransaction(i + 1, tx)).Where(p => p.Transaction.AccountId == accountId)],
            [.. data.Payments.Values.Where(p => p.AccountId == accountId || assigned.Contains(p.Id))],
            assignments,
            [.. data.Allocations.Where(a => a.AccountId == accountId)],
            [.. data.AllocationReversals.Where(r => r.AccountId == accountId)],
            [.. data.PaymentReversals.Where(r => r.AccountId == accountId)],
            charges,
            [.. charges.Select(c => data.ChargeReversals.GetValueOrDefault(c.Id)).OfType<ChargeReversalRecord>()],
            [.. data.Waivers.Where(w => w.AccountId == accountId)],
            [.. data.Refunds.Values.Where(r => r.AccountId == accountId)]));
    }

    public ValueTask<IReadOnlyList<PositionedTransaction>> ReadJournalAsync(TenantId tenantId, long after, int max, CancellationToken cancellationToken)
    {
        var journal = State(tenantId).Data.Journal;
        var start = (int)Math.Clamp(after, 0, journal.Count);
        var count = Math.Min(max, journal.Count - start);
        return ValueTask.FromResult<IReadOnlyList<PositionedTransaction>>(
            [.. Enumerable.Range(start, count).Select(i => new PositionedTransaction(i + 1, journal[i]))]);
    }

    public ValueTask<LedgerSnapshot> SnapshotAsync(TenantId tenantId, CancellationToken cancellationToken) => ValueTask.FromResult(Snapshot(tenantId));

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
            [.. data.Allocations],
            [.. data.AllocationReversals],
            [.. data.PaymentReversals],
            [.. data.Pending.Values],
            [.. data.Charges.Values],
            [.. data.ChargeReversals.Values],
            [.. data.Waivers],
            [.. data.Refunds.Values],
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

        ValidateReversals(commit, data);

        foreach (var reversal in commit.ChargeReversals)
        {
            if (!data.Charges.ContainsKey(reversal.ChargeId) || data.ChargeReversals.ContainsKey(reversal.ChargeId))
            {
                throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Charge {reversal.ChargeId} is unknown or already reversed.");
            }
        }

        foreach (var refund in commit.Refunds)
        {
            var stored = data.Refunds.GetValueOrDefault(refund.Id);
            var valid = refund.Status == RefundStatus.Requested ? stored is null : stored?.Status == RefundStatus.Requested;
            if (!valid)
            {
                throw new RuleViolationException(ErrorCodes.RefundNotPending, $"Refund {refund.Id} is not pending.");
            }
        }
    }

    private static void ValidateReversals(Commit commit, TenantData data)
    {
        var applied = commit.AppliedPendingReversals.ToHashSet();
        if (applied.Any(id => !data.Pending.ContainsKey(id)))
        {
            // Another command applied the parked reversal first; retry against fresh state.
            throw new ConcurrencyConflictException(default, CustomerAccount.NewVersion);
        }

        foreach (var reference in commit.PaymentReversals.Where(r => !applied.Contains(r.Id)).Select(r => r.Reference)
                     .Concat(commit.NewPendingReversals.Select(p => p.Reference)).OfType<ExternalReference>())
        {
            if (data.ReversalsByReference.ContainsKey(reference))
            {
                throw new DuplicateReversalException(reference);
            }
        }

        // Reversal before payment: parking and recording the payment must never miss each other.
        var recorded = commit.Payments.Select(p => p.Source).ToHashSet();
        foreach (var pending in commit.NewPendingReversals)
        {
            if (data.PaymentsBySource.ContainsKey(pending.PaymentSource) || recorded.Contains(pending.PaymentSource))
            {
                throw new ConcurrencyConflictException(default, CustomerAccount.NewVersion);
            }
        }

        if (data.Pending.Values.Any(p => recorded.Contains(p.PaymentSource) && !applied.Contains(p.Id)))
        {
            throw new ConcurrencyConflictException(default, CustomerAccount.NewVersion);
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

        data = data with
        {
            Allocations = data.Allocations.AddRange(commit.Allocations),
            AllocationReversals = data.AllocationReversals.AddRange(commit.AllocationReversals),
            PaymentReversals = data.PaymentReversals.AddRange(commit.PaymentReversals),
            Pending = data.Pending.RemoveRange(commit.AppliedPendingReversals).AddRange(commit.NewPendingReversals.Select(p => KeyValuePair.Create(p.Id, p))),
            ReversalsByReference = data.ReversalsByReference.SetItems(
                commit.PaymentReversals.Where(r => r.Reference is not null).Select(r => KeyValuePair.Create(r.Reference!, r.Id))
                    .Concat(commit.NewPendingReversals.Where(p => p.Reference is not null).Select(p => KeyValuePair.Create(p.Reference!, p.Id)))),
            Charges = data.Charges.AddRange(commit.Charges.Select(c => KeyValuePair.Create(c.Id, c))),
            ChargeReversals = data.ChargeReversals.AddRange(commit.ChargeReversals.Select(r => KeyValuePair.Create(r.ChargeId, r))),
            Waivers = data.Waivers.AddRange(commit.Waivers),
            Refunds = data.Refunds.SetItems(commit.Refunds.Select(r => KeyValuePair.Create(r.Id, r))),
        };

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
        ImmutableList<AllocationRecord> Allocations,
        ImmutableList<AllocationReversalRecord> AllocationReversals,
        ImmutableList<PaymentReversalRecord> PaymentReversals,
        ImmutableDictionary<ReversalId, PendingReversal> Pending,
        ImmutableDictionary<ExternalReference, ReversalId> ReversalsByReference,
        ImmutableDictionary<ChargeId, ChargeRecord> Charges,
        ImmutableDictionary<ChargeId, ChargeReversalRecord> ChargeReversals,
        ImmutableList<WaiverRecord> Waivers,
        ImmutableDictionary<RefundId, RefundRecord> Refunds,
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
            ImmutableList<AllocationRecord>.Empty,
            ImmutableList<AllocationReversalRecord>.Empty,
            ImmutableList<PaymentReversalRecord>.Empty,
            ImmutableDictionary<ReversalId, PendingReversal>.Empty,
            ImmutableDictionary<ExternalReference, ReversalId>.Empty,
            ImmutableDictionary<ChargeId, ChargeRecord>.Empty,
            ImmutableDictionary<ChargeId, ChargeReversalRecord>.Empty,
            ImmutableList<WaiverRecord>.Empty,
            ImmutableDictionary<RefundId, RefundRecord>.Empty,
            ImmutableDictionary<PaymentExceptionId, PaymentExceptionRecord>.Empty,
            ImmutableDictionary.Create<string, IdempotencyRecord>(StringComparer.Ordinal),
            ImmutableList<JournalTransaction>.Empty,
            ImmutableList<DomainEvent>.Empty);
    }
}
