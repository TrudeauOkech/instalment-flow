namespace InstalmentFlow;

/// <summary>Identifies a customer account within a tenant.</summary>
/// <param name="Value">A version 7 (time-ordered) UUID.</param>
public readonly record struct AccountId(Guid Value)
{
    /// <summary>Returns the UUID.</summary>
    public override string ToString() => Value.ToString();

    internal static AccountId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));
}

/// <summary>Identifies an obligation within a tenant.</summary>
/// <param name="Value">A version 7 (time-ordered) UUID.</param>
public readonly record struct ObligationId(Guid Value)
{
    /// <summary>Returns the UUID.</summary>
    public override string ToString() => Value.ToString();

    internal static ObligationId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));
}

/// <summary>The lifecycle state of an obligation.</summary>
public enum ObligationStatus
{
    /// <summary>Open; amounts may be outstanding.</summary>
    Active = 1,

    /// <summary>Every component is fully paid or waived.</summary>
    Settled = 2,

    /// <summary>Closed by the host, for example written off.</summary>
    Closed = 3,

    /// <summary>Cancelled together with its plan (ADR-011).</summary>
    Cancelled = 4,
}

/// <summary>The lifecycle state of one version of a repayment plan (ADR-011).</summary>
public enum PlanVersionStatus
{
    /// <summary>The plan currently in force.</summary>
    Active = 1,

    /// <summary>Replaced by a newer version before any payment or waiver touched it.</summary>
    Superseded = 2,

    /// <summary>Cancelled together with its obligation.</summary>
    Cancelled = 3,
}

/// <summary>How much of an instalment has been covered.</summary>
public enum InstalmentPaymentState
{
    /// <summary>Nothing paid or waived.</summary>
    Unpaid = 1,

    /// <summary>Partly paid or waived; an amount is still outstanding.</summary>
    PartiallyPaid = 2,

    /// <summary>Nothing outstanding, and at least part of it was paid.</summary>
    Paid = 3,

    /// <summary>Nothing outstanding, and none of it was paid: it was waived entirely.</summary>
    Waived = 4,
}

/// <summary>Where an instalment stands against its due date. Independent of <see cref="InstalmentPaymentState"/>.</summary>
public enum InstalmentTiming
{
    /// <summary>The due date has not arrived.</summary>
    Upcoming = 1,

    /// <summary>Falls due today.</summary>
    Due = 2,

    /// <summary>Past its due date but within the grace period.</summary>
    InGrace = 3,

    /// <summary>Past its due date and grace period with an amount outstanding.</summary>
    Overdue = 4,

    /// <summary>Nothing is outstanding.</summary>
    Settled = 5,
}
