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

    /// <summary>The account changed concurrently and the command could not be applied.</summary>
    public const string ConcurrencyConflict = "IF3001";

    /// <summary>A plan cannot be superseded because payments or waivers have been applied to it.</summary>
    public const string PlanHasFinancialHistory = "IF4001";

    /// <summary>The operation would touch another tenant's data.</summary>
    public const string TenantMismatch = "IF5001";

    /// <summary>The tenant does not exist.</summary>
    public const string UnknownTenant = "IF5002";

    /// <summary>A tenant with this ID already exists.</summary>
    public const string DuplicateTenant = "IF5003";
}
