namespace InstalmentFlow;

/// <summary>Identifies a recorded payment within a tenant.</summary>
/// <param name="Value">A version 7 (time-ordered) UUID.</param>
public readonly record struct PaymentId(Guid Value)
{
    /// <summary>Returns the UUID.</summary>
    public override string ToString() => Value.ToString();

    internal static PaymentId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));
}

/// <summary>Refers to a payment by its ID or by its provider reference.</summary>
public sealed record PaymentRef
{
    private PaymentRef(PaymentId? id, ExternalReference? source)
    {
        Id = id;
        Source = source;
    }

    /// <summary>The payment ID, when referring by ID.</summary>
    public PaymentId? Id { get; }

    /// <summary>The provider reference, when referring by source.</summary>
    public ExternalReference? Source { get; }

    /// <summary>Refers to a payment by its ID.</summary>
    public static PaymentRef ById(PaymentId id) => new(id, null);

    /// <summary>Refers to a payment by its provider reference, for example <c>("mpesa:600123", "TJ3K9XYZ")</c>.</summary>
    public static PaymentRef BySource(ExternalReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new PaymentRef(null, source);
    }

    /// <summary>Refers to a payment by its ID.</summary>
    public static PaymentRef FromPaymentId(PaymentId id) => ById(id);

    /// <summary>Refers to a payment by its ID.</summary>
    public static implicit operator PaymentRef(PaymentId id) => ById(id);

    /// <inheritdoc />
    public override string ToString() => Id?.ToString() ?? Source!.ToString();
}

/// <summary>What the payer or host said the payment is for.</summary>
public enum PaymentTargetKind
{
    /// <summary>Nothing is known about what the payment is for.</summary>
    Unknown = 0,

    /// <summary>The host knows the obligation, for example an STK push it initiated.</summary>
    Obligation = 1,

    /// <summary>The payer quoted a reference, for example a paybill account number.</summary>
    Reference = 2,

    /// <summary>The customer is known but not the obligation.</summary>
    Customer = 3,
}

/// <summary>
/// What a payment is for, as far as the host or provider knows. Identification (ADR-009) turns this into where the
/// funds are held and which obligations they may be applied to; the payment is recorded whatever the outcome.
/// </summary>
public sealed record PaymentTarget
{
    /// <summary>Raw references longer than this are treated as invalid; the first characters are kept for the exception queue.</summary>
    public const int MaxRawReferenceLength = 256;

    private PaymentTarget(PaymentTargetKind kind, ObligationRef? obligation, string? reference, AccountRef? account)
    {
        Kind = kind;
        Obligation = obligation;
        RawReference = reference;
        Account = account;
    }

    /// <summary>Nothing is known about what the payment is for; it is recorded as unidentified.</summary>
    public static PaymentTarget Unknown { get; } = new(PaymentTargetKind.Unknown, null, null, null);

    /// <summary>The kind of target.</summary>
    public PaymentTargetKind Kind { get; }

    /// <summary>The obligation, for <see cref="PaymentTargetKind.Obligation"/>.</summary>
    public ObligationRef? Obligation { get; }

    /// <summary>The reference exactly as the payer entered it, for <see cref="PaymentTargetKind.Reference"/>.</summary>
    public string? RawReference { get; }

    /// <summary>The customer, for <see cref="PaymentTargetKind.Customer"/>.</summary>
    public AccountRef? Account { get; }

    /// <summary>The host knows which obligation the payment is for.</summary>
    public static PaymentTarget ForObligation(ObligationRef obligation)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        return new PaymentTarget(PaymentTargetKind.Obligation, obligation, null, null);
    }

    /// <summary>The payer quoted <paramref name="rawReference"/>, which may be empty or mistyped.</summary>
    public static PaymentTarget ForReference(string? rawReference) =>
        new(PaymentTargetKind.Reference, null, rawReference ?? string.Empty, null);

    /// <summary>The customer is known but not which obligation the payment is for.</summary>
    public static PaymentTarget ForCustomer(AccountRef account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new PaymentTarget(PaymentTargetKind.Customer, null, null, account);
    }
}

/// <summary>Records a confirmed incoming payment.</summary>
public sealed record RecordPaymentRequest : InstalmentFlowRequest
{
    /// <summary>The provider reference, unique per source, for example <c>("mpesa:600123", "TJ3K9XYZ")</c>.</summary>
    public required ExternalReference Source { get; init; }

    /// <summary>The confirmed amount. Must be positive.</summary>
    public required Money Amount { get; init; }

    /// <summary>When the provider says the money moved.</summary>
    public required DateTimeOffset ValueAt { get; init; }

    /// <summary>What the payment is for.</summary>
    public required PaymentTarget Target { get; init; }

