namespace InstalmentFlow;

/// <summary>Identifies a payment reversal within a tenant.</summary>
/// <param name="Value">A version 7 (time-ordered) UUID.</param>
public readonly record struct ReversalId(Guid Value)
{
    /// <summary>Returns the UUID.</summary>
    public override string ToString() => Value.ToString();

    internal static ReversalId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));
}

/// <summary>Identifies a host-posted charge within a tenant.</summary>
/// <param name="Value">A version 7 (time-ordered) UUID.</param>
public readonly record struct ChargeId(Guid Value)
{
    /// <summary>Returns the UUID.</summary>
    public override string ToString() => Value.ToString();

    internal static ChargeId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));
}

/// <summary>Identifies a waiver or write-off within a tenant.</summary>
/// <param name="Value">A version 7 (time-ordered) UUID.</param>
public readonly record struct WaiverId(Guid Value)
{
    /// <summary>Returns the UUID.</summary>
    public override string ToString() => Value.ToString();

    internal static WaiverId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));
}

/// <summary>Identifies a refund within a tenant.</summary>
/// <param name="Value">A version 7 (time-ordered) UUID.</param>
public readonly record struct RefundId(Guid Value)
{
    /// <summary>Returns the UUID.</summary>
    public override string ToString() => Value.ToString();

    internal static RefundId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));
}

/// <summary>
/// Records that a payment was reversed, fully or partly, for example after a provider reversal or reconciliation
/// (ADR-010). The engine records it; it never calls the provider.
/// </summary>
public sealed record ReversePaymentRequest : InstalmentFlowRequest
{
    /// <summary>
    /// The payment. A payment referred to by its provider reference that has not been recorded yet is parked, and
    /// the reversal is applied when the payment arrives.
    /// </summary>
    public required PaymentRef Payment { get; init; }

    /// <summary>How much to reverse; null reverses everything not reversed yet.</summary>
    public Money? Amount { get; init; }

    /// <summary>
    /// The provider's reference for the reversal itself, if any. A reversal with the same reference is reported as
    /// <see cref="ReversalOutcome.Duplicate"/>; with different details it throws <c>IF2004</c>.
    /// </summary>
    public ExternalReference? ReversalReference { get; init; }

    /// <summary>Why the payment is reversed.</summary>
    public required string Reason { get; init; }
}

/// <summary>What happened to a reversal.</summary>
public enum ReversalOutcome
{
    /// <summary>The payment is now fully reversed.</summary>
    Reversed = 1,

    /// <summary>Part of the payment was reversed; the rest stands.</summary>
    PartiallyReversed = 2,

    /// <summary>This reversal was already recorded; nothing changed.</summary>
    Duplicate = 3,

    /// <summary>The payment is not recorded yet; the reversal is applied when it arrives.</summary>
    Parked = 4,
}

/// <summary>Part of an allocation undone by a reversal.</summary>
/// <param name="PaymentId">The payment whose funds had been allocated.</param>
/// <param name="ObligationId">The obligation.</param>
/// <param name="InstalmentSequence">The instalment, which re-opens.</param>
/// <param name="DueDate">That instalment's due date.</param>
/// <param name="Component">The component.</param>
/// <param name="Amount">The amount unwound.</param>
public sealed record AllocationReversalView(
    PaymentId PaymentId,
    ObligationId ObligationId,
    int InstalmentSequence,
    DateOnly DueDate,
    ComponentKey Component,
    Money Amount);

