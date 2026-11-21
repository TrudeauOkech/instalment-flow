using System.Globalization;

namespace InstalmentFlow.Testing;

/// <summary>
/// Readable, executable scenarios against an in-memory engine (doc 04 §6). Steps are recorded by
/// <see cref="ScenarioBuilder.Given"/>, <see cref="ScenarioBuilder.When"/> and <see cref="ScenarioBuilder.Then"/>
/// and run in order by <see cref="ScenarioBuilder.RunAsync"/>. Amounts are in the tenant's base currency.
/// </summary>
/// <example>
/// <code>
/// var run = await Scenario.InMemory()
///     .Given(a => a.Customer("C1").Loan("LN100001", Plan.Once(500m)))
///     .When(a => a.PaybillPayment(500m, billRef: "LN100001", transId: "TX1"))
///     .Then(a => a.Loan("LN100001").Status.ShouldBe(ObligationStatus.Settled))
///     .RunAsync();
/// await run.ShouldSatisfyAllInvariantsAsync();
/// </code>
/// </example>
public static class Scenario
{
    /// <summary>The provider account used for payments unless a step says otherwise.</summary>
    public const string DefaultSource = "mpesa:600123";

    /// <summary>Starts a scenario on a fresh in-memory engine with one tenant.</summary>
    /// <param name="tenant">The tenant ID.</param>
    /// <param name="currency">The tenant's base currency code.</param>
    /// <param name="timeZone">The tenant's IANA time zone.</param>
    /// <param name="today">The tenant's business date when the scenario starts; 2026-12-01 by default.</param>
    /// <param name="policy">Changes to the default tenant policy.</param>
    public static ScenarioBuilder InMemory(
        string tenant = "test",
        string currency = "KES",
        string timeZone = "Africa/Nairobi",
        DateOnly? today = null,
        Func<TenantPolicy, TenantPolicy>? policy = null) =>
        new(tenant, Currency.FromCode(currency), timeZone, today ?? new DateOnly(2026, 12, 1), policy);
}

/// <summary>A scenario being described. Nothing runs until <see cref="RunAsync"/>.</summary>
public sealed class ScenarioBuilder
{
    private readonly List<Func<ScenarioRun, Task>> _steps = [];
    private readonly Dictionary<string, string[]> _resolverMap = new(StringComparer.Ordinal);
    private readonly TenantId _tenant;
    private readonly Currency _currency;
    private readonly string _timeZone;
    private readonly DateOnly _today;
    private readonly Func<TenantPolicy, TenantPolicy>? _policy;

    internal ScenarioBuilder(string tenant, Currency currency, string timeZone, DateOnly today, Func<TenantPolicy, TenantPolicy>? policy)
    {
        _tenant = tenant;
        _currency = currency;
        _timeZone = timeZone;
        _today = today;
        _policy = policy;
    }

    /// <summary>Describes the starting state: customers, loans and a reference resolver.</summary>
    public ScenarioBuilder Given(Action<GivenSteps> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        describe(new GivenSteps(_steps, _resolverMap));
        return this;
    }

    /// <summary>Describes what happens: payments, reversals, charges, refunds, time passing.</summary>
    public ScenarioBuilder When(Action<WhenSteps> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        describe(new WhenSteps(_steps));
        return this;
    }

    /// <summary>Checks the outcome. Runs after every earlier step; use <see cref="Should.ShouldBe{T}"/> to assert.</summary>
    public ScenarioBuilder Then(Action<ThenContext> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _steps.Add(run =>
        {
            check(new ThenContext(run));
            return Task.CompletedTask;
        });
        return this;
    }

    /// <summary>Builds the engine and runs every step in order.</summary>
    public async Task<ScenarioRun> RunAsync(CancellationToken cancellationToken = default)
    {
        var clock = new ScenarioClock(_today, _timeZone);
        var factory = InstalmentFlowFactory.CreateInMemory(b =>
        {
            b.UseTimeProvider(clock).AddTenant(_tenant, _currency, _timeZone, _policy);
            if (_resolverMap.Count > 0)
            {
                b.UsePaymentReferenceResolver(new MapResolver(_resolverMap));
            }
        });
        var run = new ScenarioRun(factory.ForTenant(_tenant), clock, _currency, cancellationToken);
        foreach (var step in _steps)
        {
            await step(run).ConfigureAwait(false);
        }

        return run;
    }

