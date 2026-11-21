using InstalmentFlow.EntityFrameworkCore;
using InstalmentFlow.Payments.Simulation;
using InstalmentFlow.TestSupport;

namespace InstalmentFlow.Payments.Tests;

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// An engine with tenant <see cref="Tenant"/> (a customer with loan <see cref="Loan"/>), payments on top, and the
/// simulator connected to the inbox with paybill <see cref="Paybill"/> routed to the tenant. In memory by default;
/// with <c>IF_STORE=postgres</c>, on PostgreSQL (a database per harness).
/// </summary>
internal sealed class Harness
{
    public const string Tenant = "acme-ke";
    public const string Other = "beta-ke";
    public const string Paybill = "600123";
    public const string Loan = "LN-1";

    public static bool IsPostgres { get; } = string.Equals(Environment.GetEnvironmentVariable("IF_STORE"), "postgres", StringComparison.OrdinalIgnoreCase);

    private Harness(ManualClock clock, InstalmentFlowFactory flows, InstalmentFlowPayments payments, SimulatedProvider simulator)
    {
        Clock = clock;
        Flows = flows;
        Payments = payments;
        Simulator = simulator;
    }

    public ManualClock Clock { get; }

    public InstalmentFlowFactory Flows { get; }

    public InstalmentFlowPayments Payments { get; }

    public SimulatedProvider Simulator { get; }

    public IInstalmentFlow Flow => Flows.ForTenant(Tenant);

    public ITenantPayments TenantPayments => Payments.ForTenant(Tenant);

    public AccountId Customer { get; private set; }

    public ObligationId LoanId { get; private set; }

    public static string Source => $"sim:{Paybill}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Harness> CreateAsync(Action<PaymentsBuilder>? configure = null, SimulatedProvider? simulator = null)
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 3, 2, 6, 0, 0, TimeSpan.Zero));
        void Engine(InstalmentFlowBuilder b) => b
            .UseTimeProvider(clock)
            .AddTenant(Tenant, Currency.KES, "Africa/Nairobi")
            .AddTenant(Other, Currency.KES, "Africa/Nairobi");
        var sim = simulator ?? new SimulatedProvider("test-secret", clock);
        void Payments(PaymentsBuilder b)
        {
            b.AddProvider(sim).UseTimeProvider(clock);
            configure?.Invoke(b);
        }

        InstalmentFlowFactory flows;
        InstalmentFlowPayments payments;
        if (IsPostgres)
        {
            var database = await PostgresServer.CreateDatabaseAsync();
            var options = new PostgresStoreOptions { ConnectionString = database.Application };
            flows = await InstalmentFlowPostgres.CreateFactoryAsync(options, Engine, Ct);
            payments = await InstalmentFlowPostgres.CreatePaymentsAsync(options, flows, Payments, Ct);
        }
        else
        {
            flows = InstalmentFlowFactory.CreateInMemory(Engine);
            payments = InstalmentFlowPayments.CreateInMemory(flows, Payments);
        }

        sim.ConnectTo(payments.Inbox);
        var harness = new Harness(clock, flows, payments, sim);
        await payments.Routing.MapAsync("sim", Paybill, Tenant, Ct);
        await harness.SeedAsync();
        return harness;
    }

    /// <summary>Delivers due callbacks and processes the inbox.</summary>
    public async Task<InboxProcessingReport> DeliverAndProcessAsync()
    {
        await Simulator.DeliverDueAsync(Ct);
        return await Payments.Inbox.ProcessPendingAsync(Ct);
    }

    public SimulatedTransaction PayBill(decimal amount, string? reference = Loan, SimulatedFaults faults = SimulatedFaults.None, TimeSpan delay = default, string account = Paybill) =>
        Simulator.Pay(new SimulatedPayment { ProviderAccount = account, Amount = Money.Kes(amount), AccountReference = reference, Faults = faults, CallbackDelay = delay });

    public Task<ReconciliationReport> ReconcileAsync(DateTimeOffset? from = null, DateTimeOffset? to = null, IStatementSource? statement = null) =>
        Payments.Reconciliation.RunAsync(new ReconciliationRequest
        {
            Provider = "sim", ProviderAccount = Paybill, From = from ?? Clock.Now.AddDays(-1), To = to ?? Clock.Now.AddSeconds(1), Statement = statement,
        }, Ct);

    public async Task<IReadOnlyList<PaymentView>> PaymentsAsync()
    {
        var list = new List<PaymentView>();
        await foreach (var payment in Flow.Queries.ListPaymentsAsync(new PaymentQuery { Source = Source }, Ct))
        {
            list.Add(payment);
        }

        return list;
    }

    public async Task<ObligationView> LoanAsync() => await Flow.Queries.GetObligationAsync(ObligationRef.ByReference(Loan), cancellationToken: Ct);

    /// <summary>What has been paid on the loan of 1,000.</summary>
    public async Task<Money> LoanPaidAsync() => Money.Kes(1_000m).Subtract((await LoanAsync()).Outstanding);

    public async Task AssertLedgerVerifiesAsync()
    {
        var report = await Flow.Queries.VerifyAsync(VerifyScope.Tenant, Ct);
        Assert.True(report.Passed, string.Join('\n', report.Violations.Select(v => $"{v.Invariant}: {v.Message}")));
    }

    public Task<PaymentIntentView> InitiateAsync(string key, decimal amount = 400m, string payer = "254712345678") =>
        TenantPayments.Intents.InitiateAsync(new InitiateCollectionRequest
        {
            IdempotencyKey = key, Provider = "sim", ProviderAccount = Paybill, Payer = payer, Amount = Money.Kes(amount),
            Target = PaymentTarget.ForObligation(ObligationRef.ByReference(Loan)),
        }, Ct);

    private async Task SeedAsync()
    {
        var customer = await Flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct", ExternalId = "C-1" }, Ct);
        var loan = await Flow.Obligations.OpenAsync(new OpenObligationRequest
        {
            IdempotencyKey = "loan", Account = customer.AccountId, ExternalId = "L-1", PaymentReference = Loan,
            Components = [Component.Principal(Money.Kes(1_000m))], Plan = PlanSpec.Equal(Frequency.Monthly, 2, new DateOnly(2026, 3, 1)),
        }, Ct);
        Customer = customer.AccountId;
        LoanId = loan.Obligation.ObligationId;
    }
}
