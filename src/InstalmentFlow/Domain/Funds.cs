using System.Collections.Immutable;
using InstalmentFlow.Ledger;

namespace InstalmentFlow.Domain;

/// <summary>
/// One payment's value and where it went (ADR-005). Invariant I-5:
/// <c>Original = active allocations + Remaining + Reversed + Refunded</c>. <see cref="Remaining"/> is what is still
/// held as funds; <see cref="Reversed"/> what reversals took back from the lot; <see cref="Refunded"/> what refunds
/// requested or paid out. <see cref="Deficit"/> is reversed value that had already been refunded: the holder owes
/// it (the reversal receivable), and it is not part of the sum. <see cref="Directive"/> is the obligation
/// identification directed the funds to, if any.
/// </summary>
internal sealed record FundsLot(
    PaymentId PaymentId,
    Money Original,
    Money Remaining,
    ObligationId? Directive,
    DateTimeOffset ValueAt,
    Money Reversed,
    Money Refunded,
    Money Deficit)
{
    public static FundsLot Received(PaymentId paymentId, Money amount, ObligationId? directive, DateTimeOffset valueAt) =>
        new(paymentId, amount, amount, directive, valueAt, Money.Zero(amount.Currency), Money.Zero(amount.Currency), Money.Zero(amount.Currency));

    /// <summary>How much of the payment can still be reversed (I-8): its amount less what reversals already took.</summary>
    public Money Reversible => Original - Reversed - Deficit;
}

/// <summary>
/// Tenant unidentified funds for one provider account (source). A separate aggregate per source, so unidentified
/// receipts do not all contend on one version (ADR-003).
/// </summary>
internal sealed record UnidentifiedFunds(TenantId TenantId, string Source, long Version, ImmutableArray<FundsLot> Lots)
{
    public static UnidentifiedFunds Empty(TenantId tenantId, string source) => new(tenantId, source, CustomerAccount.NewVersion, []);

    public FundsLot GetLot(PaymentId payment) =>
        Lots.SingleOrDefault(l => l.PaymentId == payment) ?? throw new InvalidOperationException($"Payment {payment} has no unidentified funds in {Source}.");

    public UnidentifiedFunds ReplaceLot(FundsLot lot) =>
        this with { Lots = Lots.SetItem(Lots.IndexOf(GetLot(lot.PaymentId)), lot) };
}

/// <summary>A recorded payment. An immutable fact (ADR-004); later changes are separate facts.</summary>
internal sealed record PaymentRecord(
    PaymentId Id,
    ExternalReference Source,
    Money Amount,
    DateTimeOffset ValueAt,
    DateTimeOffset RecordedAt,
    PaymentTargetKind TargetKind,
    string? RawReference,
    AccountId? AccountId,
    ObligationId? IdentifiedObligation,
    PaymentExceptionId? ExceptionId,
    JournalTxId JournalTxId,
    ImmutableSortedDictionary<string, string> Metadata);

/// <summary>Unidentified funds assigned to a customer: a fact separate from the payment.</summary>
internal sealed record PaymentAssignment(PaymentId PaymentId, AccountId AccountId, Money Amount, JournalTxId JournalTxId, DateTimeOffset At, PaymentExceptionId ExceptionId);

internal sealed record ExceptionResolution(ExceptionResolutionKind Kind, string ResolvedBy, string? Note, DateTimeOffset At, JournalTxId? JournalTxId);

/// <summary>A payment exception. Its resolution is set exactly once.</summary>
internal sealed record PaymentExceptionRecord(
    PaymentExceptionId Id,
    PaymentId PaymentId,
    PaymentExceptionReason Reason,
    string Detail,
    string? RawReference,
    ImmutableArray<CandidateView> Candidates,
    DateTimeOffset OpenedAt,
    ExceptionResolution? Resolution)
{
    public PaymentExceptionStatus Status => Resolution switch
    {
        null => PaymentExceptionStatus.Open,
        { Kind: ExceptionResolutionKind.Dismissed } => PaymentExceptionStatus.Dismissed,
        _ => PaymentExceptionStatus.Resolved,
    };
}

/// <summary>A domain event, written to the outbox in the same commit and published as an <see cref="IntegrationEvent"/>.</summary>
internal sealed record DomainEvent(
    Guid EventId,
    string Type,
    TenantId Tenant,
    AccountId? AccountId,
    DateTimeOffset OccurredAt,
    ImmutableSortedDictionary<string, string> Data)
{
    public static DomainEvent Create(TimeProvider time, string type, TenantId tenant, AccountId? account, params (string Key, string Value)[] data) =>
        new(Guid.CreateVersion7(time.GetUtcNow()), type, tenant, account, time.GetUtcNow(),
            data.ToImmutableSortedDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal));
}
