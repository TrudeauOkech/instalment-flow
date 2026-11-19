namespace InstalmentFlow;

/// <summary>
/// Base type for every command. The idempotency key makes a command safe to retry; the same key with a
/// different request will be rejected with <c>IF1002</c> once idempotency storage lands (milestone M3).
/// </summary>
public abstract record InstalmentFlowRequest
{
    /// <summary>A caller-chosen key, unique per tenant, at most 128 characters.</summary>
    public required string IdempotencyKey { get; init; }
}

/// <summary>Opens a customer account.</summary>
public sealed record OpenAccountRequest : InstalmentFlowRequest
{
    /// <summary>The host's ID for the customer. Unique per tenant.</summary>
    public required string ExternalId { get; init; }
}

/// <summary>Opens an obligation and its first repayment plan.</summary>
public sealed record OpenObligationRequest : InstalmentFlowRequest
{
    /// <summary>The customer who owes the obligation.</summary>
    public required AccountRef Account { get; init; }

    /// <summary>The host's ID for the loan or financing agreement. Unique per tenant.</summary>
    public required string ExternalId { get; init; }

    /// <summary>
    /// The reference customers quote when paying, for example the paybill account number. It is normalised and
    /// validated with the tenant's <see cref="ReferenceFormat"/>, must be unique per tenant, and is never reused.
    /// </summary>
    public required string PaymentReference { get; init; }

    /// <summary>The amounts owed per component, as determined by the host. At least one; each key at most once.</summary>
    public required IReadOnlyList<Component> Components { get; init; }

    /// <summary>How the amounts are scheduled.</summary>
    public required PlanSpec Plan { get; init; }

    /// <summary>An optional description, for example the financed product.</summary>
    public string? Description { get; init; }
}

/// <summary>Replaces an obligation's repayment plan before any payment or waiver has touched it.</summary>
public sealed record SupersedePlanRequest : InstalmentFlowRequest
{
    /// <summary>The obligation.</summary>
    public required ObligationRef Obligation { get; init; }

    /// <summary>The replacement plan.</summary>
    public required PlanSpec Plan { get; init; }

    /// <summary>Why the plan is being replaced, recorded on the superseded version.</summary>
    public required string Reason { get; init; }
}
