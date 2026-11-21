namespace InstalmentFlow.Payments;

/// <summary>What a provider adapter can do. Documentation and start-up validation use it.</summary>
[Flags]
public enum ProviderCapabilities
{
    /// <summary>Nothing beyond parsing callbacks.</summary>
    None = 0,

    /// <summary>The provider notifies payments the customer starts, such as paybill or till payments.</summary>
    CustomerInitiatedCallbacks = 1,

    /// <summary>The merchant can request a payment from the customer, such as an STK or USSD push (<see cref="ICollectionInitiator"/>).</summary>
    MerchantInitiatedCollection = 2,

    /// <summary>The status of a requested collection can be queried (<see cref="ITransactionStatusQuery"/>).</summary>
    StatusQuery = 4,

    /// <summary>The provider's statement can be read for reconciliation (<see cref="IStatementSource"/>).</summary>
    StatementSource = 8,

    /// <summary>The provider asks the merchant to validate a payment before accepting it.</summary>
    ValidationHook = 16,

    /// <summary>Callbacks carry something the adapter can verify, such as a signature.</summary>
    AuthenticatedCallbacks = 32,
}

/// <summary>
/// A payment provider: it authenticates raw callbacks and turns them into <see cref="NormalisedPaymentEvent"/>s.
/// An adapter also implements <see cref="ICollectionInitiator"/>, <see cref="ITransactionStatusQuery"/> and
/// <see cref="IStatementSource"/> when, and only when, its provider supports them.
/// </summary>
public interface IPaymentProviderAdapter
{
    /// <summary>The provider name, for example <c>mpesa</c>. Lower case; used in payment sources (<c>mpesa:600123</c>).</summary>
    string Provider { get; }

    /// <summary>What the provider supports.</summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>Authenticates and normalises a callback. It must not throw for a malformed or forged callback; it rejects it.</summary>
    ValueTask<CallbackParseResult> ParseCallbackAsync(ProviderCallback callback, CancellationToken cancellationToken);
}

/// <summary>Requests payments from customers (merchant-initiated collection).</summary>
public interface ICollectionInitiator
{
    /// <summary>The provider name, as in <see cref="IPaymentProviderAdapter.Provider"/>.</summary>
    string Provider { get; }

    /// <summary>
    /// Asks the provider to collect a payment. Throwing, or returning <see cref="InitiationOutcome.Unknown"/>, means
    /// the provider may or may not have started the collection: the intent becomes ambiguous and is never retried.
    /// </summary>
    Task<InitiationResult> InitiateAsync(CollectionRequest request, CancellationToken cancellationToken);
}

/// <summary>Queries the status of a requested collection.</summary>
public interface ITransactionStatusQuery
{
    /// <summary>The provider name, as in <see cref="IPaymentProviderAdapter.Provider"/>.</summary>
    string Provider { get; }

    /// <summary>The provider's view of a collection. <see cref="ProviderTransactionStatus.NotFound"/> is not a failure.</summary>
    Task<StatusQueryResult> QueryAsync(ProviderTransactionRef reference, CancellationToken cancellationToken);
}

/// <summary>Reads a provider account's statement, for reconciliation.</summary>
public interface IStatementSource
{
    /// <summary>The provider name, as in <see cref="IPaymentProviderAdapter.Provider"/>.</summary>
    string Provider { get; }

    /// <summary>The statement lines of <paramref name="providerAccount"/> whose value time is in [<paramref name="windowStart"/>, <paramref name="windowEnd"/>).</summary>
    IAsyncEnumerable<ProviderStatementLine> ReadAsync(string providerAccount, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);
}

/// <summary>A raw callback, as received from a provider.</summary>
public sealed class ProviderCallback
{
    /// <summary>Creates a callback.</summary>
    /// <param name="provider">The provider that sent it, for example <c>mpesa</c>.</param>
    /// <param name="kind">Which endpoint received it, for example <c>c2b-confirmation</c>; adapters interpret it.</param>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">Request headers the adapter needs; looked up ignoring case.</param>
    public ProviderCallback(string provider, string kind, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(kind);
        Provider = provider;
        Kind = kind;
        Body = body;
        Headers = headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The provider that sent it.</summary>
    public string Provider { get; }

    /// <summary>Which endpoint received it.</summary>
    public string Kind { get; }

    /// <summary>The raw request body.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>Request headers, looked up ignoring case.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }
}

/// <summary>The outcome of parsing a callback.</summary>
public sealed record CallbackParseResult
{
    private CallbackParseResult(bool accepted, string? rejectionReason, IReadOnlyList<NormalisedPaymentEvent> events)
    {
        Accepted = accepted;
        RejectionReason = rejectionReason;
        Events = events;
    }

    /// <summary>Whether the callback is authentic and well formed.</summary>
    public bool Accepted { get; }

    /// <summary>Why the callback was rejected.</summary>
    public string? RejectionReason { get; }

    /// <summary>The events the callback carries; an accepted callback may carry none (for example a test ping).</summary>
    public IReadOnlyList<NormalisedPaymentEvent> Events { get; }

    /// <summary>Accepts the callback with its events.</summary>
    public static CallbackParseResult Accept(params IReadOnlyList<NormalisedPaymentEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return new CallbackParseResult(true, null, [.. events]);
    }

    /// <summary>Rejects the callback: it is not stored and nothing is recorded.</summary>
    public static CallbackParseResult Reject(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new CallbackParseResult(false, reason, []);
    }
}

/// <summary>A request to collect a payment from a customer.</summary>
public sealed record CollectionRequest
{
    /// <summary>The merchant account to collect into, for example a paybill shortcode.</summary>
    public required string ProviderAccount { get; init; }

