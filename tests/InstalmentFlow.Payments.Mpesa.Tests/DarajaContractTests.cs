using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace InstalmentFlow.Payments.Mpesa.Tests;

/// <summary>What the adapter sends to Daraja and how it reads each documented answer.</summary>
public sealed class DarajaContractTests
{
    private const string Stk = "/mpesa/stkpush/v1/processrequest";
    private const string StkQuery = "/mpesa/stkpushquery/v1/query";
    private const string Pull = "/pulltransactions/v1/query";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_stk_push_sends_the_documented_request_and_returns_the_checkout_id()
    {
        using var h = new DarajaHarness();
        h.Daraja.On(Stk, "stk-push-response.json");

        var result = await h.Adapter.InitiateAsync(DarajaHarness.Collection(), Ct);

        Assert.Equal((InitiationOutcome.Accepted, "ws_CO_191220191020363925"), (result.Outcome, result.CorrelationId));
        var sent = Assert.Single(h.Daraja.To(Stk));
        Assert.Equal("Bearer token-1", sent.Authorization);
        string Field(string name) => sent.Body.GetProperty(name).ToString();
        Assert.Equal("20260302090000", Field("Timestamp"));   // 06:00 UTC is 09:00 in Nairobi
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("600123" + TestCredentials.Passkey + "20260302090000")), Field("Password"));
        Assert.Equal(("CustomerPayBillOnline", "400", "254708374149", "254708374149", "600123"),
            (Field("TransactionType"), Field("Amount"), Field("PartyA"), Field("PhoneNumber"), Field("PartyB")));
        Assert.Equal(("LN-1", "https://pay.example.test/hooks/s3cret/stk/600123", "Loan repaymen"), (Field("AccountReference"), Field("CallBackURL"), Field("TransactionDesc")));
    }

    [Fact]
    public async Task A_till_pushes_buy_goods_to_its_own_party()
    {
        using var h = new DarajaHarness(o => o.Accounts[0] = o.Accounts[0] with { CollectionType = MpesaCollectionType.BuyGoods, PartyB = "174379" });
        h.Daraja.On(Stk, "stk-push-response.json");

        await h.Adapter.InitiateAsync(DarajaHarness.Collection(), Ct);

        var sent = Assert.Single(h.Daraja.To(Stk)).Body;
        Assert.Equal(("CustomerBuyGoodsOnline", "174379"), (sent.GetProperty("TransactionType").GetString(), sent.GetProperty("PartyB").GetString()));
    }

    [Theory]
    [InlineData(400.50, "0708374149", "LN-1", "whole Kenyan shillings")]
    [InlineData(400, "0708374149", "LN-1234567890X", "longer than 12")]
    [InlineData(400, "12345", "LN-1", "not a Kenyan mobile number")]
    public async Task A_push_daraja_would_refuse_or_get_wrong_is_refused_before_anything_is_sent(decimal amount, string payer, string reference, string reason)
    {
        using var h = new DarajaHarness();

        var result = await h.Adapter.InitiateAsync(DarajaHarness.Collection(amount, payer, reference), Ct);

        Assert.Equal(InitiationOutcome.Rejected, result.Outcome);
        Assert.Contains(reason, result.Reason, StringComparison.Ordinal);
        Assert.Empty(h.Daraja.Requests);
    }

    [Fact]
    public async Task A_refused_push_fails_and_a_server_error_is_unknown()
    {
        using var h = new DarajaHarness();
        h.Daraja.On(Stk, "stk-push-error.json", HttpStatusCode.BadRequest).On(Stk, """{"errorCode":"500.003.02","errorMessage":"System is busy"}""", HttpStatusCode.ServiceUnavailable);

        var refused = await h.Adapter.InitiateAsync(DarajaHarness.Collection(), Ct);
        var unknown = await h.Adapter.InitiateAsync(DarajaHarness.Collection(), Ct);

        Assert.Equal(InitiationOutcome.Rejected, refused.Outcome);
        Assert.Contains("400.002.02", refused.Reason, StringComparison.Ordinal);
        Assert.Equal(InitiationOutcome.Unknown, unknown.Outcome);   // the intent becomes ambiguous, never failed
    }

    [Fact]
    public async Task A_token_is_cached_until_shortly_before_it_expires()
    {
        using var h = new DarajaHarness();
        h.Daraja.On(Stk, "stk-push-response.json");

        await h.Adapter.InitiateAsync(DarajaHarness.Collection(), Ct);
        await h.Adapter.InitiateAsync(DarajaHarness.Collection(), Ct);
        h.Clock.Now += TimeSpan.FromMinutes(59);   // within a minute of the 3599 s lifetime: refreshed early
        await h.Adapter.InitiateAsync(DarajaHarness.Collection(), Ct);

        Assert.Equal(2, h.Daraja.TokensIssued);
        var oauth = h.Daraja.To("/oauth/v1/generate");
        Assert.All(oauth, r => Assert.Equal($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TestCredentials.ConsumerKey}:{TestCredentials.ConsumerSecret}"))}", r.Authorization));
        Assert.Equal(["Bearer token-1", "Bearer token-1", "Bearer token-2"], h.Daraja.To(Stk).Select(r => r.Authorization));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_token_that_expires_mid_flight_is_refreshed_once_and_the_request_retried(HttpStatusCode refusal)
    {
        using var h = new DarajaHarness();
        h.Daraja.On(Stk, "token-expired.json", refusal).On(Stk, "stk-push-response.json");

        var result = await h.Adapter.InitiateAsync(DarajaHarness.Collection(), Ct);

        Assert.Equal(InitiationOutcome.Accepted, result.Outcome);
        Assert.Equal(["Bearer token-1", "Bearer token-2"], h.Daraja.To(Stk).Select(r => r.Authorization));
    }

    [Fact]
    public async Task A_token_refused_twice_is_not_retried_forever()
    {
        using var h = new DarajaHarness();
        h.Daraja.On(Stk, "token-expired.json", HttpStatusCode.Unauthorized);

        var result = await h.Adapter.InitiateAsync(DarajaHarness.Collection(), Ct);

        Assert.Equal(InitiationOutcome.Rejected, result.Outcome);
        Assert.Equal(2, h.Daraja.To(Stk).Count);
    }

    [Fact]
    public async Task The_stk_query_reads_paid_as_pending_until_a_receipt_arrives_and_cancelled_as_failed()
    {
        using var h = new DarajaHarness();
        h.Daraja.On(StkQuery, "stk-query-success.json").On(StkQuery, "stk-query-processing.json", HttpStatusCode.InternalServerError).On(StkQuery, "stk-query-cancelled.json");
        var reference = new ProviderTransactionRef(DarajaHarness.ShortCode, "ws_CO_191220191020363925", "intent");

        var paid = await h.Adapter.QueryAsync(reference, Ct);
        var processing = await h.Adapter.QueryAsync(reference, Ct);
        var cancelled = await h.Adapter.QueryAsync(reference, Ct);

        Assert.Equal(ProviderTransactionStatus.Pending, paid.Status);
        Assert.Equal(ProviderTransactionStatus.Pending, processing.Status);
        Assert.Equal(ProviderTransactionStatus.Failed, cancelled.Status);
        Assert.StartsWith("1032", cancelled.Reason, StringComparison.Ordinal);
        Assert.Equal("ws_CO_191220191020363925", h.Daraja.To(StkQuery)[0].Body.GetProperty("CheckoutRequestID").GetString());
    }

    [Fact]
    public async Task A_push_without_a_checkout_id_cannot_be_queried()
    {
        using var h = new DarajaHarness();

        var result = await h.Adapter.QueryAsync(new ProviderTransactionRef(DarajaHarness.ShortCode, null, "intent"), Ct);

        Assert.Equal(ProviderTransactionStatus.NotFound, result.Status);
        Assert.Empty(h.Daraja.Requests);
    }

    [Fact]
    public async Task Pull_transactions_are_read_page_by_page_as_statement_lines()
    {
        using var h = new DarajaHarness();
        h.Daraja.On(Pull, "pull-query-page.json").On(Pull, "pull-query-empty-sandbox.json", HttpStatusCode.InternalServerError);
        var from = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.FromHours(3));

        var lines = await h.Adapter.ReadAsync(DarajaHarness.ShortCode, from, from.AddDays(1), Ct).ToListAsync(Ct);

        Assert.Equal(["RKTQDM7W6S", "RKTQDM7W6V", "RKTQDM7W6W"], lines.Select(l => l.TransactionId));   // the unlinked reversal is skipped
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 6, 15, 0, TimeSpan.Zero), lines[0].ValueAt);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 7, 5, 0, TimeSpan.Zero), lines[1].ValueAt);   // no offset: Nairobi time
        Assert.Equal((StatementLineKind.Reversal, Money.Kes(700m), "RKTQDM7W6V"), (lines[2].Kind, lines[2].Amount, lines[2].ReversedTransactionId));
        Assert.Equal(("1500.0", "LN-1"), (lines[0].Amount.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), lines[0].AccountReference));
        var queries = h.Daraja.To(Pull);
        Assert.Equal(["0", "4"], queries.Select(q => q.Body.GetProperty("OffSetValue").GetString()));
        Assert.Equal(("2026-03-02 00:00:00", "2026-03-03 00:00:00"), (queries[0].Body.GetProperty("StartDate").GetString(), queries[0].Body.GetProperty("EndDate").GetString()));
    }

    [Fact]
    public async Task Registration_reads_the_sandbox_spelling_of_its_keys()
    {
        using var h = new DarajaHarness();
        h.Daraja.On("/pulltransactions/v1/register", "pull-register-sandbox.json")
            .On("/mpesa/c2b/v2/registerurl", """{"OriginatorCoversationID":"6e86-45dd-91ac-fd5d4178ab523408729","ResponseCode":"0","ResponseDescription":"Success"}""");

        var pull = await h.Adapter.RegisterPullAsync(DarajaHarness.ShortCode, "254708374149", new Uri("https://pay.example.test/hooks/s3cret/pull"), Ct);
        var c2b = await h.Adapter.RegisterC2BUrlsAsync(DarajaHarness.ShortCode, new Uri("https://pay.example.test/hooks/s3cret/c2b/confirm"), new Uri("https://pay.example.test/hooks/s3cret/c2b/validate"), cancellationToken: Ct);

        Assert.Equal("Shortcode already Registered!", pull);
        Assert.Equal("Success", c2b);
        Assert.Equal("Completed", h.Daraja.To("/mpesa/c2b/v2/registerurl")[0].Body.GetProperty("ResponseType").GetString());
    }

    [Fact]
    public async Task A_transaction_status_request_carries_the_encrypted_initiator_password()
    {
        using var h = new DarajaHarness();
        h.Daraja.On("/mpesa/transactionstatus/v1/query", """{"OriginatorConversationID":"10571-7910404-1","ConversationID":"AG_20260302_000049d8f7f3f9d6b7c2","ResponseCode":"0","ResponseDescription":"Accept the service request successfully."}""");

        var conversation = await h.Adapter.RequestTransactionStatusAsync(DarajaHarness.ShortCode, "RKTQDM7W6S", Ct);

        Assert.Equal("AG_20260302_000049d8f7f3f9d6b7c2", conversation);
        var sent = Assert.Single(h.Daraja.To("/mpesa/transactionstatus/v1/query")).Body;
        using var key = h.Credentials.Certificate.GetRSAPrivateKey()!;
        var decrypted = Encoding.UTF8.GetString(key.Decrypt(Convert.FromBase64String(sent.GetProperty("SecurityCredential").GetString()!), RSAEncryptionPadding.Pkcs1));
        Assert.Equal(TestCredentials.InitiatorPassword, decrypted);
        Assert.Equal(("TransactionStatusQuery", "RKTQDM7W6S", "https://pay.example.test/hooks/s3cret/status/600123"),
            (sent.GetProperty("CommandID").GetString(), sent.GetProperty("TransactionID").GetString(), sent.GetProperty("ResultURL").GetString()));
    }

    [Fact]
    public async Task Credentials_never_show_their_values()
    {
        using var h = new DarajaHarness();
        var credentials = await h.Credentials.GetAsync("mpesa", DarajaHarness.ShortCode, Ct);

        Assert.DoesNotContain(TestCredentials.ConsumerSecret, credentials.ToString(), StringComparison.Ordinal);
        Assert.Contains(MpesaCredentialNames.ConsumerSecret, credentials.ToString(), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new ProviderCredentials(new Dictionary<string, string>()).Require(MpesaCredentialNames.Passkey));
    }

    [Fact]
    public async Task Simulating_c2b_is_refused_outside_the_sandbox()
    {
        using var h = new DarajaHarness(o => o.Environment = MpesaEnvironment.Production);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Adapter.SimulateC2BAsync(DarajaHarness.ShortCode, Money.Kes(10m), "254708374149", "LN-1", Ct));
        Assert.Empty(h.Daraja.Requests);
    }
}
