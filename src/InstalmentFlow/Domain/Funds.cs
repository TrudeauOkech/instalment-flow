using System.Collections.Immutable;
using InstalmentFlow.Ledger;

namespace InstalmentFlow.Domain;

/// <summary>
/// The part of one payment's value still held as funds (ADR-005). Allocations (M4) consume lots; until then a
/// lot's remaining amount equals its original amount. <see cref="Directive"/> is the obligation identification
/// directed the funds to, if any.
/// </summary>
internal sealed record FundsLot(PaymentId PaymentId, Money Original, Money Remaining, ObligationId? Directive, DateTimeOffset ValueAt);

/// <summary>
/// Tenant unidentified funds for one provider account (source). A separate aggregate per source, so unidentified
/// receipts do not all contend on one version (ADR-003).
/// </summary>
internal sealed record UnidentifiedFunds(TenantId TenantId, string Source, long Version, ImmutableArray<FundsLot> Lots)
{
    public static UnidentifiedFunds Empty(TenantId tenantId, string source) => new(tenantId, source, CustomerAccount.NewVersion, []);

    public FundsLot GetLot(PaymentId payment) =>
        Lots.SingleOrDefault(l => l.PaymentId == payment) ?? throw new InvalidOperationException($"Payment {payment} has no unidentified funds in {Source}.");
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

/// <summary>A domain event, delivered through the outbox from milestone M7. Typed public contracts arrive then.</summary>
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
