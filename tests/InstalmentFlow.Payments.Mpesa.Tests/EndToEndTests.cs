using System.Net;
using System.Text;

namespace InstalmentFlow.Payments.Mpesa.Tests;

/// <summary>The M-Pesa adapter inside the payments package and the engine, against the fake Daraja.</summary>
public sealed class EndToEndTests : IDisposable
{
    private const string Tenant = "acme-ke";
    private readonly DarajaHarness _h = new();
    private InstalmentFlowFactory _flows = null!;
    private InstalmentFlowPayments _payments = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task A_c2b_confirmation_is_stored_once_and_allocated_to_the_loan()
    {
        await SetUpAsync();
        var callback = new ProviderCallback("mpesa", MpesaAdapter.C2BConfirmation, Fixture.Bytes("c2b-confirmation.json"));

        var first = await _payments.Inbox.ReceiveAsync(callback, Ct);
        var again = await _payments.Inbox.ReceiveAsync(callback, Ct);
        await _payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal((InboxReceiptOutcome.Accepted, InboxReceiptOutcome.Duplicate), (first.Outcome, again.Outcome));
        Assert.Equal(Money.Kes(1_500m), await PaidAsync());
    }

    [Fact]
    public async Task An_stk_push_through_an_intent_is_confirmed_by_its_callback()
    {
        await SetUpAsync();
        _h.Daraja.On("/mpesa/stkpush/v1/processrequest", "stk-push-response.json");

        var intent = await _payments.ForTenant(Tenant).Intents.InitiateAsync(new InitiateCollectionRequest
        {
            IdempotencyKey = "push-1", Provider = "mpesa", ProviderAccount = DarajaHarness.ShortCode, Payer = "0708374149", Amount = Money.Kes(400m),
            Target = PaymentTarget.ForObligation(ObligationRef.ByReference("LN-1")),
        }, Ct);
        await _payments.Inbox.ReceiveAsync(new ProviderCallback("mpesa", MpesaAdapter.StkResult, Fixture.Bytes("stk-callback-success.json"), providerAccount: DarajaHarness.ShortCode), Ct);
        await _payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal((PaymentIntentStatus.Submitted, "ws_CO_191220191020363925"), (intent.Status, intent.CorrelationId));
        var confirmed = await _payments.ForTenant(Tenant).Intents.GetAsync(intent.IntentId, Ct);
        Assert.Equal((PaymentIntentStatus.Confirmed, "NLJ7RT61SV"), (confirmed.Status, confirmed.TransactionId));
        Assert.Equal(Money.Kes(400m), await PaidAsync());
    }

    [Fact]
    public async Task A_push_that_times_out_is_ambiguous_and_never_sent_again()
    {
        await SetUpAsync();
        _h.Daraja.On("/mpesa/stkpush/v1/processrequest", """{"errorCode":"500.003.02","errorMessage":"System is busy"}""", HttpStatusCode.GatewayTimeout);
        var request = new InitiateCollectionRequest
        {
            IdempotencyKey = "push-1", Provider = "mpesa", ProviderAccount = DarajaHarness.ShortCode, Payer = "0708374149", Amount = Money.Kes(400m),
            Target = PaymentTarget.ForObligation(ObligationRef.ByReference("LN-1")),
        };

        var first = await _payments.ForTenant(Tenant).Intents.InitiateAsync(request, Ct);
        var retry = await _payments.ForTenant(Tenant).Intents.InitiateAsync(request, Ct);

        Assert.Equal(PaymentIntentStatus.Ambiguous, first.Status);
        Assert.Equal(first.IntentId, retry.IntentId);
        Assert.Single(_h.Daraja.To("/mpesa/stkpush/v1/processrequest"));
    }

    [Fact]
    public async Task A_fractional_confirmation_waits_in_the_inbox_as_failed_and_records_nothing()
    {
        await SetUpAsync();
        var body = Fixture.Text("c2b-confirmation.json").Replace("\"1500.00\"", "\"100.50\"", StringComparison.Ordinal);

        var receipt = await _payments.Inbox.ReceiveAsync(new ProviderCallback("mpesa", MpesaAdapter.C2BConfirmation, Encoding.UTF8.GetBytes(body)), Ct);
        var processed = await _payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal(InboxReceiptOutcome.Accepted, receipt.Outcome);   // M-Pesa is answered: the money is not refused
        Assert.Equal(0, processed.Processed);
        var held = Assert.Single(await _payments.Inbox.ListAsync(InboxStatus.Failed, cancellationToken: Ct));
        Assert.StartsWith("Held:", held.Error, StringComparison.Ordinal);
        Assert.Equal(Money.Kes(0m), await PaidAsync());
    }

