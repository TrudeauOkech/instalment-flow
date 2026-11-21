namespace InstalmentFlow;

/// <summary>
/// The point in time a query reconstructs from the append-only record (ADR-004; docs/concepts.md). Everything
/// recorded at or before the cut-off is included; instalment status is evaluated for the business date. A cut-off
/// at or after now shows the current state, so a future date answers "what will be overdue on that day if nothing
/// changes".
/// </summary>
public sealed record AsOf
{
    private AsOf(DateOnly? businessDate, DateTimeOffset? instant)
    {
        BusinessDate = businessDate;
        Instant = instant;
    }

    /// <summary>The business date, for <see cref="Date"/>.</summary>
    public DateOnly? BusinessDate { get; }

    /// <summary>The instant, for <see cref="At"/>.</summary>
    public DateTimeOffset? Instant { get; }

    /// <summary>As at the end of a business date in the tenant's time zone.</summary>
    public static AsOf Date(DateOnly businessDate) => new(businessDate, null);

    /// <summary>As at an instant.</summary>
    public static AsOf At(DateTimeOffset instant) => new(null, instant);

    /// <summary>As at the end of a business date.</summary>
    public static implicit operator AsOf(DateOnly businessDate) => Date(businessDate);

    /// <summary>As at the end of a business date.</summary>
    public static AsOf FromDateOnly(DateOnly businessDate) => Date(businessDate);

    /// <inheritdoc />
    public override string ToString() => BusinessDate is { } date ? date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : Instant!.Value.ToString("O");
}

// ----- Statements -----

/// <summary>What a statement line records. The values match the journal's transaction kinds.</summary>
public enum StatementEntryKind
{
    /// <summary>An obligation was opened; its amounts are owed.</summary>
    ObligationOpened = 1,

    /// <summary>A payment was received.</summary>
    PaymentReceived = 2,

    /// <summary>Unidentified funds were assigned to the customer.</summary>
    PaymentAssigned = 4,

    /// <summary>Unapplied funds were allocated.</summary>
    FundsAllocated = 5,

    /// <summary>A payment was reversed.</summary>
    PaymentReversed = 6,

    /// <summary>A charge was posted.</summary>
    ChargePosted = 7,

    /// <summary>A charge was reversed.</summary>
    ChargeReversed = 8,

    /// <summary>An amount was waived.</summary>
    AmountWaived = 9,

    /// <summary>A refund was requested.</summary>
    RefundRequested = 10,

    /// <summary>A refund was paid out.</summary>
    RefundConfirmed = 11,

    /// <summary>A refund failed and the credit was returned.</summary>
    RefundFailed = 12,

    /// <summary>An obligation was cancelled and its balance written off.</summary>
    ObligationCancelled = 13,
}

/// <summary>
/// One line of a customer statement. <see cref="Balance"/> is the customer's net position afterwards: what they owe
/// (obligations and any reversal receivable) less their unapplied funds; negative means they are in credit.
/// </summary>
/// <param name="EffectiveDate">The business date of the event (a payment's value date).</param>
/// <param name="RecordedAt">When it was recorded.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Description">A human-readable description.</param>
/// <param name="Amount">The amount of the event.</param>
/// <param name="Allocated">How much was applied to obligations in the same transaction.</param>
/// <param name="Change">How the balance changed: positive when the customer owes more.</param>
/// <param name="Balance">The balance afterwards.</param>
public sealed record StatementLine(
    DateOnly EffectiveDate,
    DateTimeOffset RecordedAt,
    StatementEntryKind Kind,
    string Description,
    Money Amount,
    Money Allocated,
    Money Change,
    Money Balance);

// ----- Payment trace -----

/// <summary>A step in a payment's history.</summary>
public enum PaymentTraceStepKind
{
    /// <summary>Recorded and held by a customer.</summary>
    Received = 1,

    /// <summary>Recorded as tenant unidentified funds.</summary>
    Unidentified = 2,

    /// <summary>Unidentified funds assigned to a customer.</summary>
    Assigned = 3,

    /// <summary>Applied to an instalment component.</summary>
    Allocated = 4,

    /// <summary>An allocation was undone.</summary>
    AllocationReversed = 5,

    /// <summary>Part or all of the payment was reversed.</summary>
    Reversed = 6,

    /// <summary>A reversal found the value already refunded: a reversal receivable.</summary>
    ReversalDeficit = 7,

    /// <summary>A refund of the payment's funds was requested.</summary>
    RefundRequested = 8,

    /// <summary>That refund was paid out.</summary>
    RefundConfirmed = 9,

    /// <summary>That refund failed and the funds returned.</summary>
    RefundFailed = 10,

