using InstalmentFlow.Domain;

namespace InstalmentFlow.Tests;

public sealed class TenantAndObligationTests
{
    private static readonly DateOnly s_today = new(2026, 11, 18);
    private static readonly DateOnly s_dec1 = new(2026, 12, 1);

    private readonly FixedTimeProvider _time = FixedTimeProvider.AtNairobiDate(s_today);
    private readonly InstalmentFlowFactory _factory;

    public TenantAndObligationTests()
    {
        _factory = InstalmentFlowFactory.CreateInMemory(b => b
            .UseTimeProvider(_time)
            .AddTenant("acme-ke", Currency.KES, "Africa/Nairobi", p => p with
            {
                PaymentReference = new ReferenceFormat(@"^LN\d{6}$"),
                DefaultGrace = GracePeriod.OfDays(2),
            })
            .AddTenant("other-ke", Currency.KES, "Africa/Nairobi", p => p with { PaymentReference = new ReferenceFormat(@"^LN\d{6}$") }));
    }

    private IInstalmentFlow Acme => _factory.ForTenant("acme-ke");

    private IInstalmentFlow Other => _factory.ForTenant("other-ke");

    [Fact]
    public async Task Opens_an_account_and_a_phone_financing_obligation()
    {
        var account = await OpenAccount(Acme, "C-881");

        var opened = await Acme.Obligations.OpenAsync(PhoneFinancing(account, "loan-2041", " ln 100 001 "), TestContext.Current.CancellationToken);

        var view = opened.Obligation;
        Assert.Equal("LN100001", view.PaymentReference);
        Assert.Equal(ObligationStatus.Active, view.Status);
        Assert.Equal(Money.Kes(15_000m), view.Outstanding);
        Assert.Equal(271, view.Instalments.Count);
        Assert.Equal(2, view.Grace!.Days);              // tenant default applied
        Assert.Equal(1, view.ActivePlanVersion);
        Assert.Equal(s_today, view.AsOfDate);
        Assert.Equal(InstalmentTiming.Due, view.Instalments[0].Timing);  // the deposit is due today
        Assert.Null(view.PaidThroughDate);

        var summary = await Acme.Queries.GetAccountAsync(AccountRef.ByExternalId("C-881"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("LN100001", Assert.Single(summary.Obligations).PaymentReference);
    }

    [Fact]
    public async Task Arrears_and_days_past_due_with_no_payments()
    {
        var account = await OpenAccount(Acme, "C-1");
        await Acme.Obligations.OpenAsync(PhoneFinancing(account, "loan-1", "LN000001"), TestContext.Current.CancellationToken);

        var view = await Acme.Queries.GetObligationAsync(ObligationRef.ByReference("LN000001"), new DateOnly(2026, 12, 4), TestContext.Current.CancellationToken);

        // Deposit (18 Nov) and 1-3 Dec are past due; grace is 2 days.
        Assert.Equal(Money.Kes(1_500m + 150m), view.Arrears);
        Assert.Equal(16, view.DaysPastDue);
        Assert.Equal(
            [InstalmentTiming.Overdue, InstalmentTiming.Overdue, InstalmentTiming.InGrace, InstalmentTiming.InGrace, InstalmentTiming.Due, InstalmentTiming.Upcoming],
            view.Instalments.Take(6).Select(i => i.Timing));
    }

    [Fact]
    public async Task Payment_reference_must_match_the_tenant_format()
    {
        var account = await OpenAccount(Acme, "C-1");

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() =>
            Acme.Obligations.OpenAsync(PhoneFinancing(account, "loan-1", "LOAN-1"), TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.InvalidPaymentReference, ex.ErrorCode);
    }

    [Fact]
    public async Task Payment_reference_is_unique_within_a_tenant_but_not_across_tenants()
    {
        var a1 = await OpenAccount(Acme, "C-1");
        var a2 = await OpenAccount(Acme, "C-2");
        var b1 = await OpenAccount(Other, "C-1");   // same external ID in another tenant is fine
        await Acme.Obligations.OpenAsync(PhoneFinancing(a1, "loan-1", "LN100001"), TestContext.Current.CancellationToken);

        var duplicate = await Assert.ThrowsAsync<RuleViolationException>(() =>
            Acme.Obligations.OpenAsync(PhoneFinancing(a2, "loan-2", "ln100001"), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.DuplicatePaymentReference, duplicate.ErrorCode);

        var otherTenant = await Other.Obligations.OpenAsync(PhoneFinancing(b1, "loan-1", "LN100001"), TestContext.Current.CancellationToken);
        Assert.Equal("LN100001", otherTenant.Obligation.PaymentReference);
    }

    [Fact]
    public async Task External_ids_are_unique_within_a_tenant()
    {
        var account = await OpenAccount(Acme, "C-1");
        await Acme.Obligations.OpenAsync(PhoneFinancing(account, "loan-1", "LN100001"), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorCodes.DuplicateExternalId, (await Assert.ThrowsAsync<RuleViolationException>(() => OpenAccount(Acme, "C-1"))).ErrorCode);
        Assert.Equal(ErrorCodes.DuplicateExternalId, (await Assert.ThrowsAsync<RuleViolationException>(() =>
            Acme.Obligations.OpenAsync(PhoneFinancing(account, "loan-1", "LN100002"), TestContext.Current.CancellationToken))).ErrorCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_use_an_obligation_I18()
    {
        var account = await OpenAccount(Acme, "C-1");
        var opened = await Acme.Obligations.OpenAsync(PhoneFinancing(account, "loan-1", "LN100001"), TestContext.Current.CancellationToken);
        var obligationId = opened.Obligation.ObligationId;
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<NotFoundException>(() => Other.Queries.GetObligationAsync(obligationId, cancellationToken: ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Other.Queries.GetObligationAsync(ObligationRef.ByReference("LN100001"), cancellationToken: ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Other.Queries.GetObligationAsync(ObligationRef.ByExternalId("loan-1"), cancellationToken: ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Other.Queries.GetAccountAsync(account, cancellationToken: ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Other.Obligations.OpenAsync(PhoneFinancing(account, "loan-x", "LN999999"), ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Other.Plans.SupersedeAsync(Supersede(obligationId), ct));
    }

    [Fact]
    public async Task Unknown_tenant_fails_fast_on_use()
    {
        var unknown = _factory.ForTenant("nobody");

        var ex = await Assert.ThrowsAsync<UnknownTenantException>(() => OpenAccount(unknown, "C-1"));

        Assert.Equal(ErrorCodes.UnknownTenant, ex.ErrorCode);
    }

    [Fact]
    public async Task Superseding_a_plan_keeps_every_version()
    {
        var account = await OpenAccount(Acme, "C-1");
        var opened = await Acme.Obligations.OpenAsync(PhoneFinancing(account, "loan-1", "LN100001"), TestContext.Current.CancellationToken);

        var result = await Acme.Plans.SupersedeAsync(Supersede(opened.Obligation.ObligationId), TestContext.Current.CancellationToken);
        var schedule = await Acme.Queries.GetScheduleAsync(opened.Obligation.ObligationId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((1, 2), (result.PreviousVersion, result.ActiveVersion));
        Assert.Equal(2, schedule.ActiveVersion);
        Assert.Equal([PlanVersionStatus.Superseded, PlanVersionStatus.Active], schedule.Versions.Select(v => v.Status));
        Assert.Equal("Set-up error: weekly not daily", schedule.Versions[0].EndReason);
        Assert.Equal(271, schedule.Versions[0].Instalments.Count);
        Assert.Equal(28, schedule.Versions[1].Instalments.Count);
        Assert.Equal(Money.Kes(15_000m), Money.Sum(Currency.KES, schedule.Versions[1].Instalments.Select(i => i.Due)));
    }

    [Fact]
    public void A_plan_with_financial_history_cannot_be_superseded_IF4001()
    {
        var history = InstalmentEvaluatorTests.Instalment(500m, allocated: 50m);
        var plan = new PlanVersion(1, PlanVersionStatus.Active, GracePeriod.None, "equal", [history], DateTimeOffset.UnixEpoch, null, null);
        var obligation = new Obligation(
            new ObligationId(Guid.CreateVersion7()), new AccountId(Guid.CreateVersion7()), "loan-1", "LN100001", Currency.KES,
            ObligationStatus.Active, [Component.Principal(Money.Kes(500m))], [plan], null, DateTimeOffset.UnixEpoch);
        var replacement = plan with { Version = 2 };

        var ex = Assert.Throws<RuleViolationException>(() => obligation.SupersedePlan(replacement, DateTimeOffset.UnixEpoch, "change"));

        Assert.Equal(ErrorCodes.PlanHasFinancialHistory, ex.ErrorCode);
    }

    [Fact]
    public async Task Policy_changes_are_versioned_and_apply_going_forward()
    {
        var account = await OpenAccount(Acme, "C-1");
        var before = await Acme.Obligations.OpenAsync(PhoneFinancing(account, "loan-1", "LN100001"), TestContext.Current.CancellationToken);

        var tenant = await _factory.Tenants.UpdatePolicyAsync("acme-ke", p => p with { DefaultGrace = GracePeriod.OfDays(5) }, "ops@acme", TestContext.Current.CancellationToken);
        var after = await Acme.Obligations.OpenAsync(PhoneFinancing(account, "loan-2", "LN100002"), TestContext.Current.CancellationToken);
        var beforeNow = await Acme.Queries.GetObligationAsync(before.Obligation.ObligationId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], tenant.History.Select(v => v.Version));
        Assert.Equal("ops@acme", tenant.Policy.CreatedBy);
        Assert.Equal(2, beforeNow.Grace!.Days);
        Assert.Equal(5, after.Obligation.Grace!.Days);
    }

    [Fact]
    public async Task Tenants_can_be_created_at_runtime_but_not_twice()
    {
        var created = await _factory.Tenants.CreateAsync(new CreateTenantRequest { TenantId = "new-ug", BaseCurrency = Currency.UGX, TimeZone = "Africa/Kampala" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, created.Policy.Version);
        Assert.Equal(ErrorCodes.DuplicateTenant, (await Assert.ThrowsAsync<RuleViolationException>(() =>
            _factory.Tenants.CreateAsync(new CreateTenantRequest { TenantId = "new-ug", BaseCurrency = Currency.UGX, TimeZone = "Africa/Kampala" }, TestContext.Current.CancellationToken))).ErrorCode);
        Assert.Equal(ErrorCodes.InvalidRequest, (await Assert.ThrowsAsync<RuleViolationException>(() =>
            _factory.Tenants.CreateAsync(new CreateTenantRequest { TenantId = "bad-tz", BaseCurrency = Currency.KES, TimeZone = "Mars/Base" }, TestContext.Current.CancellationToken))).ErrorCode);
    }

    [Fact]
    public async Task Concurrent_obligations_with_the_same_reference_only_one_wins()
    {
        var accounts = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => OpenAccount(Acme, $"C-{i}")));

        var attempts = await Task.WhenAll(accounts.Select((a, i) => Task.Run(async () =>
        {
            try
            {
                await Acme.Obligations.OpenAsync(PhoneFinancing(a, $"loan-{i}", "LN555555"), TestContext.Current.CancellationToken);
                return true;
            }
            catch (RuleViolationException ex) when (ex.ErrorCode == ErrorCodes.DuplicatePaymentReference)
            {
                return false;
            }
        })));

        Assert.Equal(1, attempts.Count(won => won));
    }

    [Fact]
    public async Task Requests_are_validated()
    {
        var account = await OpenAccount(Acme, "C-1");
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Acme.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = " ", ExternalId = "C-2" }, ct));
        await Assert.ThrowsAsync<RuleViolationException>(() => Acme.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "k", ExternalId = " C-2" }, ct));
        await Assert.ThrowsAsync<RuleViolationException>(() => Acme.Obligations.OpenAsync(PhoneFinancing(account, "loan-1", "LN100001") with { Components = [] }, ct));
        await Assert.ThrowsAsync<CurrencyMismatchException>(() => Acme.Obligations.OpenAsync(PhoneFinancing(account, "loan-1", "LN100001") with
        {
            Components = [Component.Principal(Money.Kes(100m)), Component.Fee(Money.Of(1m, "USD"))],
        }, ct));
        await Assert.ThrowsAsync<RuleViolationException>(() => Acme.Plans.SupersedeAsync(Supersede(ObligationRef.ByExternalId("missing")) with { Reason = "" }, ct));
    }

    private static async Task<AccountId> OpenAccount(IInstalmentFlow flow, string externalId) =>
        (await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = $"acct-{externalId}", ExternalId = externalId }, TestContext.Current.CancellationToken)).AccountId;

    private static OpenObligationRequest PhoneFinancing(AccountId account, string externalId, string reference) => new()
    {
        IdempotencyKey = $"open-{externalId}",
        Account = account,
        ExternalId = externalId,
        PaymentReference = reference,
        Components = [Component.Principal(Money.Kes(15_000m))],
        Plan = PlanSpec.Equal(Frequency.Daily, 270, s_dec1, deposit: new Deposit(Money.Kes(1_500m), s_today)),
        Description = "Phone financing",
    };

    private static SupersedePlanRequest Supersede(ObligationRef obligation) => new()
    {
        IdempotencyKey = $"supersede-{obligation}",
        Obligation = obligation,
        Plan = PlanSpec.Equal(Frequency.Weekly, 28, s_dec1),
        Reason = "Set-up error: weekly not daily",
    };
}
