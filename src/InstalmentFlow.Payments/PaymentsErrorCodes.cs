namespace InstalmentFlow.Payments;

/// <summary>Stable error codes of the payments package, raised as <see cref="RuleViolationException"/>. Never reused or renumbered.</summary>
public static class PaymentsErrorCodes
{
    /// <summary>No adapter is registered for the provider.</summary>
    public const string UnknownProvider = "IF6001";

    /// <summary>The provider does not support what was asked, for example a collection request.</summary>
    public const string CapabilityNotSupported = "IF6002";

    /// <summary>An intent idempotency key was reused with a different request.</summary>
    public const string IntentKeyReused = "IF6003";

    /// <summary>The provider account is already routed to another tenant.</summary>
    public const string ProviderAccountTaken = "IF6004";

    /// <summary>A statement file could not be read.</summary>
    public const string InvalidStatement = "IF6005";

    /// <summary>A collection needs a known obligation or customer as its target.</summary>
    public const string InvalidIntentTarget = "IF6006";

    /// <summary>A collection notification could not be confirmed: its amount is unknown, or the provider contradicts it.</summary>
    public const string UnverifiableCollection = "IF6007";
}
