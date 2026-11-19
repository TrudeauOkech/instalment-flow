namespace InstalmentFlow;

/// <summary>Identifies a payment exception within a tenant.</summary>
/// <param name="Value">A version 7 (time-ordered) UUID.</param>
public readonly record struct PaymentExceptionId(Guid Value)
{
    /// <summary>Returns the UUID.</summary>
    public override string ToString() => Value.ToString();

    internal static PaymentExceptionId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));
}

/// <summary>Why a recorded payment was not directed to an obligation (ADR-009).</summary>
public enum PaymentExceptionReason
{
    /// <summary>No reference, an invalid one, or one matching nothing: the customer is unknown (case 4).</summary>
    UnidentifiedPayment = 1,

    /// <summary>The reference matches more than one obligation or customer (case 5).</summary>
    AmbiguousReference = 2,

    /// <summary>The customer is known but must say which obligation the payment is for (cases 2 and 3).</summary>
    AllocationInstructionRequired = 3,

    /// <summary>The reference matches an obligation that is settled, closed or cancelled (case 6).</summary>
    ObligationNotPayable = 4,

    /// <summary>The payment's currency differs from the obligation's or the tenant's (case 7).</summary>
    CurrencyMismatch = 5,

    /// <summary>The customer is known but has no payable obligation in the payment's currency.</summary>
    NoPayableObligation = 6,
}

/// <summary>Whether an exception still needs action.</summary>
public enum PaymentExceptionStatus
{
    /// <summary>Needs action.</summary>
    Open = 1,

    /// <summary>Resolved by a command that moved or applied the funds.</summary>
    Resolved = 2,

    /// <summary>Closed without moving funds, with a reason.</summary>
    Dismissed = 3,
}

/// <summary>How an exception was resolved.</summary>
public enum ExceptionResolutionKind
{
    /// <summary>Unidentified funds were assigned to a customer.</summary>
    AssignedToCustomer = 1,

    /// <summary>The funds were allocated to an obligation.</summary>
    AllocatedToObligation = 2,

    /// <summary>A refund was requested.</summary>
    MarkedForRefund = 3,

    /// <summary>Closed without moving funds.</summary>
    Dismissed = 4,
}

/// <summary>A candidate obligation or customer found while identifying a payment.</summary>
/// <param name="AccountId">The customer.</param>
/// <param name="ObligationId">The obligation, or null for a customer-level candidate.</param>
/// <param name="PaymentReference">The obligation's payment reference, if any.</param>
public sealed record CandidateView(AccountId AccountId, ObligationId? ObligationId, string? PaymentReference);

/// <summary>How an exception was resolved.</summary>
/// <param name="Kind">The kind of resolution.</param>
/// <param name="ResolvedBy">Who resolved it, as supplied by the host.</param>
/// <param name="Note">An optional note or the dismissal reason.</param>
/// <param name="ResolvedAt">When it was resolved.</param>
public sealed record ResolutionView(ExceptionResolutionKind Kind, string ResolvedBy, string? Note, DateTimeOffset ResolvedAt);

/// <summary>A payment exception: a recorded payment that needs a person or the host to act.</summary>
/// <param name="ExceptionId">The exception.</param>
/// <param name="PaymentId">The payment.</param>
/// <param name="Reason">Why it was raised.</param>
/// <param name="Detail">A human-readable explanation.</param>
/// <param name="RawReference">The reference the payer entered, if any.</param>
/// <param name="Candidates">Candidates found at the time it was raised.</param>
/// <param name="Amount">The payment amount.</param>
/// <param name="HeldBy">The customer holding the funds, or null if they are tenant unidentified funds.</param>
/// <param name="OpenedAt">When it was raised.</param>
/// <param name="Status">Open, resolved or dismissed.</param>
/// <param name="Resolution">How it was resolved, if it was.</param>
public sealed record PaymentExceptionView(
    PaymentExceptionId ExceptionId,
    PaymentId PaymentId,
    PaymentExceptionReason Reason,
    string Detail,
    string? RawReference,
    IReadOnlyList<CandidateView> Candidates,
    Money Amount,
    AccountId? HeldBy,
    DateTimeOffset OpenedAt,
    PaymentExceptionStatus Status,
    ResolutionView? Resolution);

/// <summary>Filters open exceptions.</summary>
public sealed record ExceptionFilter
{
    /// <summary>Only exceptions with this reason.</summary>
    public PaymentExceptionReason? Reason { get; init; }

    /// <summary>Only exceptions raised before this instant, for example to find ageing items.</summary>
    public DateTimeOffset? OpenedBefore { get; init; }
}

/// <summary>Assigns unidentified funds to a customer, resolving the exception.</summary>
public sealed record AssignToCustomerRequest : InstalmentFlowRequest
{
    /// <summary>The exception.</summary>
    public required PaymentExceptionId ExceptionId { get; init; }

    /// <summary>The customer the funds belong to.</summary>
    public required AccountRef Account { get; init; }

    /// <summary>Optionally, the customer's obligation the funds are for. Allocation follows in a later release.</summary>
    public ObligationRef? Obligation { get; init; }

    /// <summary>Who is resolving it.</summary>
    public required string ResolvedBy { get; init; }

    /// <summary>An optional note.</summary>
    public string? Note { get; init; }
}

/// <summary>Closes an exception without moving funds.</summary>
public sealed record DismissExceptionRequest : InstalmentFlowRequest
{
    /// <summary>The exception.</summary>
    public required PaymentExceptionId ExceptionId { get; init; }

    /// <summary>Why it is being dismissed.</summary>
    public required string Reason { get; init; }

    /// <summary>Who is dismissing it.</summary>
    public required string ResolvedBy { get; init; }
}

/// <summary>The result of resolving an exception.</summary>
/// <param name="Exception">The exception after resolution.</param>
/// <param name="AccountId">The customer now holding the funds, if any.</param>
/// <param name="UnappliedAfter">That customer's unapplied funds in the payment currency, if any.</param>
public sealed record ResolutionResult(PaymentExceptionView Exception, AccountId? AccountId, Money? UnappliedAfter);

/// <summary>
/// The payment exception queue (ADR-009). Resolutions are ordinary commands: they are idempotent, and a wrong
/// resolution is corrected by a compensating command, never by editing history.
/// </summary>
public interface IPaymentExceptions
{
    /// <summary>Open exceptions, oldest first.</summary>
    IAsyncEnumerable<PaymentExceptionView> ListOpenAsync(ExceptionFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>One exception, whatever its status.</summary>
    Task<PaymentExceptionView> GetAsync(PaymentExceptionId exceptionId, CancellationToken cancellationToken = default);

    /// <summary>Moves unidentified funds to a customer's unapplied funds.</summary>
    /// <exception cref="RuleViolationException">The exception is resolved, or its funds are not unidentified (<c>IF1012</c>).</exception>
    Task<ResolutionResult> AssignToCustomerAsync(AssignToCustomerRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes an exception without moving funds. Not allowed while the funds are still tenant unidentified funds
    /// (invariant I-22): assign them first.
    /// </summary>
    Task<ResolutionResult> DismissAsync(DismissExceptionRequest request, CancellationToken cancellationToken = default);
}
