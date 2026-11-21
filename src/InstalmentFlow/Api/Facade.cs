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

/// <summary>Read-only queries. Status values are derived for <c>asOfDate</c>, the tenant's current business date by default.</summary>
public interface IInstalmentQueries
{
    /// <summary>An account and a summary of its obligations.</summary>
    Task<AccountSummary> GetAccountAsync(AccountRef account, DateOnly? asOfDate = null, CancellationToken cancellationToken = default);

    /// <summary>An obligation with its balances and active schedule.</summary>
    Task<ObligationView> GetObligationAsync(ObligationRef obligation, DateOnly? asOfDate = null, CancellationToken cancellationToken = default);

    /// <summary>A recorded payment.</summary>
    Task<PaymentView> GetPaymentAsync(PaymentRef payment, CancellationToken cancellationToken = default);

    /// <summary>Every plan version of an obligation, including superseded and cancelled ones.</summary>
    Task<ScheduleView> GetScheduleAsync(ObligationRef obligation, DateOnly? asOfDate = null, CancellationToken cancellationToken = default);
}
