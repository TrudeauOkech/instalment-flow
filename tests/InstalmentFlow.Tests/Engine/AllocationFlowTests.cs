namespace InstalmentFlow.Tests;

/// <summary>Allocation end to end: recording, explicit allocation, exception resolution and failure handling.</summary>
public sealed class AllocationFlowTests
{
    private const string Tenant = "acme-ke";
    private static readonly DateOnly s_today = new(2026, 12, 1);
    private static readonly decimal[] s_scenario4 = [100m, 150m, 50m, 200m];

    private readonly FixedTimeProvider _time = FixedTimeProvider.AtNairobiDate(s_today);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ----- The brief's scenarios -----

    [Fact]
    public async Task Scenario2_two_concurrent_400_payments_against_500_leave_300_credit()
    {
        var (factory, flow) = Engine();
        var customer = await Customer(flow, "C-1");
        await Loan(flow, customer, "LN100001", Single(500m, s_today));

        var results = await Task.WhenAll(
            Task.Run(() => flow.Payments.RecordAsync(Paybill("TX1", 400m, "LN100001"), Ct)),
            Task.Run(() => flow.Payments.RecordAsync(Paybill("TX2", 400m, "LN100001"), Ct)));

        Assert.Equal([RecordOutcome.Allocated, RecordOutcome.PartiallyAllocated], results.Select(r => r.Outcome).Order());
        Assert.Equal(Money.Kes(500m), Money.Sum(Currency.KES, results.Select(r => r.Allocated)));
        var loan = await flow.Queries.GetObligationAsync(ObligationRef.ByReference("LN100001"), cancellationToken: Ct);
        Assert.Equal(ObligationStatus.Settled, loan.Status);
        Assert.Equal(Money.Kes(300m), (await flow.Queries.GetAccountAsync(customer, cancellationToken: Ct)).Unapplied.Single());
        Invariants.AssertHold(factory, Tenant);
    }

    [Fact]
    public async Task Scenario4_four_small_payments_settle_one_instalment_and_stay_traceable()
    {
        var (factory, flow) = Engine();
        var customer = await Customer(flow, "C-1");
        await Loan(flow, customer, "LN100001", Single(500m, s_today));

        var results = new List<RecordPaymentResult>();
        foreach (var (amount, i) in s_scenario4.Select((a, i) => (a, i)))
        {
            results.Add(await flow.Payments.RecordAsync(Paybill($"TX{i}", amount, "LN100001"), Ct));
        }

        Assert.All(results, r => Assert.Equal(RecordOutcome.Allocated, r.Outcome));
        Assert.Empty(results[2].InstalmentsSettled);
        Assert.Single(results[3].InstalmentsSettled);
        Assert.Single(results[3].ObligationsSettled);
        var instalment = (await flow.Queries.GetObligationAsync(ObligationRef.ByReference("LN100001"), cancellationToken: Ct)).Instalments.Single();
        Assert.Equal((InstalmentPaymentState.Paid, Money.Kes(500m)), (instalment.PaymentState, instalment.Paid));
        foreach (var (result, amount) in results.Zip([100m, 150m, 50m, 200m]))
        {
            var payment = await flow.Queries.GetPaymentAsync(result.PaymentId, Ct);
            Assert.Equal((Money.Kes(amount), Money.Kes(0m)), (payment.Allocated, payment.Unapplied));
            Assert.Equal(Money.Kes(amount), Assert.Single(result.Lines).Amount);
        }

        Invariants.AssertHold(factory, Tenant);
    }

    // ----- Overpayment, credit and customer direction -----

    [Fact]
    public async Task Overpayment_is_held_and_applied_when_the_next_instalment_falls_due()
    {
        var (factory, flow) = Engine();
        var customer = await Customer(flow, "C-1");
        await Loan(flow, customer, "LN100001", PlanSpec.Equal(Frequency.Daily, 3, s_today));   // 3 × KES 500 = 1,500

        var paid = await Loan(flow, customer, "LN100002", Single(100m, s_today));
        var result = await flow.Payments.RecordAsync(Paybill("TX1", 700m, "LN100001"), Ct);
        Assert.Equal((RecordOutcome.PartiallyAllocated, Money.Kes(500m), Money.Kes(200m)), (result.Outcome, result.Allocated, result.UnappliedAfter));

        _time.Now = _time.Now.AddDays(1);
        var applied = await flow.Allocations.AllocateAsync(new AllocateFundsRequest { IdempotencyKey = "daily-1", Account = customer }, Ct);

        Assert.Equal(Money.Kes(200m), applied.Allocated.Single());
        Assert.All(applied.Lines, l => Assert.Equal(2, l.InstalmentSequence));   // the directed loan, not LN100002 (I-21)
        var other = await flow.Queries.GetObligationAsync(paid, cancellationToken: Ct);
        Assert.Equal(Money.Kes(100m), other.Outstanding);
        Invariants.AssertHold(factory, Tenant);
    }

