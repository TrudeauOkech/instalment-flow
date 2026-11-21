namespace InstalmentFlow;

/// <summary>
/// A tenant's configurable rules (ADR-007). Policies are versioned: every change creates a new version and
/// commands record the version they used, so a change never alters history.
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

    /// <summary>The allocation strategy and version (ADR-006). Tenants pin a version, so strategy changes never alter their books silently.</summary>
    public StrategyRef Allocation { get; init; } = AllocationStrategies.OldestDueFirst.Ref;

    /// <summary>
    /// The order in which an instalment's components are paid. The default, principal → interest → fee → penalty,
    /// never puts charges first (risk R-F11); set a different order only where the contract and regulation allow it.
    /// </summary>
    public IReadOnlyList<ComponentType> ComponentOrder { get; init; } =
        [ComponentType.Principal, ComponentType.Interest, ComponentType.Fee, ComponentType.Penalty];

    /// <summary>What happens to funds left after every due instalment is paid. The default keeps them as unapplied funds.</summary>
    public FutureInstalmentPolicy FutureInstalments { get; init; } = FutureInstalmentPolicy.HoldAsCredit;

    /// <summary>
    /// Whether a reversal that re-opens instalments re-allocates the customer's other unapplied funds onto them
    /// (doc 02 §4). It changes which receipts paid which instalments, so it can be turned off.
    /// </summary>
    public bool ReallocateAfterReversal { get; init; } = true;

    /// <summary>
    /// Which instalment a charge posted with <see cref="ChargeDue.PerTenantPolicy"/> is added to:
    /// <see cref="ChargeDue.NextUnpaidInstalment"/> (the default) or <see cref="ChargeDue.Immediately"/>.
    /// </summary>
    public ChargeDue ChargeDue { get; init; } = ChargeDue.NextUnpaidInstalment;

    internal void Validate()
    {
        if (DefaultGrace is null || PaymentReference is null || Allocation is null || ComponentOrder is null || ChargeDue is null ||
            !Enum.IsDefined(SoleObligation) || !Enum.IsDefined(MultipleObligations) || !Enum.IsDefined(FutureInstalments))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "Tenant policy settings must not be null or undefined.");
        }

        if (ComponentOrder.Count != 4 || ComponentOrder.Distinct().Count() != 4 || ComponentOrder.Any(c => !Enum.IsDefined(c)))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "The component order must list principal, interest, fee and penalty exactly once each.");
        }

        if (ChargeDue.Kind is not (ChargeDueKind.NextUnpaidInstalment or ChargeDueKind.Immediately))
        {
            throw new RuleViolationException(ErrorCodes.InvalidRequest, "The tenant's charge due policy must be NextUnpaidInstalment or Immediately.");
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
