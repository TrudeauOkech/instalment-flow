namespace InstalmentFlow.Payments.Storage;

/// <summary>
/// Storage for the payments package: provider routing, the inbox, intents and reconciliation (ADR-017: internal,
/// implemented by first-party stores). Routing, the inbox and run records are not tenant data until routed; intents
/// and discrepancies belong to one tenant and are always read with it.
/// </summary>
internal interface IPaymentsStore
{
    ValueTask<ProviderAccountMapping?> GetMappingAsync(string provider, string providerAccount, CancellationToken cancellationToken);

    /// <summary>Adds the mapping; false if the provider account is already mapped (to any tenant).</summary>
    ValueTask<bool> TryAddMappingAsync(ProviderAccountMapping mapping, CancellationToken cancellationToken);

    ValueTask<bool> RemoveMappingAsync(string provider, string providerAccount, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ProviderAccountMapping>> ListMappingsAsync(CancellationToken cancellationToken);

    /// <summary>Stores the entry; false if one with the same provider and deduplication key exists.</summary>
    ValueTask<bool> TryAddInboxAsync(InboxEntry entry, CancellationToken cancellationToken);

    ValueTask<InboxEntry?> FindInboxAsync(string provider, string dedupeKey, CancellationToken cancellationToken);

    /// <summary>Claims up to <paramref name="max"/> pending entries that are due and not leased, oldest first, leasing them until <paramref name="leaseUntil"/>.</summary>
    ValueTask<IReadOnlyList<InboxEntry>> ClaimInboxAsync(DateTimeOffset now, DateTimeOffset leaseUntil, int max, CancellationToken cancellationToken);

    /// <summary>Writes an entry's processing outcome and releases its lease.</summary>
    ValueTask UpdateInboxAsync(InboxEntry entry, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<InboxEntry>> ListInboxAsync(InboxStatus status, int max, CancellationToken cancellationToken);

    ValueTask<InboxEntry?> GetInboxAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Stores the intent; false if the tenant already has one with its idempotency key.</summary>
    ValueTask<bool> TryAddIntentAsync(IntentRecord intent, CancellationToken cancellationToken);

    ValueTask<IntentRecord?> GetIntentAsync(TenantId tenant, Guid intentId, CancellationToken cancellationToken);

    ValueTask<IntentRecord?> FindIntentByKeyAsync(TenantId tenant, string idempotencyKey, CancellationToken cancellationToken);

    ValueTask<IntentRecord?> FindIntentByCorrelationAsync(TenantId tenant, string provider, string correlationId, CancellationToken cancellationToken);

    /// <summary>Replaces the intent if its stored version is <paramref name="expectedVersion"/>; the new record carries the next version.</summary>
    ValueTask<bool> UpdateIntentAsync(IntentRecord intent, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>Open intents whose next status check is due, across tenants: only their IDs.</summary>
    ValueTask<IReadOnlyList<(TenantId Tenant, Guid IntentId)>> ListDueIntentsAsync(DateTimeOffset now, int max, CancellationToken cancellationToken);

    ValueTask<ReconciliationRunRecord?> LastRunAsync(string provider, string providerAccount, CancellationToken cancellationToken);

    ValueTask AddRunAsync(ReconciliationRunRecord run, CancellationToken cancellationToken);

    /// <summary>Stores the discrepancy; false if the tenant already has one of this kind for the transaction (open or resolved).</summary>
    ValueTask<bool> TryAddDiscrepancyAsync(DiscrepancyRecord discrepancy, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<DiscrepancyRecord>> ListDiscrepanciesAsync(TenantId tenant, bool openOnly, CancellationToken cancellationToken);

    ValueTask<bool> ResolveDiscrepancyAsync(TenantId tenant, Guid discrepancyId, DateTimeOffset at, string resolution, CancellationToken cancellationToken);

    /// <summary>Counts across tenants, from data that holds no tenant details.</summary>
    ValueTask<PaymentsHealth> GetHealthAsync(CancellationToken cancellationToken);
}

/// <summary>The payments store for tests and single-process hosts: everything in memory, behind one lock.</summary>
internal sealed class InMemoryPaymentsStore : IPaymentsStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string, string), ProviderAccountMapping> _mappings = [];
    private readonly Dictionary<Guid, InboxEntry> _inbox = [];
    private readonly Dictionary<(string, string), Guid> _inboxKeys = [];
    private readonly Dictionary<(TenantId, Guid), IntentRecord> _intents = [];
    private readonly List<ReconciliationRunRecord> _runs = [];
    private readonly Dictionary<(TenantId, Guid), DiscrepancyRecord> _discrepancies = [];

    public ValueTask<ProviderAccountMapping?> GetMappingAsync(string provider, string providerAccount, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_mappings.GetValueOrDefault((provider, providerAccount)));
        }
    }

