namespace InstalmentFlow;

/// <summary>
/// Base type for every rule violation raised by InstalmentFlow. Each subtype carries a stable
/// <see cref="ErrorCode"/> (for example <c>IF1001</c>) that hosts can map to API responses.
/// Expected business outcomes, such as a duplicate payment, are returned as values, not thrown.
/// </summary>
public abstract class InstalmentFlowException : Exception
{
    /// <summary>Initialises the exception with a stable error code and a message.</summary>
    protected InstalmentFlowException(string errorCode, string message)
        : this(errorCode, message, innerException: null)
    {
    }

    /// <summary>Initialises the exception with a stable error code, a message and an inner exception.</summary>
    protected InstalmentFlowException(string errorCode, string message, Exception? innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    /// <summary>The stable, documented error code, for example <c>IF1001</c>.</summary>
    public string ErrorCode { get; }
}

/// <summary>Stable error codes. Codes are never reused or renumbered.</summary>
public static class ErrorCodes
{
    /// <summary>Two amounts in different currencies were combined or compared.</summary>
    public const string CurrencyMismatch = "IF1001";

    /// <summary>An idempotency key was reused with a different request payload.</summary>
    public const string IdempotencyKeyReused = "IF1002";

    /// <summary>An amount has more decimal places than its currency allows, or is otherwise invalid.</summary>
    public const string InvalidAmount = "IF1003";

    /// <summary>A currency code is not recognised.</summary>
    public const string UnknownCurrency = "IF1004";

    /// <summary>A payment reference does not match the tenant's reference format.</summary>
    public const string InvalidPaymentReference = "IF1005";

    /// <summary>A payment reference is already used by another obligation in the tenant.</summary>
    public const string DuplicatePaymentReference = "IF1006";

    /// <summary>An external ID is already used by another account or obligation in the tenant.</summary>
    public const string DuplicateExternalId = "IF1007";

    /// <summary>A repayment schedule is invalid, for example its totals do not equal the obligation's amounts.</summary>
    public const string InvalidSchedule = "IF1008";

    /// <summary>A request is invalid in a way not covered by a more specific code.</summary>
    public const string InvalidRequest = "IF1009";

    /// <summary>The referenced account, obligation or other entity does not exist in the tenant.</summary>
    public const string NotFound = "IF1010";

    /// <summary>
    /// A payment with the same source and external reference was already recorded with different details,
    /// for example a different amount (failure scenario 1).
    /// </summary>
    public const string PaymentConflict = "IF1011";

    /// <summary>A payment exception is already resolved or cannot be resolved this way.</summary>
    public const string ExceptionNotResolvable = "IF1012";

    /// <summary>A reversal would take back more than the payment's amount not yet reversed (I-8).</summary>
    public const string ReversalExceedsPayment = "IF2001";

    /// <summary>An allocation strategy produced a plan the engine rejected, or failed (ADR-006).</summary>
    public const string InvalidAllocationPlan = "IF2002";

    /// <summary>The allocation strategy named in a policy or request is not registered.</summary>
    public const string UnknownAllocationStrategy = "IF2003";

    /// <summary>A reversal with the same provider reference was already recorded with different details.</summary>
    public const string ReversalConflict = "IF2004";

    /// <summary>The customer's eligible unapplied funds are smaller than the amount requested.</summary>
    public const string InsufficientFunds = "IF2005";

    /// <summary>The refund is not pending: it was already confirmed or failed.</summary>
    public const string RefundNotPending = "IF2006";

    /// <summary>The account changed concurrently and the command could not be applied.</summary>
    public const string ConcurrencyConflict = "IF3001";

    /// <summary>A plan cannot be superseded because payments or waivers have been applied to it.</summary>
    public const string PlanHasFinancialHistory = "IF4001";

    /// <summary>A cancellation's write-off lines do not clear exactly what is outstanding (ADR-011).</summary>
    public const string WriteOffMismatch = "IF4002";

    /// <summary>The obligation is cancelled or closed, so amounts can no longer be charged, waived or paid on it.</summary>
    public const string ObligationNotOpen = "IF4003";

    /// <summary>The operation would touch another tenant's data.</summary>
    public const string TenantMismatch = "IF5001";

    /// <summary>The tenant does not exist.</summary>
    public const string UnknownTenant = "IF5002";

    /// <summary>A tenant with this ID already exists.</summary>
    public const string DuplicateTenant = "IF5003";
}
