namespace InstalmentFlow.EntityFrameworkCore.Schema;

// Row types for the PostgreSQL schema (doc 05). Every row carries the tenant, every key is composite with the tenant
// first, and money is an amount plus a currency code. Projection rows are records so a command can write only the
// rows whose values changed; fact rows are inserted once and never changed (triggers forbid it).

// ----- Configuration -----

internal sealed record TenantRow
{
    public required string TenantId { get; set; }

    public required string BaseCurrency { get; set; }

    public required string TimeZone { get; set; }
}

internal sealed record PolicyVersionRow
{
    public required string TenantId { get; set; }

    public required int Version { get; set; }

    /// <summary>The policy as JSON (jsonb).</summary>
    public required string Policy { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }
}

// ----- The customer account aggregate (projections) -----

internal sealed record AccountRow
{
    public required string TenantId { get; set; }

    public required Guid AccountId { get; set; }

    public required string ExternalId { get; set; }

    /// <summary>Optimistic concurrency token, bumped by every commit that changes the account.</summary>
    public required long Version { get; set; }

    public required DateTimeOffset OpenedAt { get; set; }
}

internal sealed record ObligationRow
{
    public required string TenantId { get; set; }

    public required Guid ObligationId { get; set; }

    public required Guid AccountId { get; set; }

    public required string ExternalId { get; set; }

    public required string PaymentReference { get; set; }

    public required string Currency { get; set; }

    public required short Status { get; set; }

    public string? Description { get; set; }

    public required DateTimeOffset OpenedAt { get; set; }
}

/// <summary>What the obligation owed per component when it was opened. Immutable.</summary>
internal sealed record OriginatedComponentRow
{
    public required string TenantId { get; set; }

    public required Guid ObligationId { get; set; }

    public required short Component { get; set; }

    public required string ChargeCode { get; set; }

    public required decimal Amount { get; set; }
}

internal sealed record PlanVersionRow
{
    public required string TenantId { get; set; }

    public required Guid ObligationId { get; set; }

    public required int Version { get; set; }

    public required short Status { get; set; }

    public required int GraceDays { get; set; }

    public required string Generator { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public string? EndReason { get; set; }
}

internal sealed record InstalmentRow
{
    public required string TenantId { get; set; }

    public required Guid ObligationId { get; set; }

    public required int PlanVersion { get; set; }

    public required int Sequence { get; set; }

    public required DateOnly DueDate { get; set; }

    public DateTimeOffset? SettledAt { get; set; }
}

internal sealed record InstalmentComponentRow
{
    public required string TenantId { get; set; }

    public required Guid ObligationId { get; set; }

    public required int PlanVersion { get; set; }

    public required int Sequence { get; set; }

    public required short Component { get; set; }

    public required string ChargeCode { get; set; }

    public required string Currency { get; set; }

    public required decimal Due { get; set; }

    public required decimal Allocated { get; set; }

    public required decimal Waived { get; set; }

    public required decimal Charged { get; set; }
}

/// <summary>A payment's funds lot (ADR-005), held by a customer account or by a tenant's unidentified funds for one source.</summary>
internal sealed record FundsLotRow
{
    public required string TenantId { get; set; }

    public required Guid PaymentId { get; set; }

    public Guid? AccountId { get; set; }

    public string? PoolSource { get; set; }

    public required string Currency { get; set; }

    public required decimal Original { get; set; }

    public required decimal Remaining { get; set; }

    public required decimal Reversed { get; set; }

    public required decimal Refunded { get; set; }

    public required decimal Deficit { get; set; }

    public Guid? Directive { get; set; }

    public required DateTimeOffset ValueAt { get; set; }

    /// <summary>Insertion order, so lots load in the order they were added to their holder.</summary>
    public long Ordinal { get; set; }
}

/// <summary>The version of one source's unidentified funds (a separate aggregate per source, ADR-003).</summary>
internal sealed record PoolRow
{
    public required string TenantId { get; set; }

    public required string Source { get; set; }

    public required long Version { get; set; }
}

// ----- Facts -----

internal sealed record PaymentRow
{
    public required string TenantId { get; set; }

    public required Guid PaymentId { get; set; }

    public required string Source { get; set; }

    public required string ExternalRef { get; set; }

    public required decimal Amount { get; set; }

    public required string Currency { get; set; }

    public required DateTimeOffset ValueAt { get; set; }

    public required DateTimeOffset RecordedAt { get; set; }

    public required short TargetKind { get; set; }

    public string? RawReference { get; set; }

    public Guid? AccountId { get; set; }

    public Guid? IdentifiedObligation { get; set; }

    public Guid? ExceptionId { get; set; }

    public required Guid JournalTxId { get; set; }