    public ValueTask<bool> TryAddMappingAsync(ProviderAccountMapping mapping, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_mappings.TryAdd((mapping.Provider, mapping.ProviderAccount), mapping));
        }
    }

    public ValueTask<bool> RemoveMappingAsync(string provider, string providerAccount, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_mappings.Remove((provider, providerAccount)));
        }
    }

    public ValueTask<IReadOnlyList<ProviderAccountMapping>> ListMappingsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<ProviderAccountMapping>>(
                [.. _mappings.Values.OrderBy(m => m.Provider, StringComparer.Ordinal).ThenBy(m => m.ProviderAccount, StringComparer.Ordinal)]);
        }
    }

    public ValueTask<bool> TryAddInboxAsync(InboxEntry entry, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_inboxKeys.TryAdd((entry.Provider, entry.DedupeKey), entry.Id))
            {
                return ValueTask.FromResult(false);
            }

            _inbox[entry.Id] = entry;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<InboxEntry?> FindInboxAsync(string provider, string dedupeKey, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_inboxKeys.TryGetValue((provider, dedupeKey), out var id) ? _inbox[id] : null);
        }
    }

    public ValueTask<IReadOnlyList<InboxEntry>> ClaimInboxAsync(DateTimeOffset now, DateTimeOffset leaseUntil, int max, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var claimed = _inbox.Values
                .Where(e => e.Status == InboxStatus.Pending && e.NextAttemptAt <= now && (e.LeaseUntil is null || e.LeaseUntil <= now))
                .OrderBy(e => e.ReceivedAt).ThenBy(e => e.Id)
                .Take(max)
                .Select(e => e with { LeaseUntil = leaseUntil })
                .ToList();
            foreach (var entry in claimed)
            {
                _inbox[entry.Id] = entry;
            }

            return ValueTask.FromResult<IReadOnlyList<InboxEntry>>(claimed);
        }
    }

    public ValueTask UpdateInboxAsync(InboxEntry entry, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _inbox[entry.Id] = entry with { LeaseUntil = null };
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<IReadOnlyList<InboxEntry>> ListInboxAsync(InboxStatus status, int max, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<InboxEntry>>(
                [.. _inbox.Values.Where(e => e.Status == status).OrderBy(e => e.ReceivedAt).ThenBy(e => e.Id).Take(max)]);
        }
    }

    public ValueTask<InboxEntry?> GetInboxAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_inbox.GetValueOrDefault(id));
        }
    }

    public ValueTask<bool> TryAddIntentAsync(IntentRecord intent, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_intents.Values.Any(i => i.Tenant == intent.Tenant && i.IdempotencyKey == intent.IdempotencyKey))
            {
                return ValueTask.FromResult(false);
            }

            _intents[(intent.Tenant, intent.Id)] = intent;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<IntentRecord?> GetIntentAsync(TenantId tenant, Guid intentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_intents.GetValueOrDefault((tenant, intentId)));
        }
    }

    public ValueTask<IntentRecord?> FindIntentByKeyAsync(TenantId tenant, string idempotencyKey, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_intents.Values.SingleOrDefault(i => i.Tenant == tenant && i.IdempotencyKey == idempotencyKey));
        }
    }

    public ValueTask<IntentRecord?> FindIntentByCorrelationAsync(TenantId tenant, string provider, string correlationId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_intents.Values.SingleOrDefault(i => i.Tenant == tenant && i.Provider == provider && i.CorrelationId == correlationId));
        }
    }

    public ValueTask<bool> UpdateIntentAsync(IntentRecord intent, long expectedVersion, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_intents.TryGetValue((intent.Tenant, intent.Id), out var current) || current.Version != expectedVersion)
            {
                return ValueTask.FromResult(false);
            }

            if (intent.CorrelationId is { } correlation &&
                _intents.Values.Any(i => i.Id != intent.Id && i.Tenant == intent.Tenant && i.Provider == intent.Provider && i.CorrelationId == correlation))
            {
                throw new InvalidOperationException($"Another intent already has correlation ID '{correlation}'.");
            }

            _intents[(intent.Tenant, intent.Id)] = intent with { Version = expectedVersion + 1 };
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<IReadOnlyList<(TenantId Tenant, Guid IntentId)>> ListDueIntentsAsync(DateTimeOffset now, int max, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<(TenantId, Guid)>>(
                [.. _intents.Values.Where(i => !i.IsTerminal && i.NextCheckAt <= now).OrderBy(i => i.NextCheckAt).ThenBy(i => i.Id).Take(max).Select(i => (i.Tenant, i.Id))]);
        }
    }

    public ValueTask<ReconciliationRunRecord?> LastRunAsync(string provider, string providerAccount, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_runs.Where(r => r.Provider == provider && r.ProviderAccount == providerAccount).MaxBy(r => r.To));
        }
    }

    public ValueTask AddRunAsync(ReconciliationRunRecord run, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _runs.Add(run);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<bool> TryAddDiscrepancyAsync(DiscrepancyRecord discrepancy, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_discrepancies.Values.Any(d => d.Tenant == discrepancy.Tenant && d.Provider == discrepancy.Provider && d.ProviderAccount == discrepancy.ProviderAccount &&
                                               d.TransactionId == discrepancy.TransactionId && d.Kind == discrepancy.Kind))
            {
                return ValueTask.FromResult(false);
            }

            _discrepancies[(discrepancy.Tenant, discrepancy.Id)] = discrepancy;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<IReadOnlyList<DiscrepancyRecord>> ListDiscrepanciesAsync(TenantId tenant, bool openOnly, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<DiscrepancyRecord>>(
                [.. _discrepancies.Values.Where(d => d.Tenant == tenant && (!openOnly || d.ResolvedAt is null)).OrderBy(d => d.DetectedAt).ThenBy(d => d.Id)]);
        }
    }

    public ValueTask<PaymentsHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var pending = _inbox.Values.Where(e => e.Status == InboxStatus.Pending).ToList();
            var open = _intents.Values.Where(i => !i.IsTerminal).ToList();
            var ambiguous = open.Where(i => i.Status is PaymentIntentStatus.Ambiguous or PaymentIntentStatus.Created).ToList();
            return ValueTask.FromResult(new PaymentsHealth(
                pending.Count, pending.Count == 0 ? null : pending.Min(e => e.ReceivedAt), _inbox.Values.Count(e => e.Status == InboxStatus.Failed),
                open.Count, ambiguous.Count, ambiguous.Count == 0 ? null : ambiguous.Min(i => i.CreatedAt),
                [.. _runs.GroupBy(r => (r.Provider, r.ProviderAccount)).Select(g => g.MaxBy(r => r.To)!)
                    .OrderBy(r => r.Provider, StringComparer.Ordinal).ThenBy(r => r.ProviderAccount, StringComparer.Ordinal)
                    .Select(r => new ReconciliationStatus(r.Provider, r.ProviderAccount, r.To, r.FinishedAt))]));
        }
    }

    public ValueTask<bool> ResolveDiscrepancyAsync(TenantId tenant, Guid discrepancyId, DateTimeOffset at, string resolution, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_discrepancies.TryGetValue((tenant, discrepancyId), out var found) || found.ResolvedAt is not null)
            {
                return ValueTask.FromResult(false);
            }

            _discrepancies[(tenant, discrepancyId)] = found with { ResolvedAt = at, Resolution = resolution };
            return ValueTask.FromResult(true);
        }
    }
}