    /// <summary>The customer to collect from, for example an MSISDN.</summary>
    public required string Payer { get; init; }

    /// <summary>The amount to collect.</summary>
    public required Money Amount { get; init; }

    /// <summary>The account reference the customer sees and the statement shows, usually the obligation's payment reference.</summary>
    public required string AccountReference { get; init; }

    /// <summary>A short description for the customer's prompt.</summary>
    public string? Description { get; init; }

    /// <summary>Our reference for the collection (the intent ID). Providers that accept one echo it in callbacks and status queries.</summary>
    public required string MerchantReference { get; init; }
}

/// <summary>What a provider said to a collection request.</summary>
public enum InitiationOutcome
{
    /// <summary>The provider accepted the request; the customer is being asked to pay.</summary>
    Accepted = 1,

    /// <summary>The provider refused the request; no payment will follow.</summary>
    Rejected = 2,

    /// <summary>It is not known whether the provider started the collection, for example after a server error.</summary>
    Unknown = 3,
}

/// <summary>The result of a collection request.</summary>
/// <param name="Outcome">What the provider said.</param>
/// <param name="CorrelationId">The provider's ID for the collection, for example an M-Pesa <c>CheckoutRequestID</c>.</param>
/// <param name="Reason">Why the request was refused or its outcome is unknown.</param>
public sealed record InitiationResult(InitiationOutcome Outcome, string? CorrelationId, string? Reason)
{
    /// <summary>The provider accepted the request.</summary>
    public static InitiationResult Accepted(string? correlationId) => new(InitiationOutcome.Accepted, correlationId, null);

    /// <summary>The provider refused the request.</summary>
    public static InitiationResult Rejected(string reason) => new(InitiationOutcome.Rejected, null, reason);

    /// <summary>It is not known whether the provider started the collection.</summary>
    public static InitiationResult Unknown(string reason) => new(InitiationOutcome.Unknown, null, reason);
}

/// <summary>Identifies a requested collection at the provider.</summary>
/// <param name="ProviderAccount">The merchant account.</param>
/// <param name="CorrelationId">The provider's ID, when the provider returned one.</param>
/// <param name="MerchantReference">Our reference (the intent ID).</param>
public sealed record ProviderTransactionRef(string ProviderAccount, string? CorrelationId, string MerchantReference);

/// <summary>The provider's view of a requested collection.</summary>
public enum ProviderTransactionStatus
{
    /// <summary>The customer has not completed it yet.</summary>
    Pending = 1,

    /// <summary>The customer paid.</summary>
    Succeeded = 2,

    /// <summary>The collection failed or was cancelled; no money moved.</summary>
    Failed = 3,

    /// <summary>The provider has no record of it (yet). Not a failure: the request may still be in flight.</summary>
    NotFound = 4,
}

/// <summary>The result of a status query.</summary>
/// <param name="Status">The provider's view.</param>
/// <param name="TransactionId">The provider's receipt for a succeeded collection.</param>
/// <param name="Amount">The amount actually paid, for a succeeded collection.</param>
/// <param name="ValueAt">When the money moved, for a succeeded collection.</param>
/// <param name="Reason">Why the collection failed.</param>
public sealed record StatusQueryResult(ProviderTransactionStatus Status, string? TransactionId, Money? Amount, DateTimeOffset? ValueAt, string? Reason)
{
    /// <summary>The customer has not completed it yet.</summary>
    public static StatusQueryResult Pending { get; } = new(ProviderTransactionStatus.Pending, null, null, null, null);

    /// <summary>The provider has no record of it.</summary>
    public static StatusQueryResult NotFound { get; } = new(ProviderTransactionStatus.NotFound, null, null, null, null);

    /// <summary>The customer paid.</summary>
    public static StatusQueryResult Succeeded(string transactionId, Money amount, DateTimeOffset valueAt) =>
        new(ProviderTransactionStatus.Succeeded, transactionId, amount, valueAt, null);

    /// <summary>The collection failed.</summary>
    public static StatusQueryResult Failed(string reason) => new(ProviderTransactionStatus.Failed, null, null, null, reason);
}

/// <summary>What a statement line records.</summary>
public enum StatementLineKind
{
    /// <summary>Money received.</summary>
    Credit = 1,

    /// <summary>Money returned to the payer for an earlier credit.</summary>
    Reversal = 2,
}

/// <summary>One line of a provider account's statement.</summary>
public sealed record ProviderStatementLine
{
    /// <summary>The provider, for example <c>mpesa</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>The merchant account, for example <c>600123</c>.</summary>
    public required string ProviderAccount { get; init; }

    /// <summary>The provider's receipt for this line.</summary>
    public required string TransactionId { get; init; }

    /// <summary>What the line records.</summary>
    public required StatementLineKind Kind { get; init; }

    /// <summary>The amount, always positive.</summary>
    public required Money Amount { get; init; }

    /// <summary>When the money moved.</summary>
    public required DateTimeOffset ValueAt { get; init; }

    /// <summary>The account reference the payer entered, for a credit.</summary>
    public string? AccountReference { get; init; }

    /// <summary>The payer, as the provider shows it (often masked).</summary>
    public string? Payer { get; init; }

    /// <summary>The credit this line reverses, for a reversal.</summary>
    public string? ReversedTransactionId { get; init; }
}

/// <summary>Protects raw provider payloads at rest, for example with the Data Protection API.</summary>
public interface IPayloadProtector
{
    /// <summary>Protects a payload before it is stored.</summary>
    byte[] Protect(ReadOnlySpan<byte> payload);

    /// <summary>Restores a protected payload.</summary>
    byte[] Unprotect(ReadOnlySpan<byte> protectedPayload);
}
