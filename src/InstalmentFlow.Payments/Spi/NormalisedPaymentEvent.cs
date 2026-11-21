namespace InstalmentFlow.Payments;

/// <summary>What a provider event reports.</summary>
public enum PaymentEventKind
{
    /// <summary>Money was received, for example a paybill payment.</summary>
    PaymentReceived = 1,

    /// <summary>An earlier payment was reversed at the provider.</summary>
    PaymentReversed = 2,

    /// <summary>A requested collection was paid.</summary>
    CollectionSucceeded = 3,

    /// <summary>A requested collection failed or was cancelled; no money moved.</summary>
    CollectionFailed = 4,
}

/// <summary>
/// A provider event in provider-neutral form. The core only ever sees confirmed payments (ADR-016); this is what the
/// inbox stores and the processor hands on. The deduplication key is derived from the event, so the same
/// transaction reported by a callback and by a statement line is recognised as one.
/// </summary>
public sealed record NormalisedPaymentEvent
{
    private NormalisedPaymentEvent(PaymentEventKind kind, string provider, string providerAccount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerAccount);
        Kind = kind;
        Provider = provider;
        ProviderAccount = providerAccount;
    }

    /// <summary>What the event reports.</summary>
    public PaymentEventKind Kind { get; }

    /// <summary>The provider, for example <c>mpesa</c>.</summary>
    public string Provider { get; }

    /// <summary>The merchant account, for example <c>600123</c>.</summary>
    public string ProviderAccount { get; }

    /// <summary>The provider's receipt: the payment's, or for a reversal the reversal's own.</summary>
    public string? TransactionId { get; private init; }

    /// <summary>The amount; for a reversal, null means the whole payment.</summary>
    public Money? Amount { get; private init; }

    /// <summary>When the money moved.</summary>
    public DateTimeOffset? ValueAt { get; private init; }

    /// <summary>The account reference the payer entered, for example a paybill account number.</summary>
    public string? AccountReference { get; private init; }

    /// <summary>The payer, as the provider shows it (often masked).</summary>
    public string? Payer { get; private init; }

    /// <summary>The provider's ID for a requested collection, for example an M-Pesa <c>CheckoutRequestID</c>.</summary>
    public string? CorrelationId { get; private init; }

    /// <summary>Our reference for a requested collection (the intent ID), when the provider echoes it.</summary>
    public string? MerchantReference { get; private init; }

    /// <summary>The payment a reversal reverses.</summary>
    public string? ReversedTransactionId { get; private init; }

    /// <summary>Why a collection failed, or why a payment was reversed.</summary>
    public string? Reason { get; private init; }

    /// <summary>
    /// The provider's notification cannot be trusted on its own (it is unsigned and the provider offers a status query):
    /// the inbox confirms a collection success with a status query before recording it (R-I3).
    /// </summary>
    public bool RequiresVerification { get; private init; }

    /// <summary>Provider details to keep with the payment.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; private init; } = new Dictionary<string, string>();

    /// <summary>Identifies the event within its provider; the inbox stores each key once.</summary>
    public string DedupeKey => Kind switch
    {
        PaymentEventKind.PaymentReceived => $"payment:{ProviderAccount}:{TransactionId}",
        PaymentEventKind.PaymentReversed => $"reversal:{ProviderAccount}:{TransactionId}",
        PaymentEventKind.CollectionSucceeded => $"collection:{ProviderAccount}:{CollectionKey}:succeeded",
        _ => $"collection:{ProviderAccount}:{CollectionKey}:failed",
    };

    /// <summary>The payment source in the core, for example <c>mpesa:600123</c>.</summary>
    public string Source => $"{Provider}:{ProviderAccount}";

    private string? CollectionKey => CorrelationId ?? MerchantReference;

    /// <summary>Money received from a customer.</summary>
    public static NormalisedPaymentEvent PaymentReceived(
        string provider, string providerAccount, string transactionId, Money amount, DateTimeOffset valueAt,
        string? accountReference = null, string? payer = null, IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        RequirePositive(amount);
        return new NormalisedPaymentEvent(PaymentEventKind.PaymentReceived, provider, providerAccount)
        {
            TransactionId = transactionId, Amount = amount, ValueAt = valueAt, AccountReference = accountReference, Payer = payer, Metadata = Copy(metadata),
        };
    }

    /// <summary>An earlier payment reversed at the provider; <paramref name="amount"/> null means the whole payment.</summary>
    public static NormalisedPaymentEvent PaymentReversed(
        string provider, string providerAccount, string reversalTransactionId, string reversedTransactionId, Money? amount, DateTimeOffset valueAt,
        string? reason = null, IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reversalTransactionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reversedTransactionId);
        if (amount is not null)
        {
            RequirePositive(amount);
        }

        return new NormalisedPaymentEvent(PaymentEventKind.PaymentReversed, provider, providerAccount)
        {
            TransactionId = reversalTransactionId, ReversedTransactionId = reversedTransactionId, Amount = amount, ValueAt = valueAt, Reason = reason, Metadata = Copy(metadata),
        };
    }

    /// <summary>
    /// A requested collection was paid. At least one of <paramref name="correlationId"/> and <paramref name="merchantReference"/>
    /// is needed. <paramref name="amount"/> null means the amount requested, for providers whose notifications omit it.
    /// </summary>
    public static NormalisedPaymentEvent CollectionSucceeded(
        string provider, string providerAccount, string? correlationId, string? merchantReference, string transactionId, Money? amount, DateTimeOffset valueAt,
        string? accountReference = null, string? payer = null, IReadOnlyDictionary<string, string>? metadata = null, bool requiresVerification = false)
    {
        RequireCollectionKey(correlationId, merchantReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        if (amount is not null)
        {
            RequirePositive(amount);
        }

        return new NormalisedPaymentEvent(PaymentEventKind.CollectionSucceeded, provider, providerAccount)
        {
            CorrelationId = correlationId, MerchantReference = merchantReference, TransactionId = transactionId, Amount = amount, ValueAt = valueAt,
            AccountReference = accountReference, Payer = payer, Metadata = Copy(metadata), RequiresVerification = requiresVerification,
        };
    }

    /// <summary>A requested collection failed or was cancelled. At least one of <paramref name="correlationId"/> and <paramref name="merchantReference"/> is needed.</summary>
    public static NormalisedPaymentEvent CollectionFailed(
        string provider, string providerAccount, string? correlationId, string? merchantReference, string reason, IReadOnlyDictionary<string, string>? metadata = null,
        bool requiresVerification = false)
    {
        RequireCollectionKey(correlationId, merchantReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new NormalisedPaymentEvent(PaymentEventKind.CollectionFailed, provider, providerAccount)
        {
            CorrelationId = correlationId, MerchantReference = merchantReference, Reason = reason, Metadata = Copy(metadata), RequiresVerification = requiresVerification,
        };
    }

    internal static NormalisedPaymentEvent Restore(
        PaymentEventKind kind, string provider, string providerAccount, string? transactionId, Money? amount, DateTimeOffset? valueAt,
        string? accountReference, string? payer, string? correlationId, string? merchantReference, string? reversedTransactionId, string? reason,
        IReadOnlyDictionary<string, string>? metadata, bool requiresVerification = false) =>
        new(kind, provider, providerAccount)
        {
            TransactionId = transactionId, Amount = amount, ValueAt = valueAt, AccountReference = accountReference, Payer = payer,
            CorrelationId = correlationId, MerchantReference = merchantReference, ReversedTransactionId = reversedTransactionId, Reason = reason,
            Metadata = Copy(metadata), RequiresVerification = requiresVerification,
        };

    private static void RequirePositive(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);
        if (!amount.IsPositive)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A provider amount must be positive.");
        }
    }

    private static void RequireCollectionKey(string? correlationId, string? merchantReference)
    {
        if (string.IsNullOrWhiteSpace(correlationId) && string.IsNullOrWhiteSpace(merchantReference))
        {
            throw new ArgumentException("A collection event needs the provider's correlation ID or our merchant reference.", nameof(correlationId));
        }
    }

    private static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null ? [] : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
}
