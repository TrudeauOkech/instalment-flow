using System.Collections.Concurrent;
using InstalmentFlow.Engine;
using InstalmentFlow.Storage;

namespace InstalmentFlow;

/// <summary>
/// Entry point. <see cref="CreateInMemory"/> gives a fully working engine with no infrastructure, for tests,
/// samples and prototypes. <c>InstalmentFlow.EntityFrameworkCore</c> creates one over PostgreSQL.
/// </summary>
public sealed class InstalmentFlowFactory : IInstalmentFlowFactory
{
    private readonly EngineServices _services;

    private InstalmentFlowFactory(EngineServices services, InMemoryStore? inMemoryStore)
    {
        _services = services;
        InMemoryStore = inMemoryStore;
        Tenants = new TenantAdmin(services);
    }

    /// <inheritdoc />
    public ITenantAdmin Tenants { get; }

    /// <summary>The in-memory store, for invariant verification in tests.</summary>
    internal InMemoryStore? InMemoryStore { get; }

    internal ILedgerStore LedgerStore => _services.LedgerStore;

    /// <summary>Creates an engine backed by an in-memory store.</summary>
    /// <example>
    /// <code>
    /// var factory = InstalmentFlowFactory.CreateInMemory(b => b.AddTenant("acme-ke", Currency.KES, "Africa/Nairobi"));
    /// var flow = factory.ForTenant("acme-ke");
    /// </code>
    /// </example>
    public static InstalmentFlowFactory CreateInMemory(Action<InstalmentFlowBuilder>? configure = null)
    {
        var builder = new InstalmentFlowBuilder();
        configure?.Invoke(builder);

        var store = new InMemoryStore();
        var services = Services(builder, store, store);
        foreach (var record in TenantRecords(builder, services))
        {
            store.Create(record);
        }

        return new InstalmentFlowFactory(services, store);
    }

    /// <summary>
    /// Creates an engine over persistent stores, used by store packages such as <c>InstalmentFlow.EntityFrameworkCore</c>.
    /// Tenants added on the builder are created if they do not exist yet; existing ones are left as they are.
    /// </summary>
    internal static async Task<InstalmentFlowFactory> CreateAsync(
        Action<InstalmentFlowBuilder>? configure,
        ITenantStore tenants,
        ILedgerStore ledger,
        CancellationToken cancellationToken)
    {
        var builder = new InstalmentFlowBuilder();
        configure?.Invoke(builder);
        var services = Services(builder, tenants, ledger);
        foreach (var record in TenantRecords(builder, services))
        {
            if (await tenants.GetAsync(record.Id, cancellationToken).ConfigureAwait(false) is null)
            {
                try
                {
                    await tenants.CreateAsync(record, cancellationToken).ConfigureAwait(false);
                }
                catch (RuleViolationException ex) when (ex.ErrorCode == ErrorCodes.DuplicateTenant)
                {
                    // Another process created it first.
                }
            }
        }

        return new InstalmentFlowFactory(services, null);
    }

    private static EngineServices Services(InstalmentFlowBuilder builder, ITenantStore tenants, ILedgerStore ledger) =>
        new(tenants, ledger, new MicrosecondClock(builder.TimeProvider), builder.Calendars, builder.ReferenceResolver, builder.MaxAttempts, builder.Strategies);

    private static IEnumerable<TenantRecord> TenantRecords(InstalmentFlowBuilder builder, EngineServices services)
    {
        foreach (var tenant in builder.Tenants)
        {
            TenantAdmin.ValidateTimeZone(tenant.TimeZone);
            services.ValidatePolicy(tenant.Policy);
            yield return new TenantRecord(
                tenant.TenantId,
                tenant.BaseCurrency,
                tenant.TimeZone,
                [new TenantPolicyVersion(1, tenant.Policy, Microseconds.Truncate(builder.TimeProvider.GetUtcNow()), "configuration")]);
        }
    }

    /// <inheritdoc />
    public IInstalmentFlow ForTenant(TenantId tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return new TenantScopedFlow(tenantId, _services);
    }
}

/// <summary>Configures an <see cref="InstalmentFlowFactory"/>.</summary>
public sealed class InstalmentFlowBuilder
{
    private readonly List<CreateTenantRequest> _tenants = [];

    internal InstalmentFlowBuilder()
    {
    }

    internal TimeProvider TimeProvider { get; private set; } = TimeProvider.System;

    internal ConcurrentDictionary<TenantId, IBusinessCalendar> Calendars { get; } = new();

    internal IReadOnlyList<CreateTenantRequest> Tenants => _tenants;

    internal IPaymentReferenceResolver? ReferenceResolver { get; private set; }

    internal int MaxAttempts { get; private set; } = CommandRunner.DefaultMaxAttempts;

    internal Dictionary<StrategyRef, IAllocationStrategy> Strategies { get; } = AllocationStrategies.All.ToDictionary(s => s.Ref);

    /// <summary>
    /// Registers a custom allocation strategy (ADR-006). Tenants select it by <see cref="StrategyRef"/> in their policy.
    /// Built-in names and versions cannot be replaced.
    /// </summary>
    public InstalmentFlowBuilder AddAllocationStrategy(IAllocationStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(strategy.Ref);
        if (!Strategies.TryAdd(strategy.Ref, strategy))
        {
            throw new ArgumentException($"An allocation strategy {strategy.Ref} is already registered.", nameof(strategy));
        }

        return this;
    }

    /// <summary>
    /// How many times a command is attempted when another command for the same customer commits first
    /// (default 10, with jittered backoff). Raise it only for unusually hot accounts.
    /// </summary>
    public InstalmentFlowBuilder UseConcurrencyRetries(int maxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAttempts, 100);
        MaxAttempts = maxAttempts;
        return this;
    }

    /// <summary>Uses <paramref name="timeProvider"/> for all clocks, for example a fake one in tests.</summary>
    public InstalmentFlowBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeProvider = timeProvider;
        return this;
    }

    /// <summary>Adds a tenant at start-up with policy version 1.</summary>
    public InstalmentFlowBuilder AddTenant(TenantId tenantId, Currency baseCurrency, string timeZone, Func<TenantPolicy, TenantPolicy>? policy = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(baseCurrency);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
        _tenants.Add(new CreateTenantRequest
        {
            TenantId = tenantId,
            BaseCurrency = baseCurrency,
            TimeZone = timeZone,
            Policy = policy?.Invoke(TenantPolicy.Default) ?? TenantPolicy.Default,
            CreatedBy = "configuration",
        });
        return this;
    }

    /// <summary>
    /// Adds candidate obligations or customers for payment references, for example legacy account numbers
    /// (ADR-009). It is consulted for every payment that quotes a reference.
    /// </summary>
    public InstalmentFlowBuilder UsePaymentReferenceResolver(IPaymentReferenceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ReferenceResolver = resolver;
        return this;
    }

    /// <summary>Moves a tenant's due dates with <paramref name="calendar"/>, for example off public holidays.</summary>
    public InstalmentFlowBuilder UseBusinessCalendar(TenantId tenantId, IBusinessCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(calendar);
        Calendars[tenantId] = calendar;
        return this;
    }
}