    [Fact]
    public async Task Directed_payment_never_spills_onto_another_loan_I21()
    {
        var (factory, flow) = Engine();
        var customer = await Customer(flow, "C-1");
        await Loan(flow, customer, "LN100001", Single(300m, s_today));
        var otherLoan = await Loan(flow, customer, "LN100002", Single(300m, s_today.AddDays(-5)));   // older and overdue

        var result = await flow.Payments.RecordAsync(Paybill("TX1", 1_000m, "LN100001"), Ct);

        Assert.Equal((Money.Kes(300m), Money.Kes(700m)), (result.Allocated, result.UnappliedAfter));
        Assert.Single(result.ObligationsSettled);
        Assert.Equal(Money.Kes(300m), (await flow.Queries.GetObligationAsync(otherLoan, cancellationToken: Ct)).Outstanding);

        var again = await flow.Payments.RecordAsync(Paybill("TX2", 50m, "LN100001"), Ct);   // the settled loan
        Assert.Equal(PaymentExceptionReason.ObligationNotPayable, again.Exception!.Reason);
        Invariants.AssertHold(factory, Tenant);
    }

    [Fact]
    public async Task Customer_directed_allocation_overrides_the_recorded_direction_UC06()
    {
        var (factory, flow) = Engine();
        var customer = await Customer(flow, "C-1");
        await Loan(flow, customer, "LN100001", Single(300m, s_today.AddDays(3)));   // not due yet: held
        var loan2 = await Loan(flow, customer, "LN100002", Single(300m, s_today));
        var held = await flow.Payments.RecordAsync(Paybill("TX1", 200m, "LN100001"), Ct);
        Assert.Equal(RecordOutcome.HeldUnapplied, held.Outcome);

        var moved = await flow.Allocations.AllocateAsync(new AllocateFundsRequest { IdempotencyKey = "move", Account = customer, Obligation = loan2 }, Ct);

        Assert.Equal(loan2, Assert.Single(moved.Lines).ObligationId);
        Assert.Equal(Money.Kes(100m), (await flow.Queries.GetObligationAsync(loan2, cancellationToken: Ct)).Outstanding);
        Invariants.AssertHold(factory, Tenant);
    }

    // ----- Identification and the exception queue (I-20) -----

    [Fact]
    public async Task Unreferenced_payment_for_two_loans_is_never_allocated_without_instruction_I20()
    {
        var (factory, flow) = Engine();
        var customer = await Customer(flow, "C-1");
        await Loan(flow, customer, "LN100001", Single(300m, s_today));
        var loan2 = await Loan(flow, customer, "LN100002", Single(300m, s_today));
        var held = await flow.Payments.RecordAsync(Request("TX1", 200m, PaymentTarget.ForCustomer(customer)), Ct);

        var sweep = await flow.Allocations.AllocateAsync(new AllocateFundsRequest { IdempotencyKey = "sweep", Account = customer, AllowAcrossObligations = true }, Ct);
        Assert.Empty(sweep.Lines);   // open exception: not even an explicit sweep may touch it

        var resolved = await flow.Exceptions.AllocateToObligationAsync(new AllocateToObligationRequest
        {
            IdempotencyKey = "resolve", ExceptionId = held.Exception!.ExceptionId, Obligation = loan2, ResolvedBy = "ops@acme",
        }, Ct);

        Assert.Equal(ExceptionResolutionKind.AllocatedToObligation, resolved.Exception.Resolution!.Kind);
        Assert.Equal(Money.Kes(100m), (await flow.Queries.GetObligationAsync(loan2, cancellationToken: Ct)).Outstanding);
        Invariants.AssertHold(factory, Tenant);
    }

    [Fact]
    public async Task Tenant_opt_in_policies_allocate_unreferenced_payments()
    {
        var (factory, flow) = Engine(p => p with { SoleObligation = SoleObligationPolicy.AutoAllocate, MultipleObligations = MultipleObligationsPolicy.AllocateByStrategy });
        var single = await Customer(flow, "C-1");
        await Loan(flow, single, "LN100001", Single(300m, s_today));
        var multi = await Customer(flow, "C-2");
        await Loan(flow, multi, "LN100002", Single(100m, s_today.AddDays(-1)));
        await Loan(flow, multi, "LN100003", Single(100m, s_today));

        var sole = await flow.Payments.RecordAsync(Request("TX1", 300m, PaymentTarget.ForCustomer(single)), Ct);
        var across = await flow.Payments.RecordAsync(Request("TX2", 150m, PaymentTarget.ForCustomer(multi)), Ct);

        Assert.Equal(RecordOutcome.Allocated, sole.Outcome);
        Assert.Equal([100m, 50m], across.Lines.Select(l => l.Amount.Amount));   // oldest due first across both loans
        Assert.Null(across.Exception);
        Invariants.AssertHold(factory, Tenant);
    }