    private sealed class MapResolver(Dictionary<string, string[]> map) : IPaymentReferenceResolver
    {
        public ValueTask<IReadOnlyList<ReferenceCandidate>> ResolveAsync(ReferenceResolutionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReferenceCandidate>>(
                map.TryGetValue(context.RawReference, out var references)
                    ? [.. references.Select(r => ReferenceCandidate.ForObligation(ObligationRef.ByReference(r)))]
                    : []);
    }
}

/// <summary>The engine and everything a scenario recorded, after it has run.</summary>
public sealed class ScenarioRun
{
    internal ScenarioRun(IInstalmentFlow flow, ScenarioClock clock, Currency currency, CancellationToken cancellationToken)
    {
        Flow = flow;
        Clock = clock;
        Currency = currency;
        CancellationToken = cancellationToken;
    }

    /// <summary>The tenant-scoped engine, for anything the steps do not cover.</summary>
    public IInstalmentFlow Flow { get; }

    /// <summary>The scenario's clock.</summary>
    public ScenarioClock Clock { get; }

    /// <summary>The tenant's base currency.</summary>
    public Currency Currency { get; }

    internal CancellationToken CancellationToken { get; }

    internal Dictionary<string, AccountId> Customers { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, RecordPaymentResult> Payments { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, ReversalResult> Reversals { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, ChargeResult> Charges { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, RefundResult> Refunds { get; } = new(StringComparer.Ordinal);

    internal Money Money(decimal amount) => new(amount, Currency);

    internal AccountId Customer(string externalId) =>
        Customers.TryGetValue(externalId, out var id) ? id : throw new ScenarioAssertionException($"No customer '{externalId}' was given.");

    private int _nextKey;

    internal string Key(string what) => $"scenario:{what}:{Interlocked.Increment(ref _nextKey).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Runs <see cref="IInstalmentQueries.VerifyAsync"/> and throws if any invariant is broken.</summary>
    /// <exception cref="ScenarioAssertionException">An invariant is broken.</exception>
    public Task ShouldSatisfyAllInvariantsAsync() => Flow.ShouldSatisfyAllInvariantsAsync(CancellationToken);
}

/// <summary>Steps that set up the starting state.</summary>
public sealed class GivenSteps
{
    private readonly List<Func<ScenarioRun, Task>> _steps;
    private readonly Dictionary<string, string[]> _resolverMap;

    internal GivenSteps(List<Func<ScenarioRun, Task>> steps, Dictionary<string, string[]> resolverMap)
    {
        _steps = steps;
        _resolverMap = resolverMap;
    }

    /// <summary>A customer with the host ID <paramref name="externalId"/>.</summary>
    public CustomerSteps Customer(string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        _steps.Add(async run =>
        {
            var opened = await run.Flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = run.Key("account"), ExternalId = externalId }, run.CancellationToken).ConfigureAwait(false);
            run.Customers[externalId] = opened.AccountId;
        });
        return new CustomerSteps(externalId, _steps);
    }

    /// <summary>A reference resolver that maps raw references to obligations, for example legacy account numbers.</summary>
    public GivenSteps Resolver(Action<ResolverSteps> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        map(new ResolverSteps(_resolverMap));
        return this;
    }
}

/// <summary>Maps raw references for the scenario's reference resolver.</summary>
public sealed class ResolverSteps
{
    private readonly Dictionary<string, string[]> _map;

    internal ResolverSteps(Dictionary<string, string[]> map) => _map = map;

    /// <summary>The raw reference <paramref name="raw"/> matches the obligations with these payment references.</summary>
    public ResolverSteps Map(string raw, params string[] paymentReferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        _map[raw] = paymentReferences;
        return this;
    }
}

/// <summary>Steps for one given customer.</summary>
public sealed class CustomerSteps
{
    private readonly string _customer;
    private readonly List<Func<ScenarioRun, Task>> _steps;

    internal CustomerSteps(string customer, List<Func<ScenarioRun, Task>> steps)
    {
        _customer = customer;
        _steps = steps;
    }

    /// <summary>A principal-only loan with the payment reference <paramref name="paymentReference"/>.</summary>
    public CustomerSteps Loan(string paymentReference, Plan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentReference);
        ArgumentNullException.ThrowIfNull(plan);
        _steps.Add(run => run.Flow.Obligations.OpenAsync(new OpenObligationRequest
        {
            IdempotencyKey = run.Key("loan"),
            Account = run.Customer(_customer),
            ExternalId = $"loan-{paymentReference}",
            PaymentReference = paymentReference,
            Components = [Component.Principal(run.Money(plan.Total))],
            Plan = plan.Build(run.Clock.Today, run.Currency),
        }, run.CancellationToken));
        return this;
    }
}

/// <summary>Steps that change the state.</summary>
public sealed class WhenSteps
{
    private readonly List<Func<ScenarioRun, Task>> _steps;

