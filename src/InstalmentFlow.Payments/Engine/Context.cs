using InstalmentFlow.Payments.Storage;

namespace InstalmentFlow.Payments.Engine;

internal sealed record PaymentsSettings(
    ProviderRegistry Providers,
    ITenantRouter? Router,
    IPayloadProtector? Protector,
    TimeProvider Time,
    InboxOptions Inbox,
    IntentOptions Intents,
    ReconciliationOptions Reconciliation,
    IReadOnlyDictionary<TenantId, ReversedAtProviderPolicy> ReversalPolicies);

internal sealed class PaymentsContext
{
    public PaymentsContext(InstalmentFlowFactory flows, IPaymentsStore store, PaymentsSettings settings)
    {
        Flows = flows;
        Store = store;
        Settings = settings;
        Router = settings.Router ?? new MappingRouter(store);
        Inbox = new InboxService(this);
    }

    public InstalmentFlowFactory Flows { get; }

    public IPaymentsStore Store { get; }

    public PaymentsSettings Settings { get; }

    public ITenantRouter Router { get; }

    public InboxService Inbox { get; }

    public DateTimeOffset Now => Settings.Time.GetUtcNow();

    /// <summary>Doubles <paramref name="first"/> per earlier attempt, up to <paramref name="max"/>; integer arithmetic only.</summary>
    public static TimeSpan Backoff(TimeSpan first, TimeSpan max, int earlierAttempts)
    {
        var ticks = first.Ticks;
        for (var i = 0; i < earlierAttempts && ticks < max.Ticks; i++)
        {
            ticks *= 2;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, max.Ticks));
    }
}

/// <summary>The registered providers and the capabilities each one implements.</summary>
internal sealed class ProviderRegistry
{
    private readonly Dictionary<string, IPaymentProviderAdapter> _adapters;

    private ProviderRegistry(Dictionary<string, IPaymentProviderAdapter> adapters) => _adapters = adapters;

    public static ProviderRegistry Create(IEnumerable<IPaymentProviderAdapter> adapters)
    {
        var byName = new Dictionary<string, IPaymentProviderAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            var name = adapter.Provider;
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsUpper) || name.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Provider name '{name}' must be lower case, without ':'.");
            }

            if (!byName.TryAdd(name, adapter))
            {
                throw new InvalidOperationException($"Provider '{name}' is registered twice.");
            }

            Check(adapter, ProviderCapabilities.MerchantInitiatedCollection, adapter is ICollectionInitiator, nameof(ICollectionInitiator));
            Check(adapter, ProviderCapabilities.StatusQuery, adapter is ITransactionStatusQuery, nameof(ITransactionStatusQuery));
            Check(adapter, ProviderCapabilities.StatementSource, adapter is IStatementSource, nameof(IStatementSource));
        }

        return new ProviderRegistry(byName);
    }

    public IPaymentProviderAdapter Adapter(string provider) =>
        _adapters.GetValueOrDefault(provider) ?? throw new RuleViolationException(PaymentsErrorCodes.UnknownProvider, $"No adapter is registered for provider '{provider}'.");

    public ICollectionInitiator Initiator(string provider) =>
        Adapter(provider) as ICollectionInitiator
        ?? throw new RuleViolationException(PaymentsErrorCodes.CapabilityNotSupported, $"Provider '{provider}' does not support merchant-initiated collection.");

    public ITransactionStatusQuery? StatusQuery(string provider) => _adapters.GetValueOrDefault(provider) as ITransactionStatusQuery;

    public IStatementSource? Statement(string provider) => _adapters.GetValueOrDefault(provider) as IStatementSource;

    private static void Check(IPaymentProviderAdapter adapter, ProviderCapabilities capability, bool implemented, string contract)
    {
        var declared = adapter.Capabilities.HasFlag(capability);
        if (declared != implemented)
        {
            throw new InvalidOperationException(declared
                ? $"Provider '{adapter.Provider}' declares {capability} but does not implement {contract}."
                : $"Provider '{adapter.Provider}' implements {contract} but does not declare {capability}.");
        }
    }
}

internal sealed class MappingRouter(IPaymentsStore store) : ITenantRouter
{
    public async ValueTask<TenantId?> RouteAsync(NormalisedPaymentEvent paymentEvent, CancellationToken cancellationToken) =>
        (await store.GetMappingAsync(paymentEvent.Provider, paymentEvent.ProviderAccount, cancellationToken).ConfigureAwait(false))?.Tenant;
}

internal sealed class RoutingService(PaymentsContext context) : IProviderRouting
{
    public async Task<ProviderAccountMapping> MapAsync(string provider, string providerAccount, TenantId tenant, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerAccount);
        ArgumentNullException.ThrowIfNull(tenant);
        _ = await context.Flows.Tenants.GetAsync(tenant, cancellationToken).ConfigureAwait(false);   // the tenant must exist

        var mapping = new ProviderAccountMapping(provider, providerAccount, tenant, context.Now);
        if (await context.Store.TryAddMappingAsync(mapping, cancellationToken).ConfigureAwait(false))
        {
            return mapping;
        }

        var existing = await context.Store.GetMappingAsync(provider, providerAccount, cancellationToken).ConfigureAwait(false);
        return existing is not null && existing.Tenant == tenant
            ? existing
            : throw new RuleViolationException(PaymentsErrorCodes.ProviderAccountTaken, $"Provider account {provider}:{providerAccount} is routed to another tenant.");
    }

    public async Task<bool> RemoveAsync(string provider, string providerAccount, CancellationToken cancellationToken = default) =>
        await context.Store.RemoveMappingAsync(provider, providerAccount, cancellationToken).ConfigureAwait(false);

    public async Task<TenantId?> ResolveAsync(string provider, string providerAccount, CancellationToken cancellationToken = default) =>
        (await context.Store.GetMappingAsync(provider, providerAccount, cancellationToken).ConfigureAwait(false))?.Tenant;

    public async Task<IReadOnlyList<ProviderAccountMapping>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.Store.ListMappingsAsync(cancellationToken).ConfigureAwait(false);
}
