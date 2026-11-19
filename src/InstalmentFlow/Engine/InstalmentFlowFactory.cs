using System.Collections.Concurrent;
using InstalmentFlow.Engine;
using InstalmentFlow.Storage;

namespace InstalmentFlow;

/// <summary>
/// Entry point. <see cref="CreateInMemory"/> gives a fully working engine with no infrastructure, for tests,
/// samples and prototypes. Persistent stores arrive with <c>InstalmentFlow.EntityFrameworkCore</c> (milestone M7).
/// </summary>
public sealed class InstalmentFlowFactory : IInstalmentFlowFactory
{
    private readonly EngineServices _services;

    private InstalmentFlowFactory(EngineServices services)
    {
        _services = services;
        Tenants = new TenantAdmin(services);
    }

    /// <inheritdoc />
    public ITenantAdmin Tenants { get; }

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
        var services = new EngineServices(store, store, builder.TimeProvider, builder.Calendars);
        foreach (var tenant in builder.Tenants)
        {
            TenantAdmin.ValidateTimeZone(tenant.TimeZone);
            var record = new TenantRecord(
                tenant.TenantId,
                tenant.BaseCurrency,
                tenant.TimeZone,
                [new TenantPolicyVersion(1, tenant.Policy, builder.TimeProvider.GetUtcNow(), "configuration")]);
            tenant.Policy.Validate();
            store.Create(record);
        }

        return new InstalmentFlowFactory(services);
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

    /// <summary>Moves a tenant's due dates with <paramref name="calendar"/>, for example off public holidays.</summary>
    public InstalmentFlowBuilder UseBusinessCalendar(TenantId tenantId, IBusinessCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(calendar);
        Calendars[tenantId] = calendar;
        return this;
    }
}
