using InstalmentFlow.Payments.Simulation;

namespace InstalmentFlow.Payments.Tests;

/// <summary>Provider account routing (R-I10) and start-up validation of providers.</summary>
public sealed class RoutingAndSetupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_provider_account_belongs_to_exactly_one_tenant()
    {
        var h = await Harness.CreateAsync();

        var again = await h.Payments.Routing.MapAsync("sim", Harness.Paybill, Harness.Tenant, Ct);
        var error = await Assert.ThrowsAsync<RuleViolationException>(() => h.Payments.Routing.MapAsync("sim", Harness.Paybill, Harness.Other, Ct));

        Assert.Equal(Harness.Tenant, again.Tenant.Value);
        Assert.Equal(PaymentsErrorCodes.ProviderAccountTaken, error.ErrorCode);
        Assert.Equal(Harness.Tenant, (await h.Payments.Routing.ResolveAsync("sim", Harness.Paybill, Ct))?.Value);
        Assert.Single(await h.Payments.Routing.ListAsync(Ct));
    }

    [Fact]
    public async Task Only_existing_tenants_can_be_mapped()
    {
        var h = await Harness.CreateAsync();

        await Assert.ThrowsAnyAsync<InstalmentFlowException>(() => h.Payments.Routing.MapAsync("sim", "999", "nobody", Ct));
        Assert.Null(await h.Payments.Routing.ResolveAsync("sim", "999", Ct));
    }

    [Fact]
    public async Task Events_route_to_the_tenant_that_owns_the_account()
    {
        var h = await Harness.CreateAsync();
        await h.Flows.ForTenant(Harness.Other).Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "a", ExternalId = "B-1" }, Ct);
        await h.Payments.Routing.MapAsync("sim", "800800", Harness.Other, Ct);
        h.PayBill(100m, reference: "B-1", account: "800800");
        h.PayBill(100m);

        await h.DeliverAndProcessAsync();

        Assert.Single(await h.PaymentsAsync());
        var other = h.Flows.ForTenant(Harness.Other).Queries.ListPaymentsAsync(new PaymentQuery { Source = "sim:800800" }, Ct);
        Assert.Single(await other.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_custom_router_can_split_a_shared_account_by_reference()
    {
        var h = await Harness.CreateAsync(b => b.UseTenantRouter(new ByPrefix()));
        h.PayBill(100m, reference: "LN-1");
        h.PayBill(100m, reference: "B-7");

        await h.DeliverAndProcessAsync();

        Assert.Single(await h.PaymentsAsync());
        Assert.Single(await h.Flows.ForTenant(Harness.Other).Queries.ListPaymentsAsync(new PaymentQuery { Source = Harness.Source }, Ct).ToListAsync(Ct));
    }

    [Fact]
    public void A_provider_must_implement_exactly_what_it_declares()
    {
        var flows = InstalmentFlowFactory.CreateInMemory(b => b.AddTenant(Harness.Tenant, Currency.KES, "Africa/Nairobi"));

        var declaresMore = Assert.Throws<InvalidOperationException>(() => InstalmentFlowPayments.CreateInMemory(flows, b => b.AddProvider(new Claims(ProviderCapabilities.StatusQuery))));
        var twice = Assert.Throws<InvalidOperationException>(() => InstalmentFlowPayments.CreateInMemory(flows, b => b.AddProvider(new Claims(ProviderCapabilities.None)).AddProvider(new Claims(ProviderCapabilities.None))));
        var badName = Assert.Throws<InvalidOperationException>(() => InstalmentFlowPayments.CreateInMemory(flows, b => b.AddProvider(new SimulatedProvider("s", provider: "Sim"))));

        Assert.Contains(nameof(ITransactionStatusQuery), declaresMore.Message, StringComparison.Ordinal);
        Assert.Contains("twice", twice.Message, StringComparison.Ordinal);
        Assert.Contains("lower case", badName.Message, StringComparison.Ordinal);
    }

    private sealed class ByPrefix : ITenantRouter
    {
        public ValueTask<TenantId?> RouteAsync(NormalisedPaymentEvent paymentEvent, CancellationToken cancellationToken) =>
            ValueTask.FromResult<TenantId?>(paymentEvent.AccountReference?.StartsWith("B-", StringComparison.Ordinal) == true ? Harness.Other : Harness.Tenant);
    }

    private sealed class Claims(ProviderCapabilities capabilities) : IPaymentProviderAdapter
    {
        public string Provider => "claims";

        public ProviderCapabilities Capabilities => capabilities;

        public ValueTask<CallbackParseResult> ParseCallbackAsync(ProviderCallback callback, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CallbackParseResult.Reject("no"));
    }
}
