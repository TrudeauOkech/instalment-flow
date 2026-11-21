namespace InstalmentFlow.Payments;

/// <summary>Routes a provider account to exactly one tenant (R-I10).</summary>
/// <param name="Provider">The provider, for example <c>mpesa</c>.</param>
/// <param name="ProviderAccount">The merchant account, for example <c>600123</c>.</param>
/// <param name="Tenant">The tenant that owns the account.</param>
/// <param name="CreatedAt">When the mapping was added.</param>
public sealed record ProviderAccountMapping(string Provider, string ProviderAccount, TenantId Tenant, DateTimeOffset CreatedAt);

/// <summary>Which provider accounts belong to which tenant.</summary>
public interface IProviderRouting
{
    /// <summary>Routes the account to the tenant. Mapping it again to the same tenant does nothing; to another tenant fails (<c>IF6004</c>).</summary>
    Task<ProviderAccountMapping> MapAsync(string provider, string providerAccount, TenantId tenant, CancellationToken cancellationToken = default);

    /// <summary>Removes a mapping. Events for the account then wait in the inbox until it is mapped again.</summary>
    Task<bool> RemoveAsync(string provider, string providerAccount, CancellationToken cancellationToken = default);

    /// <summary>The tenant that owns the account, or null.</summary>
    Task<TenantId?> ResolveAsync(string provider, string providerAccount, CancellationToken cancellationToken = default);

    /// <summary>Every mapping.</summary>
    Task<IReadOnlyList<ProviderAccountMapping>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides which tenant an event belongs to. The default uses <see cref="IProviderRouting"/>; a host whose provider
/// account is shared between tenants can route on the account reference instead.
/// </summary>
public interface ITenantRouter
{
    /// <summary>The tenant for the event, or null if it cannot be routed yet.</summary>
    ValueTask<TenantId?> RouteAsync(NormalisedPaymentEvent paymentEvent, CancellationToken cancellationToken);
}

/// <summary>Where an inbox entry is in its processing.</summary>
public enum InboxStatus
{
    /// <summary>Waiting to be processed, or to be retried.</summary>
    Pending = 0,

    /// <summary>Handed to the core; done.</summary>
    Processed = 1,

    /// <summary>Gave up: a rule refused it, or it failed too many times. Needs attention; <see cref="IProviderInbox.RequeueAsync"/> retries it.</summary>
    Failed = 2,
}

/// <summary>What happened to a received callback.</summary>
public enum InboxReceiptOutcome
{
    /// <summary>Stored; the provider can be told it was received.</summary>
    Accepted = 1,

    /// <summary>Every event in it was already stored; the provider can be told it was received.</summary>
    Duplicate = 2,

    /// <summary>Not authentic or not well formed; nothing was stored.</summary>
    Rejected = 3,
}

/// <summary>The result of receiving a callback. Answer the provider only after this returns: the entry is durable.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Stored">How many events were newly stored.</param>
/// <param name="Duplicates">How many events were already stored.</param>
/// <param name="RejectionReason">Why the callback was rejected.</param>
public sealed record InboxReceipt(InboxReceiptOutcome Outcome, int Stored, int Duplicates, string? RejectionReason);

/// <summary>An inbox entry.</summary>
/// <param name="EntryId">The entry ID.</param>
/// <param name="Provider">The provider.</param>
/// <param name="DedupeKey">The deduplication key.</param>
/// <param name="Event">The normalised event.</param>
/// <param name="ReceivedAt">When it was stored.</param>
/// <param name="Status">Where it is in its processing.</param>
/// <param name="Attempts">How many times processing was attempted.</param>
/// <param name="Error">The last processing error.</param>
/// <param name="Tenant">The tenant it was routed to.</param>
/// <param name="PaymentId">The payment it recorded.</param>
public sealed record InboxEntryView(
    Guid EntryId,
    string Provider,
    string DedupeKey,
    NormalisedPaymentEvent Event,
    DateTimeOffset ReceivedAt,
    InboxStatus Status,
    int Attempts,
    string? Error,
    TenantId? Tenant,
    PaymentId? PaymentId);

/// <summary>What one processing pass did.</summary>
/// <param name="Processed">Entries handed to the core.</param>
/// <param name="Retrying">Entries that failed and will be retried.</param>
/// <param name="Failed">Entries given up on.</param>
public sealed record InboxProcessingReport(int Processed, int Retrying, int Failed);

/// <summary>
/// The provider inbox (doc 03 §6): every authentic provider event is stored before it is processed, once per
/// deduplication key, and processed with retries, so a provider can be answered as soon as the event is durable.
/// </summary>
public interface IProviderInbox
{
    /// <summary>Authenticates, normalises and stores a callback.</summary>
    Task<InboxReceipt> ReceiveAsync(ProviderCallback callback, CancellationToken cancellationToken = default);

