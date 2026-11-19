namespace InstalmentFlow;

/// <summary>
/// A tenant's configurable rules (ADR-007). Policies are versioned: every change creates a new version and
/// commands record the version they used, so a change never alters history. Later milestones add allocation,
/// identification and charge settings as further properties.
/// </summary>
public sealed record TenantPolicy
{
    /// <summary>The default policy: no grace period and <see cref="ReferenceFormat.Default"/>.</summary>
    public static TenantPolicy Default { get; } = new();

    /// <summary>Grace period applied to plans that do not specify their own.</summary>
    public GracePeriod DefaultGrace { get; init; } = GracePeriod.None;

    /// <summary>The format of payment references for this tenant's obligations.</summary>
    public ReferenceFormat PaymentReference { get; init; } = ReferenceFormat.Default;

    internal void Validate()
    {
        if (DefaultGrace is null || PaymentReference is null)
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "Tenant policy settings must not be null.");
        }
    }
}

/// <summary>One version of a tenant's policy.</summary>
/// <param name="Version">The version number, starting at 1.</param>
/// <param name="Policy">The policy.</param>
/// <param name="CreatedAt">When this version was created.</param>
/// <param name="CreatedBy">Who created it, as supplied by the host.</param>
public sealed record TenantPolicyVersion(int Version, TenantPolicy Policy, DateTimeOffset CreatedAt, string CreatedBy);