    /// <summary>Provider metadata as a JSON object (jsonb).</summary>
    public required string Metadata { get; set; }
}

internal sealed record AssignmentRow
{
    public required string TenantId { get; set; }

    public required Guid PaymentId { get; set; }

    public required Guid AccountId { get; set; }

    public required decimal Amount { get; set; }

    public required string Currency { get; set; }

    public required Guid JournalTxId { get; set; }

    public required DateTimeOffset At { get; set; }

    public required Guid ExceptionId { get; set; }
}

/// <summary>A fact recorded in order within a journal transaction.</summary>
internal interface IRecordedFact
{
    string TenantId { get; }

    Guid JournalTxId { get; }

    int Position { get; }
}

internal sealed record AllocationRow : IRecordedFact
{
    public required string TenantId { get; set; }

    public required Guid AllocationId { get; set; }

    public required Guid PaymentId { get; set; }

    public required Guid AccountId { get; set; }

    public required Guid ObligationId { get; set; }

    public required int PlanVersion { get; set; }

    public required int Sequence { get; set; }

    public required DateOnly DueDate { get; set; }

    public required short Component { get; set; }

    public required string ChargeCode { get; set; }

    public required decimal Amount { get; set; }

    public required string Currency { get; set; }

    public required string StrategyName { get; set; }

    public required int StrategyVersion { get; set; }

    public required int PolicyVersion { get; set; }

    public required Guid JournalTxId { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>The position within its commit; with the journal sequence it gives recording order, which LIFO unwinding uses (ADR-005).</summary>
    public required int Position { get; set; }
}

internal sealed record AllocationReversalRow : IRecordedFact
{
    public required string TenantId { get; set; }

    public required Guid Id { get; set; }

    public required Guid AllocationId { get; set; }

    public required Guid PaymentId { get; set; }

    public required Guid AccountId { get; set; }

    public required Guid ObligationId { get; set; }

    public required int PlanVersion { get; set; }

    public required int Sequence { get; set; }

    public required DateOnly DueDate { get; set; }

    public required short Component { get; set; }

    public required string ChargeCode { get; set; }

    public required decimal Amount { get; set; }

    public required string Currency { get; set; }

    public required short Cause { get; set; }

    public required Guid CauseId { get; set; }

    public required Guid JournalTxId { get; set; }

    public required DateTimeOffset At { get; set; }

    /// <summary>The position within its commit.</summary>
    public required int Position { get; set; }
}

internal sealed record PaymentReversalRow : IRecordedFact
{
    public required string TenantId { get; set; }

    public required Guid ReversalId { get; set; }

    public required Guid PaymentId { get; set; }

    public Guid? AccountId { get; set; }

    public string? UnidentifiedSource { get; set; }

    public string? ReferenceSource { get; set; }

    public string? ReferenceValue { get; set; }

    public required decimal Amount { get; set; }

    public required decimal FromFunds { get; set; }

    public required decimal Unwound { get; set; }

    public required decimal Deficit { get; set; }

    public required string Currency { get; set; }

    public required string Reason { get; set; }

    public required Guid JournalTxId { get; set; }

    public required DateTimeOffset At { get; set; }

    /// <summary>The position within its commit.</summary>
    public required int Position { get; set; }
}

/// <summary>A reversal parked until its payment arrives. Deleted when applied; not a fact.</summary>
internal sealed record PendingReversalRow
{
    public required string TenantId { get; set; }

    public required Guid ReversalId { get; set; }

    public required string PaymentSource { get; set; }

    public required string PaymentRef { get; set; }

    public string? ReferenceSource { get; set; }

    public string? ReferenceValue { get; set; }

    public decimal? Amount { get; set; }

    public string? Currency { get; set; }

    public required string Reason { get; set; }

    public required DateTimeOffset At { get; set; }
}

/// <summary>Reserves a provider reversal reference per tenant, whether the reversal is recorded or parked.</summary>
internal sealed record ReversalReferenceRow
{
    public required string TenantId { get; set; }

    public required string Source { get; set; }

    public required string Value { get; set; }

    public required Guid ReversalId { get; set; }
}

internal sealed record ChargeRow
{
    public required string TenantId { get; set; }

    public required Guid ChargeId { get; set; }

    public required Guid AccountId { get; set; }

    public required Guid ObligationId { get; set; }

    public required int PlanVersion { get; set; }

    public required int Sequence { get; set; }

    public required DateOnly DueDate { get; set; }

    public required short Component { get; set; }

    public required string ChargeCode { get; set; }

    public required decimal Amount { get; set; }

    public required string Currency { get; set; }

    public required string Description { get; set; }

    public required Guid JournalTxId { get; set; }