    /// <summary>Processes due entries until none is left: routes each to its tenant and records it in the core.</summary>
    Task<InboxProcessingReport> ProcessPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Entries with a status, oldest first.</summary>
    Task<IReadOnlyList<InboxEntryView>> ListAsync(InboxStatus status, int max = 100, CancellationToken cancellationToken = default);

    /// <summary>Retries a failed entry; false if it is not failed.</summary>
    Task<bool> RequeueAsync(Guid entryId, CancellationToken cancellationToken = default);
}

/// <summary>Where a payment intent is (doc 02 §6.2).</summary>
public enum PaymentIntentStatus
{
    /// <summary>Stored; the provider may or may not have been asked yet.</summary>
    Created = 0,

    /// <summary>The provider accepted the request; waiting for the customer.</summary>
    Submitted = 1,

    /// <summary>The customer paid; the payment is recorded.</summary>
    Confirmed = 2,

    /// <summary>The provider refused it, or the customer declined; no money moved.</summary>
    Failed = 3,

    /// <summary>It is not known whether the provider started the collection. Resolved only by a status query, a callback or reconciliation.</summary>
    Ambiguous = 4,

    /// <summary>Nothing was confirmed before the intent's time to live ran out. A later confirmation still counts.</summary>
    Expired = 5,
}

/// <summary>A request to collect a payment from a customer, for example an STK push.</summary>
public sealed record InitiateCollectionRequest
{
    /// <summary>Makes the request safe to retry: the same key never asks the provider twice.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>The provider, for example <c>mpesa</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>The merchant account to collect into.</summary>
    public required string ProviderAccount { get; init; }

    /// <summary>The customer to collect from, for example an MSISDN.</summary>
    public required string Payer { get; init; }

    /// <summary>The amount to collect.</summary>
    public required Money Amount { get; init; }

    /// <summary>What the payment is for: an obligation or a customer. The payment is recorded against it when confirmed.</summary>
    public required PaymentTarget Target { get; init; }

    /// <summary>The account reference the customer sees; by default the obligation's payment reference.</summary>
    public string? AccountReference { get; init; }

