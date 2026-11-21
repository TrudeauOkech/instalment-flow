namespace InstalmentFlow.Payments.AirtelMoney.Tests;

/// <summary>
/// Airtel collections through intents, the inbox and the engine. Airtel's callbacks are prompts: nothing is recorded or
/// failed until the enquiry agrees, and the amount recorded is the amount requested.
/// </summary>
public sealed class AirtelEndToEndTests : IDisposable
{
    private const string Tenant = "acme-ke";
    private readonly AirtelHarness _h = new();
    private InstalmentFlowFactory _flows = null!;
    private InstalmentFlowPayments _payments = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task A_confirmed_callback_records_the_requested_amount_against_the_loan()
    {
        await SetUpAsync();
        _h.Api.On(AirtelHarness.Collection, Fixture.Text("push-accepted.json"));
        var intent = await PushAsync();
        _h.Api.On(AirtelHarness.Enquiry, Fixture.Text("enquiry-ts.json", Ref(intent)));

        await ReceiveAndProcessAsync("callback-ts.json", intent);

        var confirmed = await _payments.ForTenant(Tenant).Intents.GetAsync(intent.IntentId, Ct);
        Assert.Equal((PaymentIntentStatus.Confirmed, "MP260302.0915.A12345"), (confirmed.Status, confirmed.TransactionId));
        Assert.Equal(Money.Kes(400m), await PaidAsync());
        Assert.Single(_h.Api.To(AirtelHarness.Enquiry));   // the callback was confirmed before anything was recorded
    }

