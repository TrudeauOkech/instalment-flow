using System.Diagnostics;
using System.Net;
using System.Text;
using InstalmentFlow.Payments;

namespace InstalmentFlow.AspNetCore.Tests;

/// <summary>The callback and validation endpoints (doc 03 §6, §7.4).</summary>
public sealed class CallbackEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_callback_is_answered_after_it_is_stored_and_then_recorded_by_the_processor()
    {
        await using var host = await Host.StartAsync();

        var response = await host.Client.SendAsync(host.SimulatorCallback("SIMH1"), Ct);
        await host.Payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"accepted"}""", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(Money.Kes(300m), await host.LoanPaidAsync());
    }

    [Fact]
    public async Task Concurrent_duplicate_posts_are_all_acknowledged_and_stored_once()
    {
        await using var host = await Host.StartAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => host.Client.SendAsync(host.SimulatorCallback("SIMH2"), Ct)));
        await host.Payments.Inbox.ProcessPendingAsync(Ct);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(await host.Payments.Inbox.ListAsync(InboxStatus.Processed, cancellationToken: Ct));
        Assert.Equal(Money.Kes(300m), await host.LoanPaidAsync());
    }

    [Fact]
    public async Task A_wrong_path_secret_is_a_404_and_stores_nothing()
    {
        await using var host = await Host.StartAsync();

        var response = await host.Client.SendAsync(host.SimulatorCallback("SIMH3", secret: "k3y-0123456789abcdeX"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, (await host.Payments.GetHealthAsync(Ct)).PendingInbox);
    }

    [Fact]
    public async Task A_forged_callback_is_refused_with_400()
    {
        await using var host = await Host.StartAsync();

        var response = await host.Client.SendAsync(host.SimulatorCallback("SIMH4", amount: 900_000m, signature: "00"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await host.Payments.GetHealthAsync(Ct)).PendingInbox);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_with_413()
    {
        await using var host = await Host.StartAsync(o => o.Callbacks.MaxBodyBytes = 1_024);

        var response = await host.Client.SendAsync(Host.Post($"/hooks/{Host.Secret}/sim/payment", new string('x', 5_000)), Ct);
        var streamed = await host.SendFromAsync(IPAddress.Loopback, $"/hooks/{Host.Secret}/sim/payment", new byte[5_000]);   // no Content-Length

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(StatusCodes413, streamed.Response.StatusCode);
    }

    [Fact]
    public async Task Only_allowed_networks_may_post_for_a_provider()
    {
        await using var host = await Host.StartAsync(o => o.Callbacks.AllowedNetworks["sim"] = [IPNetwork.Parse("196.201.214.0/24")]);
        var body = Encoding.UTF8.GetBytes("{}");

        var allowed = await host.SendFromAsync(IPAddress.Parse("196.201.214.200"), $"/hooks/{Host.Secret}/sim/payment", body);
        var mapped = await host.SendFromAsync(IPAddress.Parse("196.201.214.9").MapToIPv6(), $"/hooks/{Host.Secret}/sim/payment", body);
        var refused = await host.SendFromAsync(IPAddress.Parse("10.0.0.7"), $"/hooks/{Host.Secret}/sim/payment", body);

        Assert.Equal(400, allowed.Response.StatusCode);   // through the filter; the unsigned body is then rejected
        Assert.Equal(400, mapped.Response.StatusCode);
        Assert.Equal(403, refused.Response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_provider_is_a_404()
    {
        await using var host = await Host.StartAsync();

        var response = await host.Client.SendAsync(Host.Post($"/hooks/{Host.Secret}/airtel/payment", "{}"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Mpesa_confirmations_and_stk_results_get_darajas_acknowledgement()
    {
        await using var host = await Host.StartAsync();
        var confirmation = """{"TransactionType":"Pay Bill","TransID":"RKTQDM7W6S","TransTime":"20260302091500","TransAmount":"400.00","BusinessShortCode":"600123","BillRefNumber":"LN-1","MSISDN":"2547 ***** 149"}""";
        var stk = """{"Body":{"stkCallback":{"MerchantRequestID":"1","CheckoutRequestID":"ws_CO_1","ResultCode":1032,"ResultDesc":"Request cancelled by user."}}}""";

        var confirmed = await host.Client.SendAsync(Host.Post($"/hooks/{Host.Secret}/mpesa/c2b-confirmation", confirmation), Ct);
        var withAccount = await host.Client.SendAsync(Host.Post($"/hooks/{Host.Secret}/mpesa/stk/{Host.Paybill}", stk), Ct);
        var withoutAccount = await host.Client.SendAsync(Host.Post($"/hooks/{Host.Secret}/mpesa/stk", stk), Ct);
        var timeout = await host.Client.SendAsync(Host.Post($"/hooks/{Host.Secret}/mpesa/transaction-status-timeout/{Host.Paybill}", "{}"), Ct);
        await host.Payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal((HttpStatusCode.OK, """{"ResultCode":"0","ResultDesc":"Success"}"""), (confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(HttpStatusCode.OK, withAccount.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutAccount.StatusCode);
        Assert.Equal(HttpStatusCode.OK, timeout.StatusCode);
        Assert.Equal(Money.Kes(400m), await host.LoanPaidAsync());
    }

    [Fact]
    public async Task Validation_accepts_by_default_even_an_unknown_reference()
    {
        await using var host = await Host.StartAsync();

        var reply = await ValidateAsync(host, "NOPE-9");

        Assert.Equal("""{"ResultCode":"0","ResultDesc":"Accepted"}""", reply);
    }

    [Fact]
    public async Task A_tenant_that_opted_in_refuses_unknown_references_at_validation()
    {
        await using var host = await Host.StartAsync(o => o.Callbacks.RejectUnknownReferences.Add(Host.Tenant));

        Assert.Equal("""{"ResultCode":"C2B00012","ResultDesc":"Rejected"}""", await ValidateAsync(host, "NOPE-9"));
        Assert.Equal("""{"ResultCode":"0","ResultDesc":"Accepted"}""", await ValidateAsync(host, "ln-1"));   // references are matched normalised
        Assert.Equal(0, (await host.Payments.GetHealthAsync(Ct)).PendingInbox);   // validation never stores anything
    }

    [Fact]
    public async Task Validation_answers_the_default_within_its_budget_when_the_check_is_slow()
    {
        await using var host = await Host.StartAsync(o =>
        {
            o.Callbacks.ValidationBudget = TimeSpan.FromMilliseconds(200);
            o.Callbacks.RejectUnknownReferences.Add(Host.Tenant);
        }, router: new SlowRouter(TimeSpan.FromSeconds(10)));
        var clock = Stopwatch.StartNew();

        var reply = await ValidateAsync(host, "NOPE-9");

        Assert.Equal("""{"ResultCode":"0","ResultDesc":"Accepted"}""", reply);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"answered after {clock.Elapsed}");
    }

    [Fact]
    public void The_path_secret_must_be_configured_and_long_enough()
    {
        var error = Assert.ThrowsAny<Exception>(() => Host.StartAsync(o => o.Callbacks.PathSecret = "short").GetAwaiter().GetResult());

        Assert.Contains("PathSecret", error.Message, StringComparison.Ordinal);
    }

    private const int StatusCodes413 = 413;

    private static async Task<string> ValidateAsync(Host host, string reference)
    {
        var body = $$"""{"TransactionType":"Pay Bill","TransID":"RKTQDM7W6U","TransTime":"20260302111500","TransAmount":"900.00","BusinessShortCode":"600123","BillRefNumber":"{{reference}}"}""";
        var response = await host.Client.SendAsync(Host.Post($"/hooks/{Host.Secret}/mpesa/validate", body), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }
}