    /// <summary>A payment exception was raised.</summary>
    ExceptionRaised = 11,

    /// <summary>A payment exception was resolved or dismissed.</summary>
    ExceptionResolved = 12,
}

/// <summary>One step of a payment trace.</summary>
/// <param name="At">When it happened.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Amount">The amount moved, if any.</param>
/// <param name="ObligationId">The obligation involved, if any.</param>
/// <param name="InstalmentSequence">The instalment involved, if any.</param>
/// <param name="Component">The component involved, if any.</param>
/// <param name="Detail">A human-readable explanation.</param>
public sealed record PaymentTraceStep(
    DateTimeOffset At,
    PaymentTraceStepKind Kind,
    Money? Amount,
    ObligationId? ObligationId,
    int? InstalmentSequence,
    ComponentKey? Component,
    string Detail);

/// <summary>
/// Where every shilling of a payment went (ADR-005): the receipt, its lot, allocations, reversals, refunds and
/// exceptions, oldest first. The totals reconcile: <c>Amount = Allocated + Unapplied + Reversed (less deficit) + Refunded</c>.
/// </summary>
/// <param name="Payment">The payment as it stands now.</param>
/// <param name="Deficit">Reversed value that had already been refunded, owed back as a reversal receivable.</param>
/// <param name="Steps">Its history, oldest first.</param>
public sealed record PaymentTrace(PaymentView Payment, Money Deficit, IReadOnlyList<PaymentTraceStep> Steps);

// ----- Journal export -----

/// <summary>A position in the tenant's journal, for incremental export to a general ledger.</summary>
/// <param name="Position">The position of the last transaction read; 0 before the first.</param>
public readonly record struct JournalCursor(long Position)
{
    /// <summary>Before the first transaction.</summary>
    public static JournalCursor Start => default;
}

/// <summary>One ledger line of an exported journal transaction.</summary>
/// <param name="Account">The subledger account code, for example <c>clearing:mpesa:600123</c> (doc 02 §5).</param>
/// <param name="Currency">The currency.</param>
/// <param name="Debit">The debit amount, or zero.</param>
/// <param name="Credit">The credit amount, or zero.</param>
/// <param name="SourceType">The kind of fact the line comes from, for example <c>Payment</c>.</param>
/// <param name="SourceId">That fact's ID.</param>
public sealed record JournalLineView(string Account, Currency Currency, decimal Debit, decimal Credit, string SourceType, Guid SourceId);

/// <summary>An exported, balanced journal transaction. Journal transactions are never changed (ADR-004).</summary>
/// <param name="Cursor">This transaction's position; pass it to continue reading after it.</param>
/// <param name="TransactionId">The transaction ID.</param>
/// <param name="Kind">The business event, for example <c>PaymentReceived</c>. Values are stable.</param>
/// <param name="AccountId">The customer, if any.</param>
/// <param name="EffectiveDate">The business date it is effective.</param>
/// <param name="RecordedAt">When it was recorded.</param>
/// <param name="Lines">The ledger lines.</param>
public sealed record JournalTransactionView(
    JournalCursor Cursor,
    Guid TransactionId,
    string Kind,
    AccountId? AccountId,
    DateOnly EffectiveDate,
    DateTimeOffset RecordedAt,
    IReadOnlyList<JournalLineView> Lines);

// ----- Verification -----

/// <summary>What <see cref="IInstalmentQueries.VerifyAsync"/> checks.</summary>
public sealed record VerifyScope
{
    private VerifyScope()
    {
    }

    /// <summary>Everything stored for the tenant.</summary>
    public static VerifyScope Tenant { get; } = new();
}

/// <summary>One broken invariant.</summary>
/// <param name="Invariant">The invariant, for example <c>I-5</c> (doc 02 §7).</param>
/// <param name="Message">What is wrong.</param>
public sealed record InvariantViolation(string Invariant, string Message);

/// <summary>
/// The result of verifying a tenant: facts are folded and compared with projections, and the journal is folded
/// per ledger account and compared with both (doc 02 §7, the "V" invariants).
/// </summary>
/// <param name="VerifiedAt">When it ran.</param>
/// <param name="Passed">Whether every invariant holds.</param>
/// <param name="Violations">The broken invariants; empty when it passed.</param>
/// <param name="JournalTransactions">How many journal transactions were folded.</param>
/// <param name="Payments">How many payments were checked.</param>
/// <param name="Accounts">How many customer accounts were checked.</param>
public sealed record VerificationReport(
    DateTimeOffset VerifiedAt,
    bool Passed,
    IReadOnlyList<InvariantViolation> Violations,
    int JournalTransactions,
    int Payments,
    int Accounts);