    [Fact]
    public async Task Assigning_unidentified_funds_with_an_obligation_allocates_in_the_same_command()
    {
        var (factory, flow) = Engine();
        var customer = await Customer(flow, "C-1");
        var loan = await Loan(flow, customer, "LN100001", Single(500m, s_today));
        var typo = await flow.Payments.RecordAsync(Paybill("TX1", 500m, "LN10001"), Ct);

        await flow.Exceptions.AssignToCustomerAsync(new AssignToCustomerRequest
        {
            IdempotencyKey = "assign", ExceptionId = typo.Exception!.ExceptionId, Account = customer, Obligation = loan, ResolvedBy = "ops@acme",
        }, Ct);

        Assert.Equal(ObligationStatus.Settled, (await flow.Queries.GetObligationAsync(loan, cancellationToken: Ct)).Status);
        Assert.Equal(Money.Kes(500m), (await flow.Queries.GetPaymentAsync(typo.PaymentId, Ct)).Allocated);
        Invariants.AssertHold(factory, Tenant);
    }

    // ----- Strategy failures (ADR-002, ADR-006) -----

    [Fact]
    public async Task A_faulty_custom_strategy_never_costs_the_payment()
    {
        var (factory, flow) = Engine(p => p with { Allocation = OverAllocating.Reference }, b => b.AddAllocationStrategy(new OverAllocating()));
        var customer = await Customer(flow, "C-1");
        await Loan(flow, customer, "LN100001", Single(500m, s_today));

        var result = await flow.Payments.RecordAsync(Paybill("TX1", 200m, "LN100001"), Ct);

        Assert.Equal(RecordOutcome.HeldUnapplied, result.Outcome);
        Assert.Equal(PaymentExceptionReason.AllocationFailed, result.Exception!.Reason);
        Assert.Contains("plan was rejected", result.Exception.Detail, StringComparison.Ordinal);
        Assert.Equal(Money.Kes(200m), result.UnappliedAfter);
        Invariants.AssertHold(factory, Tenant);
    }

    [Fact]
    public async Task A_throwing_custom_strategy_never_costs_the_payment()
    {
        var (factory, flow) = Engine(p => p with { Allocation = Throwing.Reference }, b => b.AddAllocationStrategy(new Throwing()));
        var customer = await Customer(flow, "C-1");
        await Loan(flow, customer, "LN100001", Single(500m, s_today));

        var result = await flow.Payments.RecordAsync(Paybill("TX1", 200m, "LN100001"), Ct);

        Assert.Equal(PaymentExceptionReason.AllocationFailed, result.Exception!.Reason);
        Invariants.AssertHold(factory, Tenant);
    }

    [Fact]
    public async Task Unknown_strategies_are_rejected_IF2003()
    {
        var (_, flow) = Engine();
        var factory = InstalmentFlowFactory.CreateInMemory(b => b.UseTimeProvider(_time));

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => factory.Tenants.CreateAsync(new CreateTenantRequest
        {
            TenantId = "x", BaseCurrency = Currency.KES, TimeZone = "Africa/Nairobi",
            Policy = TenantPolicy.Default with { Allocation = new StrategyRef("does-not-exist", 1) },
        }, Ct));
        var customer = await Customer(flow, "C-1");
        var request = new AllocateFundsRequest { IdempotencyKey = "a", Account = customer, Strategy = new StrategyRef("nope", 1) };

