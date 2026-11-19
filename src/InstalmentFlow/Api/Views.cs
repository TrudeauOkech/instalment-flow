namespace InstalmentFlow;

/// <summary>Result of opening an account.</summary>
/// <param name="AccountId">The new account's ID.</param>
/// <param name="ExternalId">The host's customer ID.</param>
public sealed record AccountOpened(AccountId AccountId, string ExternalId);

/// <summary>Result of opening an obligation.</summary>
/// <param name="Obligation">The new obligation, evaluated for the tenant's current business date.</param>
public sealed record ObligationOpened(ObligationView Obligation);

/// <summary>Result of superseding a plan.</summary>
/// <param name="ObligationId">The obligation.</param>
/// <param name="PreviousVersion">The version that was superseded.</param>
/// <param name="ActiveVersion">The new active version.</param>
public sealed record PlanSuperseded(ObligationId ObligationId, int PreviousVersion, int ActiveVersion);

/// <summary>An account and its obligations.</summary>
/// <param name="AccountId">The account ID.</param>
/// <param name="ExternalId">The host's customer ID.</param>
/// <param name="OpenedAt">When the account was opened.</param>
/// <param name="Unapplied">Funds received but not yet applied, one entry per currency held.</param>
/// <param name="Obligations">A summary of each obligation, oldest first.</param>
public sealed record AccountSummary(
    AccountId AccountId,
    string ExternalId,
    DateTimeOffset OpenedAt,
    IReadOnlyList<Money> Unapplied,
    IReadOnlyList<ObligationSummary> Obligations);

/// <summary>A one-line summary of an obligation.</summary>
/// <param name="ObligationId">The obligation ID.</param>
/// <param name="ExternalId">The host's loan or agreement ID.</param>
/// <param name="PaymentReference">The normalised payment reference.</param>
/// <param name="Status">The lifecycle state.</param>
/// <param name="Outstanding">The total still owed.</param>
public sealed record ObligationSummary(ObligationId ObligationId, string ExternalId, string PaymentReference, ObligationStatus Status, Money Outstanding);

/// <summary>An obligation, its balances and its active schedule, evaluated for <see cref="AsOfDate"/>.</summary>
/// <param name="ObligationId">The obligation ID.</param>
/// <param name="AccountId">The account that owes it.</param>
/// <param name="ExternalId">The host's loan or agreement ID.</param>
/// <param name="PaymentReference">The normalised payment reference.</param>
/// <param name="Currency">The obligation's currency.</param>
/// <param name="Status">The lifecycle state.</param>
/// <param name="Description">The optional description.</param>
/// <param name="AsOfDate">The business date statuses were evaluated for.</param>
/// <param name="Components">Balances per component.</param>
/// <param name="Outstanding">The total still owed.</param>
/// <param name="Arrears">The amount outstanding on instalments whose due date has passed.</param>
/// <param name="DaysPastDue">Days since the oldest unpaid instalment fell due; zero if none is past due.</param>
/// <param name="PaidThroughDate">The due date of the last instalment in the unbroken run of covered instalments from the start, or null.</param>
/// <param name="ActivePlanVersion">The active plan version, or null if the plan was cancelled.</param>
/// <param name="Grace">The active plan's grace period.</param>
/// <param name="Instalments">The active plan's instalments.</param>
public sealed record ObligationView(
    ObligationId ObligationId,
    AccountId AccountId,
    string ExternalId,
    string PaymentReference,
    Currency Currency,
    ObligationStatus Status,
    string? Description,
    DateOnly AsOfDate,
    IReadOnlyList<ComponentBalanceView> Components,
    Money Outstanding,
    Money Arrears,
    int DaysPastDue,
    DateOnly? PaidThroughDate,
    int? ActivePlanVersion,
    GracePeriod? Grace,
    IReadOnlyList<InstalmentView> Instalments);

/// <summary>The balance of one component of an obligation.</summary>
/// <param name="Key">The component.</param>
/// <param name="Originated">The amount owed, including charges posted later.</param>
/// <param name="Paid">The amount covered by allocated payments.</param>
/// <param name="Waived">The amount waived.</param>
/// <param name="Outstanding">The amount still owed.</param>
public sealed record ComponentBalanceView(ComponentKey Key, Money Originated, Money Paid, Money Waived, Money Outstanding);

/// <summary>One instalment, evaluated for a business date.</summary>
/// <param name="Sequence">The instalment number, from 1.</param>
/// <param name="DueDate">The due date.</param>
/// <param name="Due">The amount due.</param>
/// <param name="Paid">The amount covered by allocated payments.</param>
/// <param name="Waived">The amount waived.</param>
/// <param name="Outstanding">The amount still owed.</param>
/// <param name="PaymentState">How much has been covered.</param>
/// <param name="Timing">Where it stands against its due date and grace period.</param>
/// <param name="DaysPastDue">Days since the due date while an amount is outstanding.</param>
/// <param name="Components">The amounts per component.</param>
public sealed record InstalmentView(
    int Sequence,
    DateOnly DueDate,
    Money Due,
    Money Paid,
    Money Waived,
    Money Outstanding,
    InstalmentPaymentState PaymentState,
    InstalmentTiming Timing,
    int DaysPastDue,
    IReadOnlyList<InstalmentComponentView> Components);

/// <summary>One component of one instalment.</summary>
/// <param name="Key">The component.</param>
/// <param name="Due">The amount due.</param>
/// <param name="Paid">The amount covered by allocated payments.</param>
/// <param name="Waived">The amount waived.</param>
/// <param name="Outstanding">The amount still owed.</param>
public sealed record InstalmentComponentView(ComponentKey Key, Money Due, Money Paid, Money Waived, Money Outstanding);

/// <summary>Every plan version of an obligation.</summary>
/// <param name="ObligationId">The obligation.</param>
/// <param name="ActiveVersion">The active version, or null if the plan was cancelled.</param>
/// <param name="Versions">All versions, oldest first.</param>
public sealed record ScheduleView(ObligationId ObligationId, int? ActiveVersion, IReadOnlyList<PlanVersionView> Versions);

/// <summary>One version of a repayment plan.</summary>
/// <param name="Version">The version number, from 1.</param>
/// <param name="Status">Active, superseded or cancelled.</param>
/// <param name="Grace">The grace period.</param>
/// <param name="Generator">The name of the generator that built it.</param>
/// <param name="CreatedAt">When the version was created.</param>
/// <param name="EndedAt">When it was superseded or cancelled.</param>
/// <param name="EndReason">Why it was superseded or cancelled.</param>
/// <param name="Instalments">Its instalments, evaluated for the query's business date.</param>
public sealed record PlanVersionView(
    int Version,
    PlanVersionStatus Status,
    GracePeriod Grace,
    string Generator,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    IReadOnlyList<InstalmentView> Instalments);