    [Fact]
    public async Task Reconciliation_through_pull_transactions_ingests_a_missed_confirmation_once()
    {
        await SetUpAsync();
        _h.Daraja.On("/pulltransactions/v1/query", "pull-query-page.json").On("/pulltransactions/v1/query", "pull-query-empty-sandbox.json");
        await _payments.Inbox.ReceiveAsync(new ProviderCallback("mpesa", MpesaAdapter.C2BConfirmation, Fixture.Bytes("c2b-confirmation.json")), Ct);
        await _payments.Inbox.ProcessPendingAsync(Ct);
        var day = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.FromHours(3));

        var report = await _payments.Reconciliation.RunAsync(new ReconciliationRequest { Provider = "mpesa", ProviderAccount = DarajaHarness.ShortCode, From = day, To = day.AddDays(1) }, Ct);
        await _payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal(
            [ReconciliationClassification.Matched, ReconciliationClassification.MissingInEngine, ReconciliationClassification.ReversedAtProvider],
            report.Lines.Select(l => l.Classification).ToArray());
        Assert.Equal(Money.Kes(2_200m), await PaidAsync());   // 1500 + the missed 700; its reversal waits for a person
        Assert.Single(await _payments.ForTenant(Tenant).Discrepancies.ListOpenAsync(Ct));
    }

    [Fact]
    public async Task An_org_portal_statement_reconciles_its_completed_rows()
    {
        await SetUpAsync();
        await _payments.Inbox.ReceiveAsync(new ProviderCallback("mpesa", MpesaAdapter.C2BConfirmation, Fixture.Bytes("c2b-confirmation.json")), Ct);
        await _payments.Inbox.ProcessPendingAsync(Ct);
        var statement = await CsvStatementSource.ReadAsync("mpesa", DarajaHarness.ShortCode, new StringReader(Fixture.Text("org-portal-statement.csv")), MpesaStatements.OrgPortal(), Ct);
        var day = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.FromHours(3));

        var report = await _payments.Reconciliation.RunAsync(new ReconciliationRequest
        {
            Provider = "mpesa", ProviderAccount = DarajaHarness.ShortCode, From = day, To = day.AddDays(1), Statement = statement,
        }, Ct);

        Assert.Equal(2, statement.Lines.Count);   // the failed row moved no money
        Assert.Equal((StatementLineKind.Reversal, "RKTQDM7W6S"), (statement.Lines[1].Kind, statement.Lines[1].ReversedTransactionId));
        Assert.Equal([ReconciliationClassification.Matched, ReconciliationClassification.ReversedAtProvider], report.Lines.Select(l => l.Classification).ToArray());
    }

    private async Task SetUpAsync()
    {
        _flows = InstalmentFlowFactory.CreateInMemory(b => b.UseTimeProvider(_h.Clock).AddTenant(Tenant, Currency.KES, "Africa/Nairobi"));
        _payments = InstalmentFlowPayments.CreateInMemory(_flows, p => p.AddProvider(_h.Adapter).UseTimeProvider(_h.Clock));
        await _payments.Routing.MapAsync("mpesa", DarajaHarness.ShortCode, Tenant, Ct);
        var flow = _flows.ForTenant(Tenant);
        var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct", ExternalId = "C-1" }, Ct);
        await flow.Obligations.OpenAsync(new OpenObligationRequest
        {
            IdempotencyKey = "loan", Account = customer.AccountId, ExternalId = "L-1", PaymentReference = "LN-1",
            Components = [Component.Principal(Money.Kes(5_000m))], Plan = PlanSpec.Equal(Frequency.Monthly, 2, new DateOnly(2026, 3, 1)),
        }, Ct);
    }

    private async Task<Money> PaidAsync() =>
        Money.Kes(5_000m).Subtract((await _flows.ForTenant(Tenant).Queries.GetObligationAsync(ObligationRef.ByReference("LN-1"), cancellationToken: Ct)).Outstanding);
}
