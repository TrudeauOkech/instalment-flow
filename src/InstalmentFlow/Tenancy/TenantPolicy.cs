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

    /// <summary>
    /// What to do with a payment from a known customer who did not say which loan it is for and has exactly one
    /// payable obligation (ADR-009, case 2). The default holds the funds and raises an exception.
    /// </summary>
    public SoleObligationPolicy SoleObligation { get; init; } = SoleObligationPolicy.Hold;

    /// <summary>
    /// What to do with such a payment when the customer has several payable obligations (ADR-009, case 3). The
    /// default holds the funds and raises an exception; allocating across loans requires an explicit opt-in.
    /// </summary>
    public MultipleObligationsPolicy MultipleObligations { get; init; } = MultipleObligationsPolicy.Hold;

    internal void Validate()
    {
        if (DefaultGrace is null || PaymentReference is null || !Enum.IsDefined(SoleObligation) || !Enum.IsDefined(MultipleObligations))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "Tenant policy settings must not be null.");
        }
    }
}

/// <summary>Handling of an unreferenced payment from a customer with one payable obligation.</summary>
public enum SoleObligationPolicy
{
    /// <summary>Hold the funds as unapplied and raise <see cref="PaymentExceptionReason.AllocationInstructionRequired"/>.</summary>
    Hold = 0,

    /// <summary>Direct the funds to that obligation.</summary>
    AutoAllocate = 1,
}

/// <summary>Handling of an unreferenced payment from a customer with several payable obligations.</summary>
public enum MultipleObligationsPolicy
{
    /// <summary>Hold the funds as unapplied and raise <see cref="PaymentExceptionReason.AllocationInstructionRequired"/>.</summary>
    Hold = 0,

    /// <summary>Allocate across the customer's payable obligations using the tenant's strategy (only where contracts allow it).</summary>
    AllocateByStrategy = 1,
}

/// <summary>One version of a tenant's policy.</summary>
/// <param name="Version">The version number, starting at 1.</param>
/// <param name="Policy">The policy.</param>
/// <param name="CreatedAt">When this version was created.</param>
/// <param name="CreatedBy">Who created it, as supplied by the host.</param>
public sealed record TenantPolicyVersion(int Version, TenantPolicy Policy, DateTimeOffset CreatedAt, string CreatedBy);