/// <summary>The result of a reversal.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ReversalId">The reversal.</param>
/// <param name="PaymentId">The payment, or null while parked.</param>
/// <param name="AccountId">The customer who held the funds, or null for unidentified funds or while parked.</param>
/// <param name="Amount">The amount reversed.</param>
/// <param name="FromUnapplied">The part taken from the payment's unapplied funds.</param>
/// <param name="Unwound">The part freed by unwinding the payment's allocations, most recent first (ADR-005).</param>
/// <param name="Deficit">The part already refunded, now owed by the customer as a reversal receivable.</param>
/// <param name="Unwinds">The allocations undone.</param>
/// <param name="Reallocated">Allocations of other unapplied funds onto the re-opened instalments.</param>
/// <param name="UnappliedAfter">The customer's unapplied funds in the payment currency afterwards, if a customer held them.</param>
public sealed record ReversalResult(
    ReversalOutcome Outcome,
    ReversalId ReversalId,
    PaymentId? PaymentId,
    AccountId? AccountId,
    Money Amount,
    Money FromUnapplied,
    Money Unwound,
    Money Deficit,
    IReadOnlyList<AllocationReversalView> Unwinds,
    IReadOnlyList<AllocationLineView> Reallocated,
    Money? UnappliedAfter);

/// <summary>The kind of amount a host posts after origination (ADR-008).</summary>
public enum ChargeType
{
    /// <summary>Interest calculated by the host.</summary>
    Interest = 2,

    /// <summary>A fee.</summary>
    Fee = 3,

    /// <summary>A penalty.</summary>
    Penalty = 4,
}

/// <summary>How a charge's instalment is chosen.</summary>
public enum ChargeDueKind
{
    /// <summary>Use the tenant's <see cref="TenantPolicy.ChargeDue"/>.</summary>
    PerTenantPolicy = 0,

    /// <summary>Add it to the first unpaid instalment not yet past due, or the last unpaid one if all are past due.</summary>
    NextUnpaidInstalment = 1,

    /// <summary>Make it due now: add it to the latest instalment already due, or the first instalment if none is.</summary>
    Immediately = 2,

    /// <summary>Add it to the given instalment.</summary>
    OnInstalment = 3,
}

/// <summary>Which instalment a posted charge is added to.</summary>
public sealed record ChargeDue
{
    private ChargeDue(ChargeDueKind kind, int? sequence)
    {
        Kind = kind;
        Sequence = sequence;
    }

    /// <summary>The rule.</summary>
    public ChargeDueKind Kind { get; }

    /// <summary>The instalment number, for <see cref="ChargeDueKind.OnInstalment"/>.</summary>
    public int? Sequence { get; }

    /// <summary>Use the tenant's policy.</summary>
    public static ChargeDue PerTenantPolicy { get; } = new(ChargeDueKind.PerTenantPolicy, null);

    /// <summary>Add it to the next unpaid instalment.</summary>
    public static ChargeDue NextUnpaidInstalment { get; } = new(ChargeDueKind.NextUnpaidInstalment, null);

    /// <summary>Make it due now.</summary>
    public static ChargeDue Immediately { get; } = new(ChargeDueKind.Immediately, null);

    /// <summary>Add it to instalment <paramref name="sequence"/>.</summary>
    public static ChargeDue OnInstalment(int sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        return new ChargeDue(ChargeDueKind.OnInstalment, sequence);
    }

    /// <inheritdoc />
    public override string ToString() => Sequence is { } s ? $"{Kind}({s})" : Kind.ToString();
}

/// <summary>Posts interest, a fee or a penalty that the host calculated (ADR-008). The engine never calculates it.</summary>
public sealed record PostChargeRequest : InstalmentFlowRequest
{
    /// <summary>The obligation. It must be active or settled; a settled obligation re-opens.</summary>
    public required ObligationRef Obligation { get; init; }

    /// <summary>The kind of charge.</summary>
    public required ChargeType Type { get; init; }

    /// <summary>The amount, positive and in the obligation's currency.</summary>
    public required Money Amount { get; init; }

    /// <summary>An optional charge code for fees and penalties, for example <c>late</c>.</summary>
    public string? ChargeCode { get; init; }

    /// <summary>What the charge is for.</summary>
    public required string Description { get; init; }

    /// <summary>Which instalment it is added to.</summary>
    public ChargeDue DuePolicy { get; init; } = ChargeDue.PerTenantPolicy;
}