    /// <summary>Optional provider details to keep with the payment: at most 32 entries, keys up to 64 and values up to 512 characters.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>What happened to a recorded payment.</summary>
public enum RecordOutcome
{
    /// <summary>Recorded and held as the customer's unapplied funds (allocation arrives in a later release).</summary>
    HeldUnapplied = 1,

    /// <summary>Recorded as tenant unidentified funds; an exception explains why.</summary>
    Unidentified = 2,

    /// <summary>Already recorded; nothing changed. The result describes the original recording.</summary>
    Duplicate = 3,
}

/// <summary>The result of recording a payment.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="PaymentId">The payment.</param>
/// <param name="AccountId">The customer holding the funds, or null if unidentified.</param>
/// <param name="IdentifiedObligation">The obligation the funds are directed to, if identification found one.</param>
/// <param name="Amount">The payment amount.</param>
/// <param name="UnappliedAfter">The customer's unapplied funds in this currency after recording; zero if unidentified.</param>
/// <param name="Exception">The payment exception raised by identification, if any.</param>
public sealed record RecordPaymentResult(
    RecordOutcome Outcome,
    PaymentId PaymentId,
    AccountId? AccountId,
    ObligationId? IdentifiedObligation,
    Money Amount,
    Money UnappliedAfter,
    PaymentExceptionView? Exception);

/// <summary>A recorded payment.</summary>
/// <param name="PaymentId">The payment.</param>
/// <param name="Source">The provider reference.</param>
/// <param name="Amount">The amount.</param>
/// <param name="ValueAt">When the provider says the money moved.</param>
/// <param name="RecordedAt">When it was recorded.</param>
/// <param name="TargetKind">What the payer or host said it was for.</param>
/// <param name="RawReference">The reference as entered by the payer, if any.</param>
/// <param name="AccountId">The customer now holding the funds, or null while unidentified.</param>
/// <param name="IdentifiedObligation">The obligation the funds are directed to, if any.</param>
/// <param name="Unapplied">How much of the payment has not yet been applied.</param>
/// <param name="ExceptionId">The payment exception raised for it, if any.</param>
/// <param name="Metadata">Provider details kept with the payment.</param>
public sealed record PaymentView(
    PaymentId PaymentId,
    ExternalReference Source,
    Money Amount,
    DateTimeOffset ValueAt,
    DateTimeOffset RecordedAt,
    PaymentTargetKind TargetKind,
    string? RawReference,
    AccountId? AccountId,
    ObligationId? IdentifiedObligation,
    Money Unapplied,
    PaymentExceptionId? ExceptionId,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>Payments.</summary>
public interface IPayments
{
    /// <summary>
    /// Records a confirmed payment. Recording never depends on identification succeeding (ADR-002): funds land in
    /// the customer's unapplied funds or the tenant's unidentified funds, and a payment exception explains anything
    /// that needs attention. Recording the same <see cref="RecordPaymentRequest.Source"/> again returns
    /// <see cref="RecordOutcome.Duplicate"/>; with a different amount it throws <c>IF1011</c>.
    /// </summary>
    Task<RecordPaymentResult> RecordAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional host extension that adds candidate obligations or customers for a raw reference, for example legacy
/// account numbers. It cannot bypass the ambiguity rule: more than one candidate always raises an exception.
/// </summary>
public interface IPaymentReferenceResolver
{
    /// <summary>Returns extra candidates for a reference; return an empty list when there are none.</summary>
    ValueTask<IReadOnlyList<ReferenceCandidate>> ResolveAsync(ReferenceResolutionContext context, CancellationToken cancellationToken);
}

/// <summary>What a reference resolver is told.</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="RawReference">The reference as entered by the payer.</param>
/// <param name="NormalisedReference">The normalised reference, or null if it does not match the tenant's format.</param>
/// <param name="ExactMatch">The obligation whose payment reference matched exactly, if any.</param>
public sealed record ReferenceResolutionContext(TenantId Tenant, string RawReference, string? NormalisedReference, ObligationId? ExactMatch);

/// <summary>A candidate returned by a reference resolver: an obligation, or a customer without a specific obligation.</summary>
public sealed record ReferenceCandidate
{
    private ReferenceCandidate(ObligationRef? obligation, AccountRef? account)
    {
        Obligation = obligation;
        Account = account;
    }

    /// <summary>The candidate obligation, if the reference identifies one.</summary>
    public ObligationRef? Obligation { get; }

    /// <summary>The candidate customer, if the reference identifies a customer but not an obligation.</summary>
    public AccountRef? Account { get; }

    /// <summary>The reference identifies this obligation.</summary>
    public static ReferenceCandidate ForObligation(ObligationRef obligation)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        return new ReferenceCandidate(obligation, null);
    }

    /// <summary>The reference identifies this customer, but not which obligation.</summary>
    public static ReferenceCandidate ForCustomer(AccountRef account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new ReferenceCandidate(null, account);
    }
}
