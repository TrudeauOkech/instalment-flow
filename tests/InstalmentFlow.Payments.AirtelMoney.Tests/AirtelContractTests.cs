using System.Net;
using System.Text;

namespace InstalmentFlow.Payments.AirtelMoney.Tests;

/// <summary>What the adapter sends to Airtel and how it reads each documented answer and callback.</summary>
public sealed class AirtelContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_ussd_push_sends_the_documented_request()
    {
        using var h = new AirtelHarness();
        h.Api.On(AirtelHarness.Collection, Fixture.Text("push-accepted.json"));

        var result = await h.Adapter.InitiateAsync(AirtelHarness.Push(), Ct);

        Assert.Equal(InitiationOutcome.Accepted, result.Outcome);
        var sent = Assert.Single(h.Api.To(AirtelHarness.Collection));
        Assert.Equal(("Bearer token-1", "KE", "KES"), (sent.Headers["Authorization"], sent.Headers["X-Country"], sent.Headers["X-Currency"]));
        Assert.Equal("733123456", sent.Body.GetProperty("subscriber").GetProperty("msisdn").GetString());
        var transaction = sent.Body.GetProperty("transaction");
        Assert.Equal(("400", "ref-1", "KE", "KES"), (transaction.GetProperty("amount").GetRawText(), transaction.GetProperty("id").GetString(),
            transaction.GetProperty("country").GetString(), transaction.GetProperty("currency").GetString()));
        var token = Assert.Single(h.Api.To("/auth/oauth2/token")).Body;
        Assert.Equal((TestCredentials.ClientId, "client_credentials"), (token.GetProperty("client_id").GetString(), token.GetProperty("grant_type").GetString()));
    }

    [Theory]
    [InlineData("+254 733 123 456", AirtelMsisdnFormat.National, "733123456")]
    [InlineData("254733123456", AirtelMsisdnFormat.NationalWithZero, "0733123456")]
    [InlineData("0733123456", AirtelMsisdnFormat.International, "254733123456")]
    public async Task The_number_format_and_collection_path_follow_the_options(string payer, AirtelMsisdnFormat format, string expected)
    {
        using var h = new AirtelHarness(o =>
        {
            o.MsisdnFormat = format;
            o.CollectionPath = "/merchant/v1/payments/";
        });
        h.Api.On("/merchant/v1/payments/", Fixture.Text("push-accepted.json"));

        await h.Adapter.InitiateAsync(AirtelHarness.Push(payer: payer), Ct);

        Assert.Equal(expected, Assert.Single(h.Api.To("/merchant/v1/payments/")).Body.GetProperty("subscriber").GetProperty("msisdn").GetString());
    }

    [Theory]
    [InlineData(400.50, "0733123456", "whole KES")]
    [InlineData(400, "12", "valid KE mobile number")]
    public async Task A_push_airtel_would_get_wrong_is_refused_before_anything_is_sent(decimal amount, string payer, string reason)
    {
        using var h = new AirtelHarness();

        var result = await h.Adapter.InitiateAsync(AirtelHarness.Push(amount, payer), Ct);

        Assert.Equal(InitiationOutcome.Rejected, result.Outcome);
        Assert.Contains(reason, result.Reason, StringComparison.Ordinal);
        Assert.Empty(h.Api.Requests);
    }

    [Fact]
    public async Task Only_an_explicit_success_is_accepted_a_refusal_code_is_ambiguous_and_a_client_error_is_refused()
    {
        using var h = new AirtelHarness();
        h.Api.On(AirtelHarness.Collection, Fixture.Text("push-not-successful.json"))
            .On(AirtelHarness.Collection, """{"status":{"code":"400","message":"Bad request","success":false}}""", HttpStatusCode.BadRequest)
            .On(AirtelHarness.Collection, "<html>gateway</html>", HttpStatusCode.BadGateway);

        var notSuccessful = await h.Adapter.InitiateAsync(AirtelHarness.Push(), Ct);
        var badRequest = await h.Adapter.InitiateAsync(AirtelHarness.Push(), Ct);
        var gateway = await h.Adapter.InitiateAsync(AirtelHarness.Push(), Ct);

        Assert.Equal(InitiationOutcome.Unknown, notSuccessful.Outcome);   // DP codes are disputed: the enquiry decides
        Assert.Contains("DP00800001007", notSuccessful.Reason, StringComparison.Ordinal);
        Assert.Equal(InitiationOutcome.Rejected, badRequest.Outcome);
        Assert.Equal(InitiationOutcome.Unknown, gateway.Outcome);
    }

    [Fact]
    public async Task A_token_is_refreshed_at_four_fifths_of_its_stated_life_and_once_after_a_401()
    {
        using var h = new AirtelHarness();
        h.Api.On(AirtelHarness.Collection, Fixture.Text("push-accepted.json"));

        await h.Adapter.InitiateAsync(AirtelHarness.Push(), Ct);
        h.Clock.Now += TimeSpan.FromSeconds(143);
        await h.Adapter.InitiateAsync(AirtelHarness.Push(), Ct);   // still valid (144 s of 180)
        h.Clock.Now += TimeSpan.FromSeconds(2);
        await h.Adapter.InitiateAsync(AirtelHarness.Push(), Ct);   // refreshed

        Assert.Equal(2, h.Api.TokensIssued);

        using var expired = new AirtelHarness();
        expired.Api.ExpiresIn = "3600";
        expired.Api.On(AirtelHarness.Collection, """{"error":"invalid_token"}""", HttpStatusCode.Unauthorized).On(AirtelHarness.Collection, Fixture.Text("push-accepted.json"));
        var retried = await expired.Adapter.InitiateAsync(AirtelHarness.Push(), Ct);
        Assert.Equal(InitiationOutcome.Accepted, retried.Outcome);
        Assert.Equal(["Bearer token-1", "Bearer token-2"], expired.Api.To(AirtelHarness.Collection).Select(r => r.Headers["Authorization"]));
    }

    [Theory]
    [InlineData("enquiry-ts.json", ProviderTransactionStatus.Succeeded)]
    [InlineData("enquiry-ts-flat.json", ProviderTransactionStatus.Succeeded)]
    [InlineData("enquiry-tf.json", ProviderTransactionStatus.Failed)]
    [InlineData("enquiry-ta.json", ProviderTransactionStatus.Ambiguous)]
    [InlineData("enquiry-tip.json", ProviderTransactionStatus.Pending)]
    public async Task The_enquiry_maps_each_status(string fixture, ProviderTransactionStatus expected)
    {
        using var h = new AirtelHarness();
        h.Api.On(AirtelHarness.Enquiry, Fixture.Text(fixture));

        var status = await h.Adapter.QueryAsync(new ProviderTransactionRef(AirtelHarness.Account, null, "ref-1", Money.Kes(400m)), Ct);

        Assert.Equal(expected, status.Status);
        if (expected == ProviderTransactionStatus.Succeeded)
        {
            Assert.Equal(("MP260302.0915.A12345", null), (status.TransactionId, status.Amount));   // the amount is the one requested
        }

        var sent = Assert.Single(h.Api.To(AirtelHarness.Enquiry));
        Assert.Equal((HttpMethod.Get, "/standard/v1/payments/ref-1", "KE"), (sent.Method, sent.Path, sent.Headers["X-Country"]));
    }

    [Fact]
    public async Task An_unknown_transaction_is_not_found_and_a_server_error_throws()
    {
        using var h = new AirtelHarness();
        h.Api.On(AirtelHarness.Enquiry, """{"status":{"code":"404","message":"Not found","success":false}}""", HttpStatusCode.NotFound)
            .On(AirtelHarness.Enquiry, "{}", HttpStatusCode.InternalServerError);
        var reference = new ProviderTransactionRef(AirtelHarness.Account, null, "ref-1");

        Assert.Equal(ProviderTransactionStatus.NotFound, (await h.Adapter.QueryAsync(reference, Ct)).Status);
        await Assert.ThrowsAsync<AirtelApiException>(() => h.Adapter.QueryAsync(reference, Ct));
    }

    [Fact]
    public async Task A_success_callback_needs_confirmation_and_carries_no_amount()
    {
        using var h = new AirtelHarness();

        var result = await h.Adapter.ParseCallbackAsync(AirtelHarness.Callback("callback-ts.json"), Ct);

        var paid = Assert.Single(result.Events);
        Assert.Equal((PaymentEventKind.CollectionSucceeded, "ref-1", "MP260302.0915.A12345", true), (paid.Kind, paid.MerchantReference, paid.TransactionId, paid.RequiresVerification));
        Assert.Null(paid.Amount);
        Assert.Equal(("airtel:acme-ke-app", h.Clock.Now), (paid.Source, paid.ValueAt));
    }

    [Fact]
    public async Task A_failure_callback_also_needs_confirmation_and_in_progress_says_nothing()
    {
        using var h = new AirtelHarness();

        var failed = Assert.Single((await h.Adapter.ParseCallbackAsync(AirtelHarness.Callback("callback-tf.json"), Ct)).Events);
        var inProgress = await h.Adapter.ParseCallbackAsync(new ProviderCallback("airtel", AirtelAdapter.CollectionResult,
            """{"transaction":{"id":"ref-1","status_code":"TIP"}}"""u8.ToArray(), providerAccount: AirtelHarness.Account), Ct);
        var hashed = await h.Adapter.ParseCallbackAsync(AirtelHarness.Callback("callback-with-hash.json"), Ct);

        Assert.Equal((PaymentEventKind.CollectionFailed, true), (failed.Kind, failed.RequiresVerification));
        Assert.True(inProgress.Accepted);
        Assert.Empty(inProgress.Events);
        Assert.True(Assert.Single(hashed.Events).RequiresVerification);   // the hash is not understood, so it proves nothing
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else")]
    public async Task A_callback_must_come_through_a_configured_accounts_endpoint(string? account)
    {
        using var h = new AirtelHarness();

        Assert.False((await h.Adapter.ParseCallbackAsync(AirtelHarness.Callback("callback-ts.json", account: account), Ct)).Accepted);
    }

    [Theory]
    [InlineData("""{"transaction":{"status_code":"TS","airtel_money_id":"X"}}""")]
    [InlineData("""{"transaction":{"id":"ref-1","status_code":"TS"}}""")]
    [InlineData("""{"transaction":{"id":"ref-1"}}""")]
    [InlineData("""{"transaction":"ref-1"}""")]
    [InlineData("[]")]
    [InlineData("{not json")]
    [InlineData("")]
    public async Task A_malformed_callback_is_rejected_not_thrown(string body)
    {
        using var h = new AirtelHarness();

        var result = await h.Adapter.ParseCallbackAsync(new ProviderCallback("airtel", AirtelAdapter.CollectionResult, Encoding.UTF8.GetBytes(body), providerAccount: AirtelHarness.Account), Ct);

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task Random_and_mutated_callbacks_never_throw()
    {
        using var h = new AirtelHarness();
        var random = new Random(11);
        for (var i = 0; i < 2_000; i++)
        {
            var bytes = Fixture.Bytes(i % 3 == 0 ? "callback-tf.json" : "callback-ts.json");
            if (i % 2 == 0)
            {
                for (var flips = random.Next(1, 8); flips > 0; flips--)
                {
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(32, 127);
                }
            }
            else
            {
                bytes = new byte[random.Next(0, 200)];
                random.NextBytes(bytes);
            }

            var result = await h.Adapter.ParseCallbackAsync(new ProviderCallback("airtel", AirtelAdapter.CollectionResult, bytes, providerAccount: AirtelHarness.Account), Ct);
            Assert.True(result.Accepted || result.RejectionReason is not null);
        }

        var huge = await h.Adapter.ParseCallbackAsync(new ProviderCallback("airtel", AirtelAdapter.CollectionResult, new byte[20_000], providerAccount: AirtelHarness.Account), Ct);
        Assert.Contains("larger than", huge.RejectionReason, StringComparison.Ordinal);
    }
}