    public required DateTimeOffset At { get; set; }
}

internal sealed record ChargeReversalRow
{
    public required string TenantId { get; set; }

    public required Guid ChargeId { get; set; }

    public required string Reason { get; set; }

    public required Guid JournalTxId { get; set; }

    public required DateTimeOffset At { get; set; }
}

internal sealed record WaiverRow
{
    public required string TenantId { get; set; }

    public required Guid WaiverId { get; set; }

    public required int Line { get; set; }

    public required Guid AccountId { get; set; }

    public required Guid ObligationId { get; set; }

    public required int PlanVersion { get; set; }

    public required int Sequence { get; set; }

    public required DateOnly DueDate { get; set; }

    public required short Component { get; set; }

    public required string ChargeCode { get; set; }

    public required decimal Amount { get; set; }

    public required string Currency { get; set; }

    public required short Kind { get; set; }

    public required string Reason { get; set; }

    public required Guid JournalTxId { get; set; }

    public required DateTimeOffset At { get; set; }
}

/// <summary>A refund and its lifecycle. The status changes once (Requested → Confirmed | Failed).</summary>
internal sealed record RefundRow
{
    public required string TenantId { get; set; }

    public required Guid RefundId { get; set; }

    public Guid? AccountId { get; set; }

    public string? UnidentifiedSource { get; set; }

    public required decimal Amount { get; set; }

    public required string Currency { get; set; }

    /// <summary>The portions per payment as JSON (jsonb).</summary>
    public required string Portions { get; set; }

    public required short Status { get; set; }

    public required string Reason { get; set; }

    public required string RequestedBy { get; set; }

    public required DateTimeOffset RequestedAt { get; set; }

    public Guid? ExceptionId { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? ProviderReferenceSource { get; set; }

    public string? ProviderReferenceValue { get; set; }

    public string? FailureReason { get; set; }
}

internal sealed record PaymentExceptionRow
{
    public required string TenantId { get; set; }

    public required Guid ExceptionId { get; set; }

    public required Guid PaymentId { get; set; }

    public required short Reason { get; set; }

    public required string Detail { get; set; }

    public string? RawReference { get; set; }

    /// <summary>Candidate obligations and customers as JSON (jsonb).</summary>
    public required string Candidates { get; set; }

    public required DateTimeOffset OpenedAt { get; set; }
}

/// <summary>An exception's resolution; its existence makes the exception resolved (doc 05).</summary>
internal sealed record ExceptionResolutionRow
{
    public required string TenantId { get; set; }

    public required Guid ExceptionId { get; set; }

    public required short Kind { get; set; }

    public required string ResolvedBy { get; set; }

    public string? Note { get; set; }

    public required DateTimeOffset At { get; set; }

    public Guid? JournalTxId { get; set; }
}

// ----- Journal -----

internal sealed record JournalTransactionRow
{
    public required string TenantId { get; set; }

    public required Guid JournalTxId { get; set; }

    /// <summary>Global order, for the export cursor and as-of replay. Gaps are possible.</summary>
    public long Seq { get; set; }

    public required short Kind { get; set; }

    public Guid? AccountId { get; set; }

    public required string IdempotencyKey { get; set; }

    public required DateOnly EffectiveDate { get; set; }

    public required DateTimeOffset RecordedAt { get; set; }
}

internal sealed record LedgerEntryRow
{
    public required string TenantId { get; set; }

    public required Guid JournalTxId { get; set; }

    public required short Line { get; set; }

    public required string LedgerAccount { get; set; }

    public required string Currency { get; set; }

    public required decimal Debit { get; set; }

    public required decimal Credit { get; set; }

    public required short SourceType { get; set; }

    public required Guid SourceId { get; set; }
}

// ----- Idempotency and outbox -----

internal sealed record IdempotencyRow
{
    public required string TenantId { get; set; }

    public required string Key { get; set; }

    public required string CommandType { get; set; }

    public required byte[] PayloadHash { get; set; }

    /// <summary>The result's CLR type, to read <see cref="Result"/> back.</summary>
    public required string ResultType { get; set; }

    /// <summary>The command's result as JSON (jsonb).</summary>
    public required string Result { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}

internal sealed record OutboxRow
{
    public long OutboxId { get; set; }

    public required string TenantId { get; set; }

    public required Guid EventId { get; set; }

    public Guid? AccountId { get; set; }

    public required string Type { get; set; }

    /// <summary>The event's data as a JSON object (jsonb).</summary>
    public required string Payload { get; set; }

    public required DateTimeOffset OccurredAt { get; set; }

    /// <summary>0 pending, 1 published.</summary>
    public required short Status { get; set; }

    public int Attempts { get; set; }

    public required DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}
