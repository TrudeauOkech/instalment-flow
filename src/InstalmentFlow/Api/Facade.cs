namespace InstalmentFlow;

/// <summary>Creates tenant-scoped <see cref="IInstalmentFlow"/> instances. There is no tenant-less entry point (ADR-007).</summary>
public interface IInstalmentFlowFactory
{
    /// <summary>Tenant administration.</summary>
    ITenantAdmin Tenants { get; }

    /// <summary>
    /// Returns the API scoped to <paramref name="tenantId"/>. Every operation on it reads and writes only that
    /// tenant's data; operations throw <see cref="UnknownTenantException"/> if the tenant does not exist.
    /// </summary>
    IInstalmentFlow ForTenant(TenantId tenantId);
}

/// <summary>The InstalmentFlow API for one tenant.</summary>
public interface IInstalmentFlow
{
    /// <summary>The tenant this instance is scoped to.</summary>
    TenantId Tenant { get; }

    /// <summary>Customer accounts.</summary>
    IAccounts Accounts { get; }

    /// <summary>Obligations (loans and financing agreements).</summary>
    IObligations Obligations { get; }

    /// <summary>Repayment plans.</summary>
    IPlans Plans { get; }

    /// <summary>Incoming payments.</summary>
    IPayments Payments { get; }

    /// <summary>Allocation of funds already recorded.</summary>
    IAllocations Allocations { get; }

    /// <summary>The payment exception queue.</summary>
    IPaymentExceptions Exceptions { get; }

    /// <summary>Host-posted charges, waivers and their reversals.</summary>
    ICharges Charges { get; }

    /// <summary>Refunds of unapplied funds, recorded only.</summary>
    IRefunds Refunds { get; }

    /// <summary>Read-only queries.</summary>
    IInstalmentQueries Queries { get; }
}

/// <summary>Customer accounts.</summary>
public interface IAccounts
{
    /// <summary>Opens a customer account.</summary>
    /// <exception cref="RuleViolationException">The external ID is already used in this tenant (<c>IF1007</c>).</exception>
    Task<AccountOpened> OpenAsync(OpenAccountRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Obligations.</summary>
public interface IObligations
{
    /// <summary>Opens an obligation for a customer and creates version 1 of its repayment plan.</summary>
    /// <exception cref="RuleViolationException">
    /// The payment reference is invalid (<c>IF1005</c>) or already used (<c>IF1006</c>), the external ID is already
    /// used (<c>IF1007</c>), or the schedule is invalid (<c>IF1008</c>).
    /// </exception>
    Task<ObligationOpened> OpenAsync(OpenObligationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels an obligation and its active plan (ADR-011). Allocations already made stay as history; unapplied
    /// funds stay with the customer; whatever is outstanding is cleared only by the request's write-off lines.
    /// </summary>
    /// <exception cref="RuleViolationException">
    /// The obligation is not active or settled (<c>IF4003</c>), or the write-offs do not match what is outstanding (<c>IF4002</c>).
    /// </exception>
    Task<ObligationCancelled> CancelAsync(CancelObligationRequest request, CancellationToken cancellationToken = default);

    /// <summary>What it would take to settle an obligation now. Read-only.</summary>
    Task<SettlementQuote> QuoteEarlySettlementAsync(QuoteSettlementRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Repayment plans.</summary>
public interface IPlans
{
    /// <summary>
    /// Replaces an obligation's plan with a new version, keeping the old one as history. Only allowed while no
    /// payment or waiver has touched the current plan (ADR-011); otherwise throws <c>IF4001</c>.
    /// </summary>
    Task<PlanSuperseded> SupersedeAsync(SupersedePlanRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only queries. <c>asOf</c> reconstructs the state at a past cut-off from the recorded facts (the journal fold),
/// so a past query is unaffected by later commands; by default it is now, evaluated for the tenant's business date.
/// </summary>
public interface IInstalmentQueries
{
    /// <summary>An account and a summary of its obligations.</summary>
    Task<AccountSummary> GetAccountAsync(AccountRef account, AsOf? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>An obligation with its balances and active schedule.</summary>
    Task<ObligationView> GetObligationAsync(ObligationRef obligation, AsOf? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>A recorded payment.</summary>
    Task<PaymentView> GetPaymentAsync(PaymentRef payment, CancellationToken cancellationToken = default);

    /// <summary>Every plan version of an obligation, including superseded and cancelled ones.</summary>
    Task<ScheduleView> GetScheduleAsync(ObligationRef obligation, AsOf? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Where every part of a payment went: receipt, lot, allocations, reversals, refunds and exceptions.</summary>
    Task<PaymentTrace> TracePaymentAsync(PaymentRef payment, CancellationToken cancellationToken = default);

    /// <summary>
    /// The customer's statement for business dates <paramref name="fromDate"/> to <paramref name="toDate"/> inclusive,
    /// in recording order, with a running balance that includes everything before <paramref name="fromDate"/>.
    /// </summary>
    IAsyncEnumerable<StatementLine> GetStatementAsync(AccountRef account, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// The tenant's journal after <paramref name="from"/>, in order, for export to a general ledger. Store the
    /// last transaction's <see cref="JournalTransactionView.Cursor"/> to continue later.
    /// </summary>
    IAsyncEnumerable<JournalTransactionView> ReadJournalAsync(JournalCursor from, CancellationToken cancellationToken = default);

    /// <summary>Checks every verifiable financial invariant (doc 02 §7) and reports what is broken.</summary>
    Task<VerificationReport> VerifyAsync(VerifyScope scope, CancellationToken cancellationToken = default);
}
