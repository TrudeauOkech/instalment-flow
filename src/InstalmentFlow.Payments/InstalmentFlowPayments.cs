using InstalmentFlow.Payments.Engine;
using InstalmentFlow.Payments.Storage;

namespace InstalmentFlow.Payments;

/// <summary>
/// Payment provider integration on top of an <see cref="InstalmentFlowFactory"/>: routing, the inbox, intents,
/// the intent resolver and reconciliation. The core only ever receives confirmed payments (ADR-016).
/// </summary>
public sealed class InstalmentFlowPayments
{
    private readonly PaymentsContext _context;

    private InstalmentFlowPayments(PaymentsContext context)
    {
        _context = context;
        Routing = new RoutingService(context);
        Inbox = context.Inbox;
        IntentResolver = new IntentResolverService(context);
        Reconciliation = new ReconciliationService(context);
    }

    /// <summary>Which provider accounts belong to which tenant.</summary>
    public IProviderRouting Routing { get; }

    /// <summary>The provider inbox and its processor.</summary>
    public IProviderInbox Inbox { get; }

    /// <summary>Resolves submitted and ambiguous intents.</summary>
    public IIntentResolver IntentResolver { get; }

    /// <summary>Reconciles provider statements with the engine.</summary>
    public IReconciliation Reconciliation { get; }

    internal IPaymentsStore Store => _context.Store;

    /// <summary>The adapter registered for <paramref name="provider"/>, or null.</summary>
    public IPaymentProviderAdapter? FindProvider(string provider) => _context.Settings.Providers.Find(provider);

    /// <summary>Inbox backlog, open intents and the last reconciliation per account, across tenants.</summary>
    public async Task<PaymentsHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        await _context.Store.GetHealthAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Routes a payment validation preview to its tenant, or null when it cannot be routed. Used by validation
    /// endpoints, which must not record anything.
    /// </summary>
    public async Task<TenantId?> RouteAsync(NormalisedPaymentEvent paymentEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentEvent);
        return await _context.Router.RouteAsync(paymentEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The underlying engine.</summary>
    public InstalmentFlowFactory Flows => _context.Flows;

    /// <summary>Payments with their state in memory, for tests and single-process hosts.</summary>
    public static InstalmentFlowPayments CreateInMemory(InstalmentFlowFactory flows, Action<PaymentsBuilder> configure) =>
        Create(flows, new InMemoryPaymentsStore(), configure);

    internal static InstalmentFlowPayments Create(InstalmentFlowFactory flows, IPaymentsStore store, Action<PaymentsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(flows);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new PaymentsBuilder();
        configure(builder);
        return new InstalmentFlowPayments(new PaymentsContext(flows, store, builder.Build()));
    }

    /// <summary>The payments features of one tenant.</summary>
    public ITenantPayments ForTenant(TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return new TenantPayments(tenant, new IntentService(_context, tenant), new DiscrepancyService(_context, tenant));
    }

    private sealed record TenantPayments(TenantId Tenant, IPaymentIntents Intents, IDiscrepancies Discrepancies) : ITenantPayments;
}

/// <summary>Configures <see cref="InstalmentFlowPayments"/>.</summary>
public sealed class PaymentsBuilder
{
    private readonly List<IPaymentProviderAdapter> _adapters = [];
    private readonly Dictionary<TenantId, ReversedAtProviderPolicy> _reversalPolicies = [];
    private ITenantRouter? _router;
    private IPayloadProtector? _protector;
    private TimeProvider _time = TimeProvider.System;

    internal PaymentsBuilder()
    {
    }

    /// <summary>Inbox processing settings.</summary>
    public InboxOptions Inbox { get; } = new();

    /// <summary>Payment intent settings.</summary>
    public IntentOptions Intents { get; } = new();

    /// <summary>Reconciliation settings.</summary>
    public ReconciliationOptions Reconciliation { get; } = new();

    /// <summary>
    /// Adds a provider. Collection, status queries and statements are used when the adapter implements
    /// <see cref="ICollectionInitiator"/>, <see cref="ITransactionStatusQuery"/> or <see cref="IStatementSource"/>;
    /// its <see cref="IPaymentProviderAdapter.Capabilities"/> must say the same.
    /// </summary>
    public PaymentsBuilder AddProvider(IPaymentProviderAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapters.Add(adapter);
        return this;
    }

    /// <summary>Routes events with a custom router instead of the provider account mappings.</summary>
    public PaymentsBuilder UseTenantRouter(ITenantRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        return this;
    }

    /// <summary>Protects raw provider payloads at rest.</summary>
    public PaymentsBuilder UsePayloadProtector(IPayloadProtector protector)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        return this;
    }

    /// <summary>The clock; use the same one as the <see cref="InstalmentFlowFactory"/>.</summary>
    public PaymentsBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        return this;
    }

    /// <summary>What to do about reversals only a tenant's statements show.</summary>
    public PaymentsBuilder UseReversedAtProviderPolicy(TenantId tenant, ReversedAtProviderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        _reversalPolicies[tenant] = policy;
        return this;
    }

    internal PaymentsSettings Build()
    {
        RequirePositive(Inbox.BatchSize, nameof(Inbox.BatchSize));
        RequirePositive(Inbox.MaxAttempts, nameof(Inbox.MaxAttempts));
        RequirePositive(Inbox.Lease, nameof(Inbox.Lease));
        RequirePositive(Inbox.RetryDelay, nameof(Inbox.RetryDelay));
        RequirePositive(Intents.InitiationTimeout, nameof(Intents.InitiationTimeout));
        RequirePositive(Intents.FirstCheck, nameof(Intents.FirstCheck));
        RequirePositive(Intents.TimeToLive, nameof(Intents.TimeToLive));
        RequirePositive(Reconciliation.DefaultWindow, nameof(Reconciliation.DefaultWindow));
        if (Reconciliation.Overlap < TimeSpan.Zero)
        {
            throw new InvalidOperationException("The reconciliation overlap cannot be negative.");
        }

        return new PaymentsSettings(
            ProviderRegistry.Create(_adapters), _router, _protector, _time, Inbox, Intents, Reconciliation,
            new Dictionary<TenantId, ReversedAtProviderPolicy>(_reversalPolicies));
    }

    private static void RequirePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"{name} must be positive.");
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{name} must be positive.");
        }
    }
}
