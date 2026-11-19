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
}
