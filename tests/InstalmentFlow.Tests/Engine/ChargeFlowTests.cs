namespace InstalmentFlow.Tests;

/// <summary>Host-posted charges and waivers (ADR-008), cancellation with write-off (ADR-011) and early settlement.</summary>
public sealed class ChargeFlowTests
{
    private static readonly DateOnly s_today = Flows.Today;

    private static CancellationToken Ct => Flows.Ct;

    // ----- Charges -----

    public static TheoryData<string, int> DuePolicies => new()
    {
        // Instalments fall due two days ago, yesterday and in five days.
        { "next", 3 },
        { "immediately", 2 },
        { "on1", 1 },
        { "tenant", 3 },
    };

    [Theory]
    [MemberData(nameof(DuePolicies))]
    public async Task A_charge_is_added_to_the_instalment_its_due_policy_chooses(string policy, int expected)
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001",
        [
            new PlannedInstalment(s_today.AddDays(-2), [Component.Principal(Money.Kes(100m))]),
            new PlannedInstalment(s_today.AddDays(-1), [Component.Principal(Money.Kes(100m))]),
            new PlannedInstalment(s_today.AddDays(5), [Component.Principal(Money.Kes(100m))]),
        ]);
        var due = policy switch
        {
            "next" => ChargeDue.NextUnpaidInstalment,
            "immediately" => ChargeDue.Immediately,
            "on1" => ChargeDue.OnInstalment(1),
            _ => ChargeDue.PerTenantPolicy,
        };

        var charge = await Post(f, 20m, due);

        Assert.Equal((expected, new ComponentKey(ComponentType.Penalty, "late")), (charge.InstalmentSequence, charge.Component));
        var loan = await f.LoanViewAsync("LN100001");
        Assert.Equal(Money.Kes(320m), loan.Outstanding);
        Assert.Contains(loan.Components, c => c.Key == charge.Component && c.Originated == Money.Kes(20m));
        Assert.Equal(Money.Kes(20m), loan.Instalments[expected - 1].Components.Single(c => c.Key == charge.Component).Due);
        f.AssertInvariants();
    }

    [Fact]
    public async Task A_charge_reopens_a_settled_obligation_and_is_paid_like_any_component()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 500m, "LN100001");

        await Post(f, 50m, ChargeDue.Immediately);
        Assert.Equal(ObligationStatus.Active, (await f.LoanViewAsync("LN100001")).Status);
        var paid = await f.PayAsync("TX2", 50m, "LN100001");

        Assert.Equal(new ComponentKey(ComponentType.Penalty, "late"), Assert.Single(paid.Lines).Component);
        Assert.Equal(ObligationStatus.Settled, (await f.LoanViewAsync("LN100001")).Status);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Reversing_a_paid_charge_unwinds_what_paid_it_and_keeps_the_funds()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        var charge = await Post(f, 50m, ChargeDue.Immediately);
        await f.PayAsync("TX1", 530m, "LN100001");   // principal 500, then 30 of the penalty

        var reversed = await f.Flow.Charges.ReverseAsync(new ReverseChargeRequest { IdempotencyKey = "rc", Charge = charge.ChargeId, Reason = "Posted in error" }, Ct);

        Assert.True(reversed.Reversed);
        Assert.Equal(Money.Kes(30m), Assert.Single(reversed.Unwinds).Amount);
        Assert.Equal(Money.Kes(0m), reversed.ObligationOutstanding);
        Assert.Equal(Money.Kes(30m), await f.UnappliedAsync(customer));
        Assert.Equal(ObligationStatus.Settled, (await f.LoanViewAsync("LN100001")).Status);
        var again = await Assert.ThrowsAsync<RuleViolationException>(() =>
            f.Flow.Charges.ReverseAsync(new ReverseChargeRequest { IdempotencyKey = "rc2", Charge = charge.ChargeId, Reason = "Again" }, Ct));
        Assert.Equal(ErrorCodes.InvalidRequest, again.ErrorCode);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Reversing_one_of_two_charges_unwinds_only_the_shortfall()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 100m);
        await Post(f, 50m, ChargeDue.Immediately);
        var second = await f.Flow.Charges.PostAsync(new PostChargeRequest
        {
            IdempotencyKey = "second", Obligation = ObligationRef.ByReference("LN100001"), Type = ChargeType.Penalty, ChargeCode = "late",
            Amount = Money.Kes(50m), Description = "Second penalty", DuePolicy = ChargeDue.Immediately,
        }, Ct);
        await f.PayAsync("TX1", 180m, "LN100001");   // principal 100, penalty 80 of 100: 20 outstanding

        var reversed = await f.Flow.Charges.ReverseAsync(new ReverseChargeRequest { IdempotencyKey = "rc", Charge = second.ChargeId, Reason = "Duplicate" }, Ct);

        Assert.Equal(Money.Kes(30m), Assert.Single(reversed.Unwinds).Amount);   // only what the 20 outstanding could not absorb
        Assert.Equal((Money.Kes(0m), Money.Kes(30m)), (reversed.ObligationOutstanding, await f.UnappliedAsync(customer)));
        f.AssertInvariants();
    }

    [Fact]
    public async Task A_charged_plan_cannot_be_superseded()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today.AddDays(10), 500m);
        await Post(f, 20m, ChargeDue.NextUnpaidInstalment);

        var error = await Assert.ThrowsAsync<RuleViolationException>(() => f.Flow.Plans.SupersedeAsync(new SupersedePlanRequest
        {
            IdempotencyKey = "s", Obligation = ObligationRef.ByReference("LN100001"), Reason = "x",
            Plan = PlanSpec.Custom([new PlannedInstalment(s_today.AddDays(20), [Component.Principal(Money.Kes(500m)), Component.Penalty(Money.Kes(20m), "late")])]),
        }, Ct));

        Assert.Equal(ErrorCodes.PlanHasFinancialHistory, error.ErrorCode);
    }

    [Fact]
    public async Task Invalid_charges_are_rejected()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);

        var codeOnInterest = await Assert.ThrowsAsync<RuleViolationException>(() => f.Flow.Charges.PostAsync(new PostChargeRequest
        {
            IdempotencyKey = "c1", Obligation = ObligationRef.ByReference("LN100001"), Type = ChargeType.Interest, ChargeCode = "late",
            Amount = Money.Kes(10m), Description = "x",
        }, Ct));
        var foreign = await Assert.ThrowsAsync<CurrencyMismatchException>(() => f.Flow.Charges.PostAsync(new PostChargeRequest
        {
            IdempotencyKey = "c2", Obligation = ObligationRef.ByReference("LN100001"), Type = ChargeType.Fee, Amount = Money.Of(10m, "USD"), Description = "x",
        }, Ct));
        var missing = await Assert.ThrowsAsync<RuleViolationException>(() => Post(f, 10m, ChargeDue.OnInstalment(9)));

        Assert.Equal((ErrorCodes.InvalidRequest, ErrorCodes.CurrencyMismatch, ErrorCodes.InvalidRequest), (codeOnInterest.ErrorCode, foreign.ErrorCode, missing.ErrorCode));
    }

    // ----- Waivers -----

    [Fact]
    public async Task A_waiver_clears_the_oldest_outstanding_amounts_of_one_component()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001",
        [
            new PlannedInstalment(s_today.AddDays(-1), [Component.Principal(Money.Kes(100m)), Component.Interest(Money.Kes(20m))]),
            new PlannedInstalment(s_today, [Component.Principal(Money.Kes(100m)), Component.Interest(Money.Kes(20m))]),
        ]);

        var waiver = await Waive(f, ComponentKey.Interest, 30m);

        Assert.Equal([(1, 20m), (2, 10m)], waiver.Lines.Select(l => (l.InstalmentSequence, l.Amount.Amount)));
        Assert.Equal(Money.Kes(210m), waiver.ObligationOutstanding);
        var tooMuch = await Assert.ThrowsAsync<RuleViolationException>(() => Waive(f, ComponentKey.Interest, 11m));
        Assert.Equal(ErrorCodes.InvalidRequest, tooMuch.ErrorCode);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Waiving_everything_settles_the_obligation_and_marks_instalments_waived()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 100m);

        var waiver = await Waive(f, ComponentKey.Principal, 100m);

        Assert.Equal(ObligationStatus.Settled, waiver.ObligationStatus);
        Assert.Equal(InstalmentPaymentState.Waived, (await f.LoanViewAsync("LN100001")).Instalments[0].PaymentState);
        var supersede = await Assert.ThrowsAsync<RuleViolationException>(() => f.Flow.Plans.SupersedeAsync(new SupersedePlanRequest
        {
            IdempotencyKey = "s", Obligation = ObligationRef.ByReference("LN100001"), Reason = "x",
            Plan = PlanSpec.Custom([new PlannedInstalment(s_today, [Component.Principal(Money.Kes(100m))])]),
        }, Ct));
        Assert.Equal(ErrorCodes.InvalidRequest, supersede.ErrorCode);   // a settled obligation cannot change plan
        f.AssertInvariants();
    }

    // ----- Cancellation (ADR-011) -----

    [Fact]
    public async Task Cancellation_writes_off_exactly_what_is_outstanding_and_keeps_history_and_credit()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 300m, 300m, 400m);
        await f.PayAsync("TX1", 450m, "LN100001");   // #1 300; 150 held (nothing else due)

        var cancelled = await Cancel(f, Component.Principal(Money.Kes(700m)));

        Assert.Equal((1, Money.Kes(150m)), (cancelled.PlanVersion, Assert.Single(cancelled.UnappliedAfter)));
        var loan = await f.LoanViewAsync("LN100001");
        Assert.Equal((ObligationStatus.Cancelled, Money.Kes(0m), (int?)null), (loan.Status, loan.Outstanding, loan.ActivePlanVersion));
        Assert.Equal(Money.Kes(700m), loan.Components.Single().Waived);
        var schedule = await f.Flow.Queries.GetScheduleAsync(ObligationRef.ByReference("LN100001"), cancellationToken: Ct);
        Assert.Equal(PlanVersionStatus.Cancelled, Assert.Single(schedule.Versions).Status);
        Assert.Single(f.Snapshot.Allocations);
        Assert.All(f.Snapshot.Waivers, w => Assert.Equal("Device returned", w.Reason));
        f.AssertInvariants();

        var twice = await Assert.ThrowsAsync<RuleViolationException>(() => Cancel(f, key: "again"));
        Assert.Equal(ErrorCodes.ObligationNotOpen, twice.ErrorCode);
        var after = await f.PayAsync("TX2", 100m, "LN100001");
        Assert.Equal(PaymentExceptionReason.ObligationNotPayable, after.Exception!.Reason);
        f.AssertInvariants();
    }

    public static TheoryData<int, string> WrongWriteOffs => new()
    {
        { 0, "none given" },
        { 699, "short" },
        { 701, "over" },
    };

    [Theory]
    [MemberData(nameof(WrongWriteOffs))]
    public async Task Cancellation_rejects_write_offs_that_do_not_match_IF4002(int amount, string because)
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 700m);

        var lines = amount == 0 ? Array.Empty<Component>() : [Component.Principal(Money.Kes(amount))];
        var error = await Assert.ThrowsAsync<RuleViolationException>(() => Cancel(f, lines));

        Assert.True(error.ErrorCode == ErrorCodes.WriteOffMismatch, because);
        Assert.Equal(ObligationStatus.Active, (await f.LoanViewAsync("LN100001")).Status);
    }

    [Fact]
    public async Task Reversing_a_payment_on_a_cancelled_obligation_shows_the_amount_owed_again()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 300m, 300m);
        await f.PayAsync("TX1", 300m, "LN100001");
        await Cancel(f, Component.Principal(Money.Kes(300m)));

        await f.ReverseAsync("TX1");

        var loan = await f.LoanViewAsync("LN100001");
        Assert.Equal((ObligationStatus.Cancelled, Money.Kes(300m)), (loan.Status, loan.Outstanding));
        f.AssertInvariants();
    }

    // ----- Early settlement -----

    [Fact]
    public async Task Early_settlement_quote_then_interest_waiver_and_payment_settle_the_loan()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001",
        [
            new PlannedInstalment(s_today, [Component.Principal(Money.Kes(300m)), Component.Interest(Money.Kes(30m))]),
            new PlannedInstalment(s_today.AddDays(30), [Component.Principal(Money.Kes(300m)), Component.Interest(Money.Kes(30m))]),
            new PlannedInstalment(s_today.AddDays(60), [Component.Principal(Money.Kes(300m)), Component.Interest(Money.Kes(30m))]),
        ]);
        await f.PayAsync("TX1", 400m, "LN100001");   // 330 due today; 70 held

        var quote = await f.Flow.Obligations.QuoteEarlySettlementAsync(new QuoteSettlementRequest { Obligation = ObligationRef.ByReference("LN100001") }, Ct);
        Assert.Equal((Money.Kes(0m), Money.Kes(660m), Money.Kes(660m)), (quote.Due, quote.NotYetDue, quote.Outstanding));
        Assert.Equal((Money.Kes(70m), Money.Kes(590m)), (quote.UnappliedFunds, quote.AmountToSettle));
        Assert.Equal(Money.Kes(60m), quote.Components.Single(c => c.Key == ComponentKey.Interest).NotYetDue);

        // The host forgives future interest (its own rule; ADR-008), then the customer pays the rest.
        await Waive(f, ComponentKey.Interest, 60m);
        await f.PayAsync("TX2", 530m, "LN100001");   // nothing is due today any more, so it is held
        await f.Flow.Allocations.AllocateAsync(new AllocateFundsRequest
        {
            IdempotencyKey = "settle", Account = customer, Obligation = ObligationRef.ByReference("LN100001"), FutureInstalments = FutureInstalmentPolicy.PrepayPrincipal,
        }, Ct);

        var loan = await f.LoanViewAsync("LN100001");
        Assert.Equal((ObligationStatus.Settled, Money.Kes(0m)), (loan.Status, loan.Outstanding));
        Assert.Equal(Money.Kes(0m), await f.UnappliedAsync(customer));
        f.AssertInvariants();
    }

    private static Task<ChargeResult> Post(Flows f, decimal amount, ChargeDue due) =>
        f.Flow.Charges.PostAsync(new PostChargeRequest
        {
            IdempotencyKey = $"charge-{amount}-{due}",
            Obligation = ObligationRef.ByReference("LN100001"),
            Type = ChargeType.Penalty,
            ChargeCode = "late",
            Amount = Money.Kes(amount),
            Description = "Late payment penalty",
            DuePolicy = due,
        }, Ct);

    private static Task<WaiverResult> Waive(Flows f, ComponentKey key, decimal amount) =>
        f.Flow.Charges.WaiveAsync(new WaiveRequest
        {
            IdempotencyKey = $"waive-{key}-{amount}", Obligation = ObligationRef.ByReference("LN100001"), Component = key, Amount = Money.Kes(amount), Reason = "Goodwill",
        }, Ct);

    private static Task<ObligationCancelled> Cancel(Flows f, params Component[] writeOffs) => Cancel(f, "cancel", writeOffs);

    private static Task<ObligationCancelled> Cancel(Flows f, string key, params Component[] writeOffs) =>
        f.Flow.Obligations.CancelAsync(new CancelObligationRequest
        {
            IdempotencyKey = key, Obligation = ObligationRef.ByReference("LN100001"), Reason = "Device returned", WriteOffs = writeOffs,
        }, Ct);
}
