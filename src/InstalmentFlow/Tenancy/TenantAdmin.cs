namespace InstalmentFlow;

/// <summary>
/// Tenant administration. This is the only API that is not tenant-scoped; hosts should restrict it to
/// operators.
/// </summary>
public interface ITenantAdmin
{
    /// <summary>Creates a tenant with policy version 1.</summary>
    /// <exception cref="RuleViolationException">The tenant already exists (<c>IF5003</c>).</exception>
    Task<TenantView> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates a new policy version by applying <paramref name="change"/> to the current policy.</summary>
    Task<TenantView> UpdatePolicyAsync(TenantId tenantId, Func<TenantPolicy, TenantPolicy> change, string changedBy, CancellationToken cancellationToken = default);

    /// <summary>Returns a tenant and its policy history.</summary>
    /// <exception cref="UnknownTenantException">The tenant does not exist.</exception>
    Task<TenantView> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Creates a tenant.</summary>
public sealed record CreateTenantRequest
{
    /// <summary>The new tenant's ID.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The currency obligations use unless stated otherwise.</summary>
    public required Currency BaseCurrency { get; init; }

    /// <summary>The IANA time zone that defines business dates, for example <c>Africa/Nairobi</c>.</summary>
    public required string TimeZone { get; init; }

    /// <summary>The initial policy.</summary>
    public TenantPolicy Policy { get; init; } = TenantPolicy.Default;

    /// <summary>Who is creating the tenant, recorded on policy version 1.</summary>
    public string CreatedBy { get; init; } = "system";
}

/// <summary>A tenant and its policy history.</summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="BaseCurrency">The tenant's base currency.</param>
/// <param name="TimeZone">The tenant's IANA time zone.</param>
/// <param name="Policy">The current policy version.</param>
/// <param name="History">Every policy version, oldest first.</param>
public sealed record TenantView(
    TenantId TenantId,
    Currency BaseCurrency,
    string TimeZone,
    TenantPolicyVersion Policy,
    IReadOnlyList<TenantPolicyVersion> History);