    internal WhenSteps(List<Func<ScenarioRun, Task>> steps) => _steps = steps;

    /// <summary>A paybill payment quoting <paramref name="billRef"/>, recorded once per <paramref name="transId"/>.</summary>
    public WhenSteps PaybillPayment(decimal amount, string billRef, string transId, string source = Scenario.DefaultSource) =>
        Record(transId, amount, source, PaymentTarget.ForReference(billRef));

    /// <summary>A payment from a known customer that does not say which loan it is for.</summary>
    public WhenSteps CustomerPayment(string customer, decimal amount, string transId, string source = Scenario.DefaultSource)
    {
        _steps.Add(async run => run.Payments[transId] = await run.Flow.Payments.RecordAsync(Request(run, transId, amount, source, PaymentTarget.ForCustomer(run.Customer(customer))), run.CancellationToken).ConfigureAwait(false));
        return this;
    }

    /// <summary>The same provider notification delivered <paramref name="times"/> times; it is recorded once.</summary>
    public WhenSteps PaybillPaymentDelivered(int times, decimal amount, string billRef, string transId, string source = Scenario.DefaultSource)
    {
        for (var i = 0; i < times; i++)
        {
            PaybillPayment(amount, billRef, transId, source);
        }

        return this;
    }

    /// <summary>The provider reversed <paramref name="amount"/> (all of it by default) of the payment <paramref name="transId"/>.</summary>
    public WhenSteps Reversal(string transId, decimal? amount = null, string? reversalRef = null, string source = Scenario.DefaultSource)
    {
        _steps.Add(async run => run.Reversals[transId] = await run.Flow.Payments.ReverseAsync(new ReversePaymentRequest
        {
            IdempotencyKey = run.Key("reversal"),
            Payment = PaymentRef.BySource(new ExternalReference(source, transId)),
            Amount = amount is { } a ? run.Money(a) : null,
            ReversalReference = reversalRef is null ? null : new ExternalReference(source, reversalRef),
            Reason = "Scenario reversal",
        }, run.CancellationToken).ConfigureAwait(false));
        return this;
    }

    /// <summary>The host posts a charge, remembered as <paramref name="name"/> for later steps.</summary>
    public WhenSteps Charge(string name, string loan, ChargeType type, decimal amount, string? chargeCode = null, ChargeDue? due = null)
    {
        _steps.Add(async run => run.Charges[name] = await run.Flow.Charges.PostAsync(new PostChargeRequest
        {
            IdempotencyKey = run.Key("charge"),
            Obligation = ObligationRef.ByReference(loan),
            Type = type,
            Amount = run.Money(amount),
            ChargeCode = chargeCode,
            Description = name,
            DuePolicy = due ?? ChargeDue.PerTenantPolicy,
        }, run.CancellationToken).ConfigureAwait(false));
        return this;
    }

    /// <summary>The host waives part of a component.</summary>
    public WhenSteps Waive(string loan, ComponentKey component, decimal amount, string reason = "Scenario waiver")
    {
        _steps.Add(run => run.Flow.Charges.WaiveAsync(new WaiveRequest
        {
            IdempotencyKey = run.Key("waiver"), Obligation = ObligationRef.ByReference(loan), Component = component, Amount = run.Money(amount), Reason = reason,
        }, run.CancellationToken));
        return this;
    }

    /// <summary>A refund of the customer's unapplied funds is requested, remembered as <paramref name="name"/>.</summary>
    public WhenSteps Refund(string name, string customer, decimal amount)
    {
        _steps.Add(async run => run.Refunds[name] = await run.Flow.Refunds.RequestAsync(new RequestRefundRequest
        {
            IdempotencyKey = run.Key("refund"), Account = run.Customer(customer), Amount = run.Money(amount), Reason = name, RequestedBy = "scenario",
        }, run.CancellationToken).ConfigureAwait(false));
        return this;
    }

    /// <summary>The host reports how the refund <paramref name="name"/> payout ended.</summary>
    public WhenSteps RefundCompleted(string name, RefundCompletion completion)
    {
        _steps.Add(async run =>
        {
            var refund = run.Refunds.TryGetValue(name, out var found) ? found : throw new ScenarioAssertionException($"No refund '{name}' was requested.");
            run.Refunds[name] = await run.Flow.Refunds.CompleteAsync(new CompleteRefundRequest
            {
                IdempotencyKey = run.Key("refund-complete"),
                Refund = refund.Refund.RefundId,
                Completion = completion,
                FailureReason = completion == RefundCompletion.Failed ? "Scenario failure" : null,
            }, run.CancellationToken).ConfigureAwait(false);
        });
        return this;
    }