/// <summary>Reverses a posted charge in full. Allocations that paid it are unwound first; their funds stay unapplied.</summary>
public sealed record ReverseChargeRequest : InstalmentFlowRequest
{
    /// <summary>The charge.</summary>
    public required ChargeId Charge { get; init; }

    /// <summary>Why it is reversed.</summary>
    public required string Reason { get; init; }
}

/// <summary>The result of posting or reversing a charge.</summary>
/// <param name="ChargeId">The charge.</param>
/// <param name="ObligationId">The obligation.</param>
/// <param name="Component">The component charged.</param>
/// <param name="Amount">The amount.</param>
/// <param name="InstalmentSequence">The instalment it was added to.</param>
/// <param name="DueDate">That instalment's due date.</param>
/// <param name="Reversed">Whether the charge is reversed.</param>
/// <param name="ObligationOutstanding">The obligation's outstanding total afterwards.</param>
/// <param name="Unwinds">Allocations undone to reverse it.</param>
public sealed record ChargeResult(
    ChargeId ChargeId,
    ObligationId ObligationId,
    ComponentKey Component,
    Money Amount,
    int InstalmentSequence,
    DateOnly DueDate,
    bool Reversed,
    Money ObligationOutstanding,
    IReadOnlyList<AllocationReversalView> Unwinds);

/// <summary>Waives part of an amount owed. It does not come from customer funds.</summary>
public sealed record WaiveRequest : InstalmentFlowRequest
{
    /// <summary>The obligation. It must be active.</summary>
    public required ObligationRef Obligation { get; init; }

    /// <summary>The component to waive.</summary>
    public required ComponentKey Component { get; init; }

    /// <summary>The amount; at most what is outstanding on that component.</summary>
    public required Money Amount { get; init; }

    /// <summary>Why it is waived.</summary>
    public required string Reason { get; init; }

    /// <summary>One instalment to waive on; by default the oldest outstanding instalments first.</summary>
    public int? Instalment { get; init; }
}

/// <summary>Part of a waiver or write-off on one instalment.</summary>
/// <param name="InstalmentSequence">The instalment.</param>
/// <param name="DueDate">Its due date.</param>
/// <param name="Component">The component.</param>
/// <param name="Amount">The amount.</param>
public sealed record WaiverLineView(int InstalmentSequence, DateOnly DueDate, ComponentKey Component, Money Amount);

/// <summary>The result of a waiver.</summary>
/// <param name="WaiverId">The waiver.</param>
/// <param name="ObligationId">The obligation.</param>
/// <param name="Lines">What was waived, per instalment.</param>
/// <param name="ObligationOutstanding">The obligation's outstanding total afterwards.</param>
/// <param name="ObligationStatus">The obligation's status afterwards.</param>
public sealed record WaiverResult(
    WaiverId WaiverId,
    ObligationId ObligationId,
    IReadOnlyList<WaiverLineView> Lines,
    Money ObligationOutstanding,
    ObligationStatus ObligationStatus);