    /// <summary>A short description for the customer's prompt.</summary>
    public string? Description { get; init; }
}

/// <summary>A payment intent.</summary>
/// <param name="IntentId">The intent ID, also the merchant reference sent to the provider.</param>
/// <param name="IdempotencyKey">The request's idempotency key.</param>
/// <param name="Status">Where the intent is.</param>
/// <param name="Provider">The provider.</param>
/// <param name="ProviderAccount">The merchant account.</param>
/// <param name="Amount">The amount requested.</param>
/// <param name="TargetKind">What the payment is for.</param>
/// <param name="ObligationId">The obligation, for an obligation target.</param>
/// <param name="AccountId">The customer, for a customer target or the obligation's holder.</param>
/// <param name="AccountReference">The account reference sent to the provider.</param>
/// <param name="CorrelationId">The provider's ID for the collection.</param>
/// <param name="TransactionId">The provider's receipt, once confirmed.</param>
/// <param name="PaymentId">The recorded payment, once confirmed.</param>
/// <param name="FailureReason">Why it failed or is ambiguous.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="ExpiresAt">When it expires if nothing is confirmed.</param>
/// <param name="StatusChecks">How many status queries were made.</param>
public sealed record PaymentIntentView(
    Guid IntentId,
    string IdempotencyKey,
    PaymentIntentStatus Status,
    string Provider,
    string ProviderAccount,
    Money Amount,
    PaymentTargetKind TargetKind,
    ObligationId? ObligationId,
    AccountId? AccountId,
    string AccountReference,
    string? CorrelationId,
    string? TransactionId,
    PaymentId? PaymentId,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    int StatusChecks);

/// <summary>A tenant's payment intents: merchant-initiated collections.</summary>
public interface IPaymentIntents
{
    /// <summary>
    /// Asks the provider to collect a payment. With a key already used, returns that intent and never asks again:
    /// a timeout is not a failure (doc 03 §7.3), so show "checking", not "try again".
    /// </summary>
    Task<PaymentIntentView> InitiateAsync(InitiateCollectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>An intent by ID.</summary>
    Task<PaymentIntentView> GetAsync(Guid intentId, CancellationToken cancellationToken = default);

    /// <summary>An intent by its idempotency key, or null.</summary>
    Task<PaymentIntentView?> FindAsync(string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>What one resolver pass did.</summary>
/// <param name="Checked">Intents whose status was queried.</param>
/// <param name="Confirmed">Intents found paid (their payments are queued in the inbox).</param>
/// <param name="Failed">Intents found failed.</param>
/// <param name="Expired">Intents that expired.</param>
public sealed record IntentResolutionReport(int Checked, int Confirmed, int Failed, int Expired);

/// <summary>Resolves submitted and ambiguous intents by status queries with back-off, and expires them after their time to live.</summary>
public interface IIntentResolver
{
    /// <summary>Checks every intent that is due.</summary>
    Task<IntentResolutionReport> ResolveDueAsync(CancellationToken cancellationToken = default);
}

/// <summary>How a statement line compared with the engine.</summary>
public enum ReconciliationClassification
{
    /// <summary>The engine has the same transaction and amount.</summary>
    Matched = 1,

    /// <summary>The engine had not recorded it; it was queued in the inbox, so it is recorded once.</summary>
    MissingInEngine = 2,

    /// <summary>The engine recorded a payment the statement does not show. A discrepancy; nothing is changed.</summary>
    MissingAtProvider = 3,

    /// <summary>The engine recorded a different amount. A discrepancy.</summary>
    AmountMismatch = 4,

    /// <summary>The provider reversed a payment the engine has not reversed. Reversed or a discrepancy, by tenant policy.</summary>
    ReversedAtProvider = 5,

    /// <summary>No tenant could be found for the line.</summary>
    Unroutable = 6,
}

/// <summary>A difference between a provider's statement and the engine that needs a person.</summary>
public enum DiscrepancyKind
{
    /// <summary>The engine recorded a payment the statement does not show, for example a forged callback.</summary>
    MissingAtProvider = 1,

    /// <summary>The engine recorded a different amount than the statement shows.</summary>
    AmountMismatch = 2,

    /// <summary>The provider reversed a payment the engine has not reversed.</summary>
    ReversedAtProvider = 3,
}

/// <summary>What to do when a statement shows a reversal the engine has not recorded.</summary>
public enum ReversedAtProviderPolicy
{
    /// <summary>Raise a discrepancy for a person to decide (the default).</summary>
    RaiseDiscrepancy = 0,

    /// <summary>Reverse the payment in the engine through the inbox.</summary>
    AutoReverse = 1,
}

/// <summary>A reconciliation request for one provider account.</summary>
public sealed record ReconciliationRequest
{
    /// <summary>The provider.</summary>
    public required string Provider { get; init; }

    /// <summary>The merchant account.</summary>
    public required string ProviderAccount { get; init; }

    /// <summary>The start of the window; by default the previous run's end less the overlap, or the default window.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>The end of the window (exclusive); by default now.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>The statement to read, for example an imported file; by default the provider adapter's.</summary>
    public IStatementSource? Statement { get; init; }
}

/// <summary>How one statement line was classified.</summary>
/// <param name="TransactionId">The provider's receipt.</param>
/// <param name="Classification">How it compared.</param>
/// <param name="Tenant">The tenant it belongs to.</param>
/// <param name="PaymentId">The engine payment it matched.</param>
public sealed record ReconciliationLine(string TransactionId, ReconciliationClassification Classification, TenantId? Tenant, PaymentId? PaymentId);

/// <summary>A discrepancy.</summary>
/// <param name="DiscrepancyId">The discrepancy ID.</param>
/// <param name="RunId">The run that found it.</param>
/// <param name="Kind">What kind of difference.</param>
/// <param name="Provider">The provider.</param>
/// <param name="ProviderAccount">The merchant account.</param>
/// <param name="TransactionId">The provider's receipt.</param>
/// <param name="StatementAmount">The amount on the statement.</param>
/// <param name="EngineAmount">The amount the engine recorded.</param>
/// <param name="PaymentId">The engine payment.</param>
/// <param name="DetectedAt">When it was found.</param>
/// <param name="ResolvedAt">When a person resolved it.</param>
/// <param name="Resolution">What they decided.</param>
public sealed record DiscrepancyView(
    Guid DiscrepancyId,
    Guid RunId,
    DiscrepancyKind Kind,
    string Provider,
    string ProviderAccount,
    string TransactionId,
    Money? StatementAmount,
    Money? EngineAmount,
    PaymentId? PaymentId,
    DateTimeOffset DetectedAt,
    DateTimeOffset? ResolvedAt,
    string? Resolution);

/// <summary>The result of a reconciliation run.</summary>
/// <param name="RunId">The run ID.</param>
/// <param name="Provider">The provider.</param>
/// <param name="ProviderAccount">The merchant account.</param>
/// <param name="From">The start of the window.</param>
/// <param name="To">The end of the window (exclusive); the next run continues from here.</param>
/// <param name="Lines">Each statement line and how it compared.</param>
/// <param name="MissingAtProvider">Engine payments the statement does not show.</param>
/// <param name="NewDiscrepancies">Discrepancies this run raised; ones already known are not raised again.</param>
public sealed record ReconciliationReport(
    Guid RunId,
    string Provider,
    string ProviderAccount,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<ReconciliationLine> Lines,
    IReadOnlyList<ReconciliationLine> MissingAtProvider,
    IReadOnlyList<DiscrepancyView> NewDiscrepancies)
{
    /// <summary>How many lines had a classification.</summary>
    public int Count(ReconciliationClassification classification) =>
        Lines.Count(l => l.Classification == classification) + MissingAtProvider.Count(l => l.Classification == classification);
}

/// <summary>Reconciles provider statements with the engine (doc 03 §7.3).</summary>
public interface IReconciliation
{
    /// <summary>Reads the statement for the window and classifies every line; records the run.</summary>
    Task<ReconciliationReport> RunAsync(ReconciliationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A tenant's reconciliation discrepancies.</summary>
public interface IDiscrepancies
{
    /// <summary>Discrepancies not yet resolved, oldest first.</summary>
    Task<IReadOnlyList<DiscrepancyView>> ListOpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Records what a person decided. The engine is not changed; corrections go through the core commands.</summary>
    Task<DiscrepancyView> ResolveAsync(Guid discrepancyId, string resolution, CancellationToken cancellationToken = default);
}

/// <summary>The payments features of one tenant.</summary>
public interface ITenantPayments
{
    /// <summary>The tenant.</summary>
    TenantId Tenant { get; }

    /// <summary>Merchant-initiated collections.</summary>
    IPaymentIntents Intents { get; }

    /// <summary>Reconciliation discrepancies.</summary>
    IDiscrepancies Discrepancies { get; }
}

/// <summary>Inbox processing settings.</summary>
public sealed class InboxOptions
{
    /// <summary>Entries claimed per batch. Default 50.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>How long a claimed entry is reserved for one processor. Default 2 minutes.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Attempts before an entry is marked failed. Default 10.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>The first retry delay; it doubles up to <see cref="MaxRetryDelay"/>. Default 5 seconds.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest retry delay. Default 1 hour.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Payment intent settings.</summary>
public sealed class IntentOptions
{
    /// <summary>How long a collection request may take before the intent becomes ambiguous. Default 30 seconds.</summary>
    public TimeSpan InitiationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The first status check after initiation; it doubles up to <see cref="MaxCheckInterval"/>. Default 30 seconds.</summary>
    public TimeSpan FirstCheck { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest interval between status checks. Default 30 minutes.</summary>
    public TimeSpan MaxCheckInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long an intent waits for a confirmation before it expires. Default 24 hours.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>Reconciliation settings.</summary>
public sealed class ReconciliationOptions
{
    /// <summary>The window of a first run. Default 48 hours.</summary>
    public TimeSpan DefaultWindow { get; set; } = TimeSpan.FromHours(48);

    /// <summary>How far a run reaches back before the previous run's end, for late statement lines. Default 1 hour.</summary>
    public TimeSpan Overlap { get; set; } = TimeSpan.FromHours(1);

    /// <summary>What to do about reversals only the statement shows, unless a tenant overrides it.</summary>
    public ReversedAtProviderPolicy ReversedAtProvider { get; set; } = ReversedAtProviderPolicy.RaiseDiscrepancy;
}