    [Fact]
    public async Task A_forged_success_callback_records_nothing_when_airtel_says_it_failed()
    {
        await SetUpAsync();
        _h.Api.On(AirtelHarness.Collection, Fixture.Text("push-accepted.json"));
        var intent = await PushAsync();
        _h.Api.On(AirtelHarness.Enquiry, Fixture.Text("enquiry-tf.json", Ref(intent)));

        await ReceiveAndProcessAsync("callback-ts.json", intent);

        Assert.Equal(PaymentIntentStatus.Failed, (await _payments.ForTenant(Tenant).Intents.GetAsync(intent.IntentId, Ct)).Status);
        Assert.Equal(Money.Kes(0m), await PaidAsync());
        Assert.Single(await _payments.Inbox.ListAsync(InboxStatus.Processed, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_success_callback_waits_while_airtel_still_says_in_progress()
    {
        await SetUpAsync(p => p.Inbox.RetryDelay = TimeSpan.FromMinutes(1));
        _h.Api.On(AirtelHarness.Collection, Fixture.Text("push-accepted.json"));
        var intent = await PushAsync();
        _h.Api.On(AirtelHarness.Enquiry, Fixture.Text("enquiry-tip.json", Ref(intent))).On(AirtelHarness.Enquiry, Fixture.Text("enquiry-ts.json", Ref(intent)));

        await ReceiveAndProcessAsync("callback-ts.json", intent);
        Assert.Equal(Money.Kes(0m), await PaidAsync());
        _h.Clock.Now += TimeSpan.FromMinutes(1);
        await _payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal(Money.Kes(400m), await PaidAsync());
    }

    [Fact]
    public async Task A_forged_failure_callback_does_not_fail_an_intent_airtel_is_still_processing()
    {
        await SetUpAsync();
        _h.Api.On(AirtelHarness.Collection, Fixture.Text("push-accepted.json"));
        var intent = await PushAsync();
        _h.Api.On(AirtelHarness.Enquiry, Fixture.Text("enquiry-tip.json", Ref(intent)));

        await ReceiveAndProcessAsync("callback-tf.json", intent);

        Assert.Equal(PaymentIntentStatus.Submitted, (await _payments.ForTenant(Tenant).Intents.GetAsync(intent.IntentId, Ct)).Status);
    }

    [Fact]
    public async Task A_push_airtel_did_not_confirm_is_ambiguous_until_the_enquiry_settles_it()
    {
        await SetUpAsync(p => p.Intents.FirstCheck = TimeSpan.FromMinutes(1));
        _h.Api.On(AirtelHarness.Collection, Fixture.Text("push-not-successful.json"));
        var intent = await PushAsync();
        Assert.Equal(PaymentIntentStatus.Ambiguous, intent.Status);
        Assert.Equal(intent.IntentId, (await PushAsync()).IntentId);   // never pushed twice
        Assert.Single(_h.Api.To(AirtelHarness.Collection));

        _h.Api.On(AirtelHarness.Enquiry, Fixture.Text("enquiry-ta.json", Ref(intent))).On(AirtelHarness.Enquiry, Fixture.Text("enquiry-ts.json", Ref(intent)));
        _h.Clock.Now += TimeSpan.FromMinutes(1);
        await _payments.IntentResolver.ResolveDueAsync(Ct);   // TA: still ambiguous
        Assert.Equal(PaymentIntentStatus.Ambiguous, (await _payments.ForTenant(Tenant).Intents.GetAsync(intent.IntentId, Ct)).Status);
        _h.Clock.Now += TimeSpan.FromMinutes(5);
        await _payments.IntentResolver.ResolveDueAsync(Ct);   // TS: paid
        await _payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal(PaymentIntentStatus.Confirmed, (await _payments.ForTenant(Tenant).Intents.GetAsync(intent.IntentId, Ct)).Status);
        Assert.Equal(Money.Kes(400m), await PaidAsync());
    }

    [Fact]
    public async Task A_push_whose_reply_was_lost_is_ambiguous_and_never_sent_again()
    {
        await SetUpAsync();
        _h.Api.On(AirtelHarness.Collection, Fixture.Text("push-accepted.json"));
        _h.Api.Fail = r => r.RequestUri!.AbsolutePath == AirtelHarness.Collection ? new HttpRequestException("connection reset") : null;

        var first = await PushAsync();
        var retry = await PushAsync();

        Assert.Equal(PaymentIntentStatus.Ambiguous, first.Status);
        Assert.Equal(first.IntentId, retry.IntentId);
        Assert.Single(_h.Api.To(AirtelHarness.Collection));
    }

    [Fact]
    public async Task A_duplicated_callback_is_confirmed_and_recorded_once()
    {
        await SetUpAsync();
        _h.Api.On(AirtelHarness.Collection, Fixture.Text("push-accepted.json"));
        var intent = await PushAsync();
        _h.Api.On(AirtelHarness.Enquiry, Fixture.Text("enquiry-ts.json", Ref(intent)));

        await _payments.Inbox.ReceiveAsync(AirtelHarness.Callback("callback-ts.json", Ref(intent)), Ct);
        await ReceiveAndProcessAsync("callback-ts.json", intent);

        Assert.Equal(Money.Kes(400m), await PaidAsync());
    }

    private static string Ref(PaymentIntentView intent) => intent.IntentId.ToString("N");

    private Task<PaymentIntentView> PushAsync() => _payments.ForTenant(Tenant).Intents.InitiateAsync(new InitiateCollectionRequest
    {
        IdempotencyKey = "push-1", Provider = "airtel", ProviderAccount = AirtelHarness.Account, Payer = "0733123456", Amount = Money.Kes(400m),
        Target = PaymentTarget.ForObligation(ObligationRef.ByReference("LN-1")),
    }, Ct);

    private async Task ReceiveAndProcessAsync(string fixture, PaymentIntentView intent)
    {
        var receipt = await _payments.Inbox.ReceiveAsync(AirtelHarness.Callback(fixture, Ref(intent)), Ct);
        Assert.NotEqual(InboxReceiptOutcome.Rejected, receipt.Outcome);
        await _payments.Inbox.ProcessPendingAsync(Ct);
    }

    private async Task SetUpAsync(Action<PaymentsBuilder>? configure = null)
    {
        _flows = InstalmentFlowFactory.CreateInMemory(b => b.UseTimeProvider(_h.Clock).AddTenant(Tenant, Currency.KES, "Africa/Nairobi"));
        _payments = InstalmentFlowPayments.CreateInMemory(_flows, p =>
        {
            p.AddProvider(_h.Adapter).UseTimeProvider(_h.Clock);
            configure?.Invoke(p);
        });
        await _payments.Routing.MapAsync("airtel", AirtelHarness.Account, Tenant, Ct);
        var flow = _flows.ForTenant(Tenant);
        var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct", ExternalId = "C-1" }, Ct);
        await flow.Obligations.OpenAsync(new OpenObligationRequest
        {
            IdempotencyKey = "loan", Account = customer.AccountId, ExternalId = "L-1", PaymentReference = "LN-1",
            Components = [Component.Principal(Money.Kes(1_000m))], Plan = PlanSpec.Equal(Frequency.Monthly, 2, new DateOnly(2026, 3, 1)),
        }, Ct);
    }

    private async Task<Money> PaidAsync() =>
        Money.Kes(1_000m).Subtract((await _flows.ForTenant(Tenant).Queries.GetObligationAsync(ObligationRef.ByReference("LN-1"), cancellationToken: Ct)).Outstanding);
}