        Assert.Equal(ErrorCodes.UnknownAllocationStrategy, ex.ErrorCode);
        Assert.Equal(ErrorCodes.UnknownAllocationStrategy, (await Assert.ThrowsAsync<RuleViolationException>(() => flow.Allocations.AllocateAsync(request, Ct))).ErrorCode);
    }

    [Fact]
    public async Task Allocations_record_the_strategy_and_policy_version()
    {
        var (factory, flow) = Engine();
        var customer = await Customer(flow, "C-1");
        await Loan(flow, customer, "LN100001", Single(500m, s_today));
        await factory.Tenants.UpdatePolicyAsync(Tenant, p => p with { Allocation = AllocationStrategies.PrincipalFirst.Ref }, "ops", Ct);

        var result = await flow.Payments.RecordAsync(Paybill("TX1", 100m, "LN100001"), Ct);

        Assert.Equal(AllocationStrategies.PrincipalFirst.Ref, Assert.Single(result.Lines).Strategy);
        Assert.Equal(2, Assert.Single(factory.InMemoryStore!.Snapshot(Tenant).Allocations).PolicyVersion);
    }

    // ----- Pay-as-you-go -----

    [Fact]
    public async Task Payg_daily_plan_buys_days_ahead_with_apply_to_next_instalments()
    {
        var (factory, flow) = Engine(p => p with { FutureInstalments = FutureInstalmentPolicy.ApplyToNextInstalments });
        var customer = await Customer(flow, "C-1");
        await Loan(flow, customer, "LN100001", PlanSpec.Equal(Frequency.Daily, 30, s_today), principal: 1_500m);   // KES 50 a day

        await flow.Payments.RecordAsync(Paybill("TX1", 175m, "LN100001"), Ct);

        var view = await flow.Queries.GetObligationAsync(ObligationRef.ByReference("LN100001"), cancellationToken: Ct);
        Assert.Equal(s_today.AddDays(2), view.PaidThroughDate);
        Assert.Equal(InstalmentPaymentState.PartiallyPaid, view.Instalments[3].PaymentState);
        Assert.Equal(Money.Kes(25m), view.Instalments[3].Paid);
        Invariants.AssertHold(factory, Tenant);
    }

    // ----- Helpers -----

    private (InstalmentFlowFactory Factory, IInstalmentFlow Flow) Engine(Func<TenantPolicy, TenantPolicy>? policy = null, Action<InstalmentFlowBuilder>? configure = null)
    {
        var factory = InstalmentFlowFactory.CreateInMemory(b =>
        {
            b.UseTimeProvider(_time).UseConcurrencyRetries(50);
            configure?.Invoke(b);
            b.AddTenant(Tenant, Currency.KES, "Africa/Nairobi", p => (policy ?? (x => x))(p with { PaymentReference = new ReferenceFormat(@"^LN\d{6}$") }));
        });
        return (factory, factory.ForTenant(Tenant));
    }

    private static async Task<AccountId> Customer(IInstalmentFlow flow, string externalId) =>
        (await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = $"acct-{externalId}", ExternalId = externalId }, Ct)).AccountId;

    private static async Task<ObligationId> Loan(IInstalmentFlow flow, AccountId customer, string reference, PlanSpec plan, decimal? principal = null)
    {
        var total = principal ?? (plan.Generator is IScheduleGenerator g && g.Name == "custom"
            ? g.Generate(new ScheduleContext(Currency.KES, [])).Sum(i => i.Amounts.Sum(a => a.Amount.Amount))
            : 1_500m);
        return (await flow.Obligations.OpenAsync(new OpenObligationRequest
        {
            IdempotencyKey = $"open-{reference}",
            Account = customer,
            ExternalId = $"loan-{reference}",
            PaymentReference = reference,
            Components = [Component.Principal(Money.Kes(total))],
            Plan = plan,
        }, Ct)).Obligation.ObligationId;
    }

    private static PlanSpec Single(decimal amount, DateOnly due) =>
        PlanSpec.Custom([new PlannedInstalment(due, [Component.Principal(Money.Kes(amount))])]);

    private static RecordPaymentRequest Paybill(string transId, decimal amount, string billRef) =>
        Request(transId, amount, PaymentTarget.ForReference(billRef));

    private static RecordPaymentRequest Request(string transId, decimal amount, PaymentTarget target) => new()
    {
        IdempotencyKey = $"mpesa:600123:{transId}",
        Source = new ExternalReference("mpesa:600123", transId),
        Amount = Money.Kes(amount),
        ValueAt = new DateTimeOffset(2026, 12, 1, 7, 0, 0, TimeSpan.Zero),
        Target = target,
    };

    private sealed class OverAllocating : IAllocationStrategy
    {
        public static readonly StrategyRef Reference = new("test.over-allocating", 1);

        public StrategyRef Ref => Reference;

        public AllocationPlan Plan(AllocationContext context) =>
            new([.. context.Targets.Select(t => new AllocationLine(t, t.Outstanding + Money.Kes(1m)))]);
    }

    private sealed class Throwing : IAllocationStrategy
    {
        public static readonly StrategyRef Reference = new("test.throwing", 1);

        public StrategyRef Ref => Reference;

        public AllocationPlan Plan(AllocationContext context) => throw new InvalidOperationException("strategy bug");
    }
}
