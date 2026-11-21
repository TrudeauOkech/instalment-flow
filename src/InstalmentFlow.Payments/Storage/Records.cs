namespace InstalmentFlow.Payments.Storage;

internal enum InboxOrigin
{
    Callback = 1,
    Statement = 2,
    StatusQuery = 3,
}

/// <summary>
/// One provider event in the inbox. The event and raw payload never change; status, attempts and the lease do.
/// <see cref="LeaseUntil"/> marks an entry a processor has claimed.
/// </summary>
internal sealed record InboxEntry(
    Guid Id,
    string Provider,
    string DedupeKey,
    InboxOrigin Origin,
    NormalisedPaymentEvent Event,
    byte[] RawPayload,
    DateTimeOffset ReceivedAt,
    InboxStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? LeaseUntil,
    DateTimeOffset? ProcessedAt,
    string? Error,
    TenantId? Tenant,
    PaymentId? PaymentId);

internal sealed record IntentRecord(
    TenantId Tenant,
    Guid Id,
    string IdempotencyKey,
    string Fingerprint,
    string Provider,
    string ProviderAccount,
    string Payer,
    Money Amount,
    PaymentTargetKind TargetKind,
    ObligationId? ObligationId,
    AccountId? AccountId,
    string AccountReference,
    string? Description,
    PaymentIntentStatus Status,
    string? CorrelationId,
    string? TransactionId,
    PaymentId? PaymentId,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? NextCheckAt,
    int StatusChecks,
    long Version)
{
    public bool IsTerminal => Status is PaymentIntentStatus.Confirmed or PaymentIntentStatus.Failed or PaymentIntentStatus.Expired;

    public PaymentTarget Target => TargetKind switch
    {
        PaymentTargetKind.Obligation => PaymentTarget.ForObligation(ObligationRef.ById(ObligationId!.Value)),
        PaymentTargetKind.Customer => PaymentTarget.ForCustomer(AccountRef.ById(AccountId!.Value)),
        _ => PaymentTarget.ForReference(AccountReference),
    };

    /// <summary>
    /// The intent state machine (doc 02 §6.2). A confirmation is accepted from any state but Confirmed, because money
    /// that arrived is a fact (ADR-002); failure and expiry only end an intent that is still open.
    /// </summary>
    public static bool CanMove(PaymentIntentStatus from, PaymentIntentStatus to) => (from, to) switch
    {
        (PaymentIntentStatus.Confirmed, _) => false,
        (_, PaymentIntentStatus.Confirmed) => true,
        (PaymentIntentStatus.Created, PaymentIntentStatus.Submitted or PaymentIntentStatus.Ambiguous) => true,
        (PaymentIntentStatus.Submitted, PaymentIntentStatus.Ambiguous) => true,
        (PaymentIntentStatus.Created or PaymentIntentStatus.Submitted or PaymentIntentStatus.Ambiguous, PaymentIntentStatus.Failed or PaymentIntentStatus.Expired) => true,
        _ => false,
    };
}

internal sealed record ReconciliationRunRecord(
    Guid Id,
    string Provider,
    string ProviderAccount,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int Lines,
    int Matched,
    int MissingInEngine,
    int MissingAtProvider,
    int AmountMismatches,
    int ReversedAtProvider,
    int Unroutable);

internal sealed record DiscrepancyRecord(
    TenantId Tenant,
    Guid Id,
    Guid RunId,
    string Provider,
    string ProviderAccount,
    DiscrepancyKind Kind,
    string TransactionId,
    Money? StatementAmount,
    Money? EngineAmount,
    PaymentId? PaymentId,
    DateTimeOffset DetectedAt,
    DateTimeOffset? ResolvedAt,
    string? Resolution);