    /// <summary>The customer's unapplied funds are allocated.</summary>
    public WhenSteps AllocateHeldFunds(string customer, string? toLoan = null)
    {
        _steps.Add(run => run.Flow.Allocations.AllocateAsync(new AllocateFundsRequest
        {
            IdempotencyKey = run.Key("allocate"), Account = run.Customer(customer), Obligation = toLoan is null ? null : ObligationRef.ByReference(toLoan),
        }, run.CancellationToken));
        return this;
    }

    /// <summary>Business days pass.</summary>
    public WhenSteps DaysPass(int days)
    {
        _steps.Add(run =>
        {
            run.Clock.Advance(TimeSpan.FromDays(days));
            return Task.CompletedTask;
        });
        return this;
    }

    /// <summary>Anything else, against the engine directly.</summary>
    public WhenSteps Do(Func<IInstalmentFlow, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _steps.Add(run => action(run.Flow));
        return this;
    }

    private WhenSteps Record(string transId, decimal amount, string source, PaymentTarget target)
    {
        _steps.Add(async run => run.Payments[transId] = await run.Flow.Payments.RecordAsync(Request(run, transId, amount, source, target), run.CancellationToken).ConfigureAwait(false));
        return this;
    }

    private static RecordPaymentRequest Request(ScenarioRun run, string transId, decimal amount, string source, PaymentTarget target) => new()
    {
        IdempotencyKey = run.Key("payment"),
        Source = new ExternalReference(source, transId),
        Amount = run.Money(amount),
        ValueAt = run.Clock.GetUtcNow(),
        Target = target,
    };
}

/// <summary>What a <see cref="ScenarioBuilder.Then"/> step can look at.</summary>
public sealed class ThenContext
{
    private readonly ScenarioRun _run;

    internal ThenContext(ScenarioRun run) => _run = run;

    /// <summary>The engine.</summary>
    public IInstalmentFlow Flow => _run.Flow;

    /// <summary>The result of the last recording of the payment <paramref name="transId"/>.</summary>
    public RecordPaymentResult Payment(string transId) =>
        _run.Payments.TryGetValue(transId, out var result) ? result : throw new ScenarioAssertionException($"No payment '{transId}' was recorded.");

    /// <summary>The result of the last reversal of the payment <paramref name="transId"/>.</summary>
    public ReversalResult Reversal(string transId) =>
        _run.Reversals.TryGetValue(transId, out var result) ? result : throw new ScenarioAssertionException($"No reversal of '{transId}' was recorded.");

    /// <summary>The charge posted as <paramref name="name"/>.</summary>
    public ChargeResult Charge(string name) =>
        _run.Charges.TryGetValue(name, out var result) ? result : throw new ScenarioAssertionException($"No charge '{name}' was posted.");

    /// <summary>The refund requested as <paramref name="name"/>, as last seen.</summary>
    public RefundView Refund(string name) =>
        _run.Refunds.TryGetValue(name, out var result) ? result.Refund : throw new ScenarioAssertionException($"No refund '{name}' was requested.");

    /// <summary>The loan with payment reference <paramref name="paymentReference"/>, as it stands now.</summary>
    public ObligationView Loan(string paymentReference) =>
        Wait(_run.Flow.Queries.GetObligationAsync(ObligationRef.ByReference(paymentReference), cancellationToken: _run.CancellationToken));

    /// <summary>The customer's account, as it stands now.</summary>
    public AccountSummary Account(string customer) =>
        Wait(_run.Flow.Queries.GetAccountAsync(_run.Customer(customer), cancellationToken: _run.CancellationToken));

    /// <summary>The customer's unapplied funds in the tenant's currency.</summary>
    public Money Unapplied(string customer) =>
        Account(customer).Unapplied.SingleOrDefault(m => m.Currency == _run.Currency) ?? Money.Zero(_run.Currency);

    /// <summary>The open payment exception of the payment <paramref name="transId"/>.</summary>
    public PaymentExceptionView OpenException(string transId)
    {
        var paymentId = Payment(transId).PaymentId;
        var open = Wait(ListAsync(_run.Flow.Exceptions.ListOpenAsync(cancellationToken: _run.CancellationToken)));
        return open.FirstOrDefault(e => e.PaymentId == paymentId) ?? throw new ScenarioAssertionException($"Payment '{transId}' has no open exception.");
    }

