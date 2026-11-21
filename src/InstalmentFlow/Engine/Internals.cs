using System.Collections.Concurrent;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Engine;

internal sealed class EngineServices(
    ITenantStore tenants,
    ILedgerStore ledger,
    TimeProvider timeProvider,
    IReadOnlyDictionary<TenantId, IBusinessCalendar> calendars,
    IPaymentReferenceResolver? referenceResolver,
    int maxAttempts,
    IReadOnlyDictionary<StrategyRef, IAllocationStrategy> strategies)
{
    private readonly ConcurrentDictionary<string, TimeZoneInfo> _timeZones = new(StringComparer.Ordinal);

    public ITenantStore TenantStore { get; } = tenants;

    public ILedgerStore LedgerStore { get; } = ledger;

    public IPaymentReferenceResolver? ReferenceResolver { get; } = referenceResolver;

    public int MaxAttempts { get; } = maxAttempts;

    public IAllocationStrategy Strategy(StrategyRef reference) =>
        strategies.GetValueOrDefault(reference)
        ?? throw new RuleViolationException(ErrorCodes.UnknownAllocationStrategy, $"Allocation strategy {reference} is not registered.");

    /// <summary>Validates a policy, including that its allocation strategy is registered.</summary>
    public void ValidatePolicy(TenantPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        _ = Strategy(policy.Allocation);
    }

    public TimeProvider TimeProvider { get; } = timeProvider;

    public IBusinessCalendar CalendarFor(TenantId tenantId) => calendars.GetValueOrDefault(tenantId) ?? BusinessCalendars.None;

    public TenantClock ClockFor(TenantRecord tenant) =>
        new(_timeZones.GetOrAdd(tenant.TimeZone, TimeZoneInfo.FindSystemTimeZoneById), TimeProvider);
}

internal sealed class TenantAdmin(EngineServices services) : ITenantAdmin
{
    public async Task<TenantView> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.TenantId);
        ArgumentNullException.ThrowIfNull(request.BaseCurrency);
        ArgumentNullException.ThrowIfNull(request.Policy);
        ValidateTimeZone(request.TimeZone);
        services.ValidatePolicy(request.Policy);

        var record = new TenantRecord(
            request.TenantId,
            request.BaseCurrency,
            request.TimeZone,
            [new TenantPolicyVersion(1, request.Policy, services.TimeProvider.GetUtcNow(), RequireActor(request.CreatedBy))]);
        await services.TenantStore.CreateAsync(record, cancellationToken).ConfigureAwait(false);
        return ToView(record);
    }

    public async Task<TenantView> UpdatePolicyAsync(TenantId tenantId, Func<TenantPolicy, TenantPolicy> change, string changedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(change);
        var actor = RequireActor(changedBy);
        _ = await GetRecordAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var updated = await services.TenantStore.AppendPolicyAsync(
            tenantId,
            current =>
            {
                var policy = change(current.Current.Policy) ?? throw new RuleViolationException(ErrorCodes.InvalidRequest, "The policy change returned null.");
                services.ValidatePolicy(policy);
                return new TenantPolicyVersion(current.Current.Version + 1, policy, services.TimeProvider.GetUtcNow(), actor);
            },
            cancellationToken).ConfigureAwait(false);
        return ToView(updated);
    }

    public async Task<TenantView> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
        ToView(await GetRecordAsync(tenantId, cancellationToken).ConfigureAwait(false));

    internal static void ValidateTimeZone(string timeZone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, $"Unknown time zone '{timeZone}'.");
        }
    }

    private async Task<TenantRecord> GetRecordAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await services.TenantStore.GetAsync(tenantId, cancellationToken).ConfigureAwait(false) ?? throw new UnknownTenantException(tenantId);

    private static string RequireActor(string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return actor;
    }

    private static TenantView ToView(TenantRecord record) =>
        new(record.Id, record.BaseCurrency, record.TimeZone, record.Current, record.Policies);
}

internal sealed class TenantScopedFlow : IInstalmentFlow
{
    public TenantScopedFlow(TenantId tenant, EngineServices services)
    {
        Tenant = tenant;
        var context = new TenantContext(tenant, services);
        Accounts = new AccountsService(context);
        Obligations = new ObligationsService(context);
        Plans = new PlansService(context);
        Payments = new PaymentsService(context);
        Allocations = new AllocationsService(context);
        Exceptions = new ExceptionsService(context);
        Charges = new ChargesService(context);
        Refunds = new RefundsService(context);
        Queries = new QueriesService(context);
    }

    public TenantId Tenant { get; }

    public IAccounts Accounts { get; }

    public IObligations Obligations { get; }

    public IPlans Plans { get; }

    public IPayments Payments { get; }

    public IAllocations Allocations { get; }

    public IPaymentExceptions Exceptions { get; }

    public ICharges Charges { get; }

    public IRefunds Refunds { get; }

    public IInstalmentQueries Queries { get; }
}

/// <summary>Everything a tenant-scoped operation needs, loaded fresh for each call.</summary>
internal sealed record TenantSnapshot(TenantRecord Tenant, TenantClock Clock, IBusinessCalendar Calendar)
{
    public TenantPolicy Policy => Tenant.Current.Policy;
}

internal sealed class TenantContext(TenantId tenantId, EngineServices services)
{
    public TenantId TenantId { get; } = tenantId;

    public EngineServices Services { get; } = services;

    public ILedgerStore Store => Services.LedgerStore;

    public TimeProvider Time => Services.TimeProvider;

    public async Task<TenantSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var tenant = await Services.TenantStore.GetAsync(TenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new UnknownTenantException(TenantId);
        return new TenantSnapshot(tenant, Services.ClockFor(tenant), Services.CalendarFor(TenantId));
    }

    public async Task<Domain.CustomerAccount> LoadAccountAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var account = await Store.LoadAccountAsync(TenantId, accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Account", accountId.ToString());
        if (account.TenantId != TenantId)
        {
            throw new RuleViolationException(ErrorCodes.TenantMismatch, "The account belongs to a different tenant.");
        }

        return account;
    }

    public async Task<AccountId> ResolveAsync(AccountRef reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Id is { } id)
        {
            return id;
        }

        return await Store.FindAccountAsync(TenantId, reference.ExternalId!, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Account", reference.ToString());
    }

    public async Task<ObligationLocation> ResolveAsync(ObligationRef reference, TenantSnapshot tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string? normalisedReference = null;
        if (reference.PaymentReference is { } raw && !tenant.Policy.PaymentReference.TryNormalise(raw, out normalisedReference))
        {
            throw new NotFoundException("Obligation", reference.ToString());
        }

        var lookup = new ObligationLookup(reference.Id, reference.ExternalId, normalisedReference);
        return await Store.FindObligationAsync(TenantId, lookup, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Obligation", reference.ToString());
    }
}

internal static class Validate
{
    public const int MaxExternalIdLength = 128;

    public static IdempotencyKey Request(InstalmentFlowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new IdempotencyKey(request.IdempotencyKey);
    }

    public static string ExternalId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxExternalIdLength || value.Trim().Length != value.Length || value.Any(char.IsControl))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, $"{name} must be 1-{MaxExternalIdLength} characters with no surrounding whitespace.");
        }

        return value;
    }

    public static string Text(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new RuleViolationException(ErrorCodes.InvalidRequest, $"{name} is required.") : value;

    public static T Required<T>(T? value, string name)
        where T : class =>
        value ?? throw new RuleViolationException(ErrorCodes.InvalidRequest, $"{name} is required.");
}