/// <summary>Charges, waivers and their reversals: amounts owed that the host decides (ADR-008).</summary>
public interface ICharges
{
    /// <summary>Posts a host-calculated charge.</summary>
    /// <exception cref="RuleViolationException">The obligation is cancelled or closed (<c>IF4003</c>), or the request is invalid.</exception>
    Task<ChargeResult> PostAsync(PostChargeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Waives part of an outstanding amount.</summary>
    Task<WaiverResult> WaiveAsync(WaiveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reverses a charge in full, unwinding allocations that paid it.</summary>
    Task<ChargeResult> ReverseAsync(ReverseChargeRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Where a refund is in its lifecycle (doc 02 §6.7).</summary>
public enum RefundStatus
{
    /// <summary>Requested; the funds are held as refund payable while the host pays them out.</summary>
    Requested = 1,

    /// <summary>The host confirmed the payout.</summary>
    Confirmed = 2,

    /// <summary>The payout failed; the funds were returned to the customer.</summary>
    Failed = 3,
}

/// <summary>
/// Requests a refund of a customer's unapplied funds. It is recorded only (ADR-010): the host executes the payout
/// and calls <see cref="IRefunds.CompleteAsync"/>.
/// </summary>
public sealed record RequestRefundRequest : InstalmentFlowRequest
{
    /// <summary>The customer.</summary>
    public required AccountRef Account { get; init; }

    /// <summary>The amount; at most the customer's eligible unapplied funds in its currency.</summary>
    public required Money Amount { get; init; }

    /// <summary>
    /// Refund only this payment's funds. By default the oldest funds are refunded first; funds with an open payment
    /// exception are skipped (use <see cref="IPaymentExceptions.MarkForRefundAsync"/>).
    /// </summary>
    public PaymentRef? Payment { get; init; }

    /// <summary>Why it is refunded.</summary>
    public required string Reason { get; init; }

    /// <summary>Who requested it, as supplied by the host.</summary>
    public required string RequestedBy { get; init; }
}

/// <summary>How a refund payout ended.</summary>
public enum RefundCompletion
{
    /// <summary>The payout succeeded.</summary>
    Confirmed = 1,

    /// <summary>The payout failed; the credit is returned.</summary>
    Failed = 2,
}

/// <summary>Records how the host's payout of a requested refund ended.</summary>
public sealed record CompleteRefundRequest : InstalmentFlowRequest
{
    /// <summary>The refund.</summary>
    public required RefundId Refund { get; init; }

    /// <summary>Whether the payout succeeded.</summary>
    public required RefundCompletion Completion { get; init; }

    /// <summary>The payout's provider reference, if any.</summary>
    public ExternalReference? ProviderReference { get; init; }

    /// <summary>Why the payout failed, for <see cref="RefundCompletion.Failed"/>.</summary>
    public string? FailureReason { get; init; }
}

/// <summary>The part of a refund taken from one payment's funds.</summary>
/// <param name="PaymentId">The payment.</param>
/// <param name="Amount">The amount.</param>
public sealed record RefundPortionView(PaymentId PaymentId, Money Amount);

/// <summary>A refund.</summary>
/// <param name="RefundId">The refund.</param>
/// <param name="AccountId">The customer, or null for a refund of unidentified funds.</param>
/// <param name="UnidentifiedSource">The provider account of refunded unidentified funds, if any.</param>
/// <param name="Amount">The amount.</param>
/// <param name="Status">Where it is in its lifecycle.</param>
/// <param name="Reason">Why it was requested.</param>
/// <param name="RequestedBy">Who requested it.</param>
/// <param name="RequestedAt">When it was requested.</param>
/// <param name="CompletedAt">When it was confirmed or failed.</param>
/// <param name="ProviderReference">The payout's provider reference, if given.</param>
/// <param name="FailureReason">Why it failed, if it did.</param>
/// <param name="Portions">The payments whose funds it uses.</param>
public sealed record RefundView(
    RefundId RefundId,
    AccountId? AccountId,
    string? UnidentifiedSource,
    Money Amount,
    RefundStatus Status,
    string Reason,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    ExternalReference? ProviderReference,
    string? FailureReason,
    IReadOnlyList<RefundPortionView> Portions);

/// <summary>The result of a refund command.</summary>
/// <param name="Refund">The refund afterwards.</param>
/// <param name="UnappliedAfter">The customer's unapplied funds in the refund currency afterwards, if a customer is involved.</param>
public sealed record RefundResult(RefundView Refund, Money? UnappliedAfter);

/// <summary>Refunds of unapplied funds, recorded only (ADR-010).</summary>
public interface IRefunds
{
    /// <summary>Requests a refund: the funds move to refund payable until the payout is confirmed or fails.</summary>
    /// <exception cref="RuleViolationException">The customer's eligible unapplied funds are too small (<c>IF2005</c>).</exception>
    Task<RefundResult> RequestAsync(RequestRefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>Records the payout result. A failed payout returns the credit.</summary>
    /// <exception cref="RuleViolationException">The refund is not pending (<c>IF2006</c>).</exception>
    Task<RefundResult> CompleteAsync(CompleteRefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>One refund.</summary>
    Task<RefundView> GetAsync(RefundId refundId, CancellationToken cancellationToken = default);
}

/// <summary>Requests a refund of a payment exception's funds, resolving it.</summary>
public sealed record MarkForRefundRequest : InstalmentFlowRequest
{
    /// <summary>The exception.</summary>
    public required PaymentExceptionId ExceptionId { get; init; }

    /// <summary>Why the funds are refunded.</summary>
    public required string Reason { get; init; }

    /// <summary>Who is resolving it.</summary>
    public required string ResolvedBy { get; init; }

    /// <summary>An optional note.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Cancels an obligation together with its active plan (ADR-011). Unpaid amounts are cleared only by the explicit
/// write-off lines given here, one per component still outstanding and equal to it.
/// </summary>
public sealed record CancelObligationRequest : InstalmentFlowRequest
{
    /// <summary>The obligation. It must be active or settled.</summary>
    public required ObligationRef Obligation { get; init; }

    /// <summary>Why it is cancelled, recorded on the plan and on every write-off.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// The amounts written off: exactly one line per component with an amount outstanding, equal to that amount.
    /// Empty if nothing is outstanding.
    /// </summary>
    public required IReadOnlyList<Component> WriteOffs { get; init; }
}

/// <summary>The result of cancelling an obligation.</summary>
/// <param name="ObligationId">The obligation.</param>
/// <param name="PlanVersion">The plan version that was cancelled.</param>
/// <param name="WrittenOff">The amounts written off per component.</param>
/// <param name="UnappliedAfter">The customer's unapplied funds per currency, which stay with the customer.</param>
public sealed record ObligationCancelled(ObligationId ObligationId, int PlanVersion, IReadOnlyList<Component> WrittenOff, IReadOnlyList<Money> UnappliedAfter);

/// <summary>Asks what it would take to settle an obligation now.</summary>
public sealed record QuoteSettlementRequest
{
    /// <summary>The obligation.</summary>
    public required ObligationRef Obligation { get; init; }

    /// <summary>The business date of the quote; the tenant's current date by default.</summary>
    public DateOnly? AsOfDate { get; init; }
}

/// <summary>One component of a settlement quote.</summary>
/// <param name="Key">The component.</param>
/// <param name="Due">Outstanding on instalments already due.</param>
/// <param name="NotYetDue">Outstanding on instalments not yet due.</param>
/// <param name="Outstanding">The total.</param>
public sealed record SettlementComponentView(ComponentKey Key, Money Due, Money NotYetDue, Money Outstanding);

/// <summary>
/// What settles an obligation early. The engine calculates no interest rebate (ADR-008): the host waives what the
/// contract forgives, then records the payment and allocates it with
/// <see cref="FutureInstalmentPolicy.ApplyToNextInstalments"/> (or <see cref="FutureInstalmentPolicy.PrepayPrincipal"/>
/// once only principal is left on future instalments).
/// </summary>
/// <param name="ObligationId">The obligation.</param>
/// <param name="AsOfDate">The business date of the quote.</param>
/// <param name="Components">Outstanding amounts per component.</param>
/// <param name="Due">Outstanding on instalments already due.</param>
/// <param name="NotYetDue">Outstanding on instalments not yet due.</param>
/// <param name="Outstanding">Everything outstanding.</param>
/// <param name="UnappliedFunds">The customer's unapplied funds in the obligation's currency.</param>
/// <param name="AmountToSettle">What the customer still needs to pay: outstanding less unapplied funds, never below zero.</param>
public sealed record SettlementQuote(
    ObligationId ObligationId,
    DateOnly AsOfDate,
    IReadOnlyList<SettlementComponentView> Components,
    Money Due,
    Money NotYetDue,
    Money Outstanding,
    Money UnappliedFunds,
    Money AmountToSettle);