    // The in-memory engine completes synchronously, so Then steps can stay plain lambdas.
    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static async Task<List<PaymentExceptionView>> ListAsync(IAsyncEnumerable<PaymentExceptionView> items)
    {
        var list = new List<PaymentExceptionView>();
        await foreach (var item in items.ConfigureAwait(false))
        {
            list.Add(item);
        }

        return list;
    }
}

/// <summary>A repayment plan for a given loan, relative to the scenario's start date.</summary>
public sealed class Plan
{
    private readonly Func<DateOnly, Currency, PlanSpec> _build;

    private Plan(decimal total, Func<DateOnly, Currency, PlanSpec> build)
    {
        Total = total;
        _build = build;
    }

    /// <summary>The principal owed.</summary>
    public decimal Total { get; }

    /// <summary><paramref name="count"/> daily instalments of <paramref name="instalment"/>, the first due <paramref name="firstDueInDays"/> days from the start.</summary>
    public static Plan Daily(decimal instalment, int count, int firstDueInDays = 0) => Equal(Frequency.Daily, instalment, count, firstDueInDays);

    /// <summary>Weekly instalments.</summary>
    public static Plan Weekly(decimal instalment, int count, int firstDueInDays = 0) => Equal(Frequency.Weekly, instalment, count, firstDueInDays);

    /// <summary>Monthly instalments.</summary>
    public static Plan Monthly(decimal instalment, int count, int firstDueInDays = 0) => Equal(Frequency.Monthly, instalment, count, firstDueInDays);

    /// <summary>One instalment of <paramref name="amount"/>.</summary>
    public static Plan Once(decimal amount, int dueInDays = 0) => Daily(amount, 1, dueInDays);

    /// <summary>Instalments of the given amounts, one a day from the start.</summary>
    public static Plan Instalments(params decimal[] amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        return new Plan(amounts.Sum(), (start, currency) => PlanSpec.Custom(
            amounts.Select((a, i) => new PlannedInstalment(start.AddDays(i), [Component.Principal(new Money(a, currency))]))));
    }

    internal PlanSpec Build(DateOnly start, Currency currency) => _build(start, currency);

    private static Plan Equal(Frequency frequency, decimal instalment, int count, int firstDueInDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return new Plan(instalment * count, (start, _) => PlanSpec.Equal(frequency, count, start.AddDays(firstDueInDays)));
    }
}

/// <summary>A clock a scenario controls, starting at 09:00 on its start date in the tenant's time zone.</summary>
public sealed class ScenarioClock : TimeProvider
{
    private readonly TenantClock _tenantClock;
    private DateTimeOffset _now;

    internal ScenarioClock(DateOnly today, string timeZone)
    {
        _tenantClock = TenantClock.ForTimeZone(timeZone, this);
        _now = _tenantClock.ToInstant(today, new TimeOnly(9, 0));
    }

    /// <summary>The tenant's business date now.</summary>
    public DateOnly Today => _tenantClock.ToBusinessDate(_now);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves time forward.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        _now += by;
    }
}

/// <summary>Assertions for scenario checks, independent of any test framework.</summary>
public static class Should
{
    /// <summary>Throws unless <paramref name="actual"/> equals <paramref name="expected"/>; returns <paramref name="actual"/>.</summary>
    /// <exception cref="ScenarioAssertionException">They differ.</exception>
    public static T ShouldBe<T>(this T actual, T expected) =>
        EqualityComparer<T>.Default.Equals(actual, expected) ? actual : throw new ScenarioAssertionException($"Expected {expected}, but was {actual}.");

    /// <summary>
    /// Runs <see cref="IInstalmentQueries.VerifyAsync"/> on the tenant and throws if any invariant is broken. Use it
    /// at the end of a host's own integration tests too.
    /// </summary>
    /// <exception cref="ScenarioAssertionException">An invariant is broken.</exception>
    public static async Task ShouldSatisfyAllInvariantsAsync(this IInstalmentFlow flow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var report = await flow.Queries.VerifyAsync(VerifyScope.Tenant, cancellationToken).ConfigureAwait(false);
        if (!report.Passed)
        {
            throw new ScenarioAssertionException("Invariant violations:\n" + string.Join('\n', report.Violations.Select(v => $"{v.Invariant}: {v.Message}")));
        }
    }
}

/// <summary>A scenario check failed.</summary>
public sealed class ScenarioAssertionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ScenarioAssertionException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ScenarioAssertionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public ScenarioAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
