using System.Text;

namespace InstalmentFlow.Payments.Mpesa.Tests;

/// <summary>Parsing Daraja callbacks: every documented variant, and anything malformed is rejected, never thrown.</summary>
public sealed class CallbackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CallbackParseResult> ParseAsync(string kind, byte[] body, string? account = null)
    {
        using var h = new DarajaHarness();
        return await h.Adapter.ParseCallbackAsync(new ProviderCallback("mpesa", kind, body, providerAccount: account), Ct);
    }

    [Fact]
    public async Task A_c2b_confirmation_becomes_a_payment_in_nairobi_time_without_the_payers_name()
    {
        var result = await ParseAsync(MpesaAdapter.C2BConfirmation, Fixture.Bytes("c2b-confirmation.json"));

        Assert.True(result.Accepted);
        Assert.Null(result.HoldReason);
        var paid = Assert.Single(result.Events);
        Assert.Equal((PaymentEventKind.PaymentReceived, "mpesa:600123", "RKTQDM7W6S"), (paid.Kind, paid.Source, paid.TransactionId));
        Assert.Equal(Money.Kes(1_500m), paid.Amount);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 6, 15, 0, TimeSpan.Zero), paid.ValueAt);
        Assert.Equal(("LN-1", "2547 ***** 149"), (paid.AccountReference, paid.Payer));
        Assert.Equal("Pay Bill", paid.Metadata["transactionType"]);
        Assert.DoesNotContain(paid.Metadata.Values, v => v.Contains("JOHN", StringComparison.Ordinal) || v.Contains("DOE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Numbers_sent_as_json_numbers_and_a_hashed_payer_are_read_the_same()
    {
        var result = await ParseAsync(MpesaAdapter.C2BConfirmation, Fixture.Bytes("c2b-confirmation-hashed.json"));

        var paid = Assert.Single(result.Events);
        Assert.Equal(("mpesa:600123", Money.Kes(250m), "ln-1"), (paid.Source, paid.Amount, paid.AccountReference));
        Assert.Equal(64, paid.Payer!.Length);
        Assert.Equal("INV-9", paid.Metadata["invoiceNumber"]);
    }

    [Fact]
    public async Task A_fractional_amount_is_held_for_a_person_and_never_rounded()
    {
        var body = Fixture.Text("c2b-confirmation.json").Replace("\"1500.00\"", "\"100.50\"", StringComparison.Ordinal);

        var result = await ParseAsync(MpesaAdapter.C2BConfirmation, Encoding.UTF8.GetBytes(body));

        Assert.True(result.Accepted);
        Assert.Contains("whole shillings", result.HoldReason, StringComparison.Ordinal);
        Assert.Equal(Money.Kes(100.50m), Assert.Single(result.Events).Amount);
    }

    [Fact]
    public async Task A_confirmation_for_another_shortcode_than_its_endpoint_is_rejected()
    {
        var result = await ParseAsync(MpesaAdapter.C2BConfirmation, Fixture.Bytes("c2b-confirmation.json"), account: "999999");

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task A_successful_stk_result_confirms_the_collection_by_checkout_id()
    {
        var result = await ParseAsync(MpesaAdapter.StkResult, Fixture.Bytes("stk-callback-success.json"), DarajaHarness.ShortCode);

        var paid = Assert.Single(result.Events);
        Assert.Equal((PaymentEventKind.CollectionSucceeded, "ws_CO_191220191020363925", "NLJ7RT61SV"), (paid.Kind, paid.CorrelationId, paid.TransactionId));
        Assert.Equal((Money.Kes(400m), "254708374149", "mpesa:600123"), (paid.Amount, paid.Payer, paid.Source));
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 6, 18, 20, TimeSpan.Zero), paid.ValueAt);
        Assert.Equal("29115-34620561-1", paid.Metadata["merchantRequestId"]);
    }

    [Fact]
    public async Task A_cancelled_stk_result_fails_the_collection()
    {
        var result = await ParseAsync(MpesaAdapter.StkResult, Fixture.Bytes("stk-callback-cancelled.json"), DarajaHarness.ShortCode);

        var failed = Assert.Single(result.Events);
        Assert.Equal(PaymentEventKind.CollectionFailed, failed.Kind);
        Assert.Equal("1032: Request cancelled by user.", failed.Reason);
    }

    [Fact]
    public async Task An_stk_result_needs_its_shortcode_from_the_endpoint()
    {
        var result = await ParseAsync(MpesaAdapter.StkResult, Fixture.Bytes("stk-callback-success.json"));

        Assert.False(result.Accepted);
        Assert.Contains("endpoint", result.RejectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_completed_transaction_status_reports_the_payment_with_the_payers_number_only()
    {
        var result = await ParseAsync(MpesaAdapter.TransactionStatusResult, Fixture.Bytes("transaction-status-result.json"));

        var paid = Assert.Single(result.Events);
        Assert.Equal((PaymentEventKind.PaymentReceived, "mpesa:600123", "RKTQDM7W6S", Money.Kes(1_500m)), (paid.Kind, paid.Source, paid.TransactionId, paid.Amount));
        Assert.Equal("254708374149", paid.Payer);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 6, 15, 2, TimeSpan.Zero), paid.ValueAt);
    }

    [Fact]
    public async Task A_failed_transaction_status_query_with_a_single_parameter_object_reports_nothing()
    {
        var result = await ParseAsync(MpesaAdapter.TransactionStatusResult, Fixture.Bytes("transaction-status-locked.json"));

        Assert.True(result.Accepted);
        Assert.Empty(result.Events);
    }

    [Theory]
    [InlineData(MpesaAdapter.C2BConfirmation, """{"TransID":"X1","TransTime":"20260302091500","BusinessShortCode":"600123"}""")]
    [InlineData(MpesaAdapter.C2BConfirmation, """{"TransID":"X1","TransTime":"2026-03-02","TransAmount":"10","BusinessShortCode":"600123"}""")]
    [InlineData(MpesaAdapter.C2BConfirmation, """{"TransID":"X1","TransTime":"20260302091500","TransAmount":"-10","BusinessShortCode":"600123"}""")]
    [InlineData(MpesaAdapter.C2BConfirmation, """{"TransID":["X1"],"TransTime":"20260302091500","TransAmount":"10","BusinessShortCode":"600123"}""")]
    [InlineData(MpesaAdapter.C2BConfirmation, """{"TransID":"X1","TransTime":"20260302091500","TransAmount":{"v":10},"BusinessShortCode":"600123"}""")]
    [InlineData(MpesaAdapter.C2BConfirmation, "[]")]
    [InlineData(MpesaAdapter.C2BConfirmation, "null")]
    [InlineData(MpesaAdapter.StkResult, """{"Body":{"stkCallback":{"CheckoutRequestID":"ws_1","ResultCode":0}}}""")]
    [InlineData(MpesaAdapter.StkResult, """{"Body":{"stkCallback":{"CheckoutRequestID":"ws_1","ResultCode":0,"CallbackMetadata":{"Item":"x"}}}}""")]
    [InlineData(MpesaAdapter.StkResult, """{"Body":{}}""")]
    [InlineData(MpesaAdapter.TransactionStatusResult, """{"Result":{"ResultCode":0,"ResultParameters":{"ResultParameter":[{"Key":"TransactionStatus","Value":"Completed"}]}}}""")]
    [InlineData("c2b-validation", """{"TransID":"X1"}""")]
    [InlineData(MpesaAdapter.C2BConfirmation, "{not json")]
    [InlineData(MpesaAdapter.C2BConfirmation, "")]
    public async Task A_malformed_callback_is_rejected_not_thrown(string kind, string body)
    {
        var result = await ParseAsync(kind, Encoding.UTF8.GetBytes(body), DarajaHarness.ShortCode);

        Assert.False(result.Accepted);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public async Task A_huge_or_deeply_nested_callback_is_rejected_unread()
    {
        var huge = Encoding.UTF8.GetBytes($$"""{"TransID":"{{new string('x', 70_000)}}"}""");
        var deep = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"a\":", 64)) + "1" + new string('}', 64));

        Assert.Contains("larger than", (await ParseAsync(MpesaAdapter.C2BConfirmation, huge)).RejectionReason, StringComparison.Ordinal);
        Assert.False((await ParseAsync(MpesaAdapter.C2BConfirmation, deep)).Accepted);
    }

    [Fact]
    public async Task Random_bytes_and_mutated_fixtures_never_throw()
    {
        var random = new Random(9);
        var fixtures = new[] { ("c2b-confirmation.json", MpesaAdapter.C2BConfirmation), ("stk-callback-success.json", MpesaAdapter.StkResult), ("transaction-status-result.json", MpesaAdapter.TransactionStatusResult) };
        using var h = new DarajaHarness();
        for (var i = 0; i < 2_000; i++)
        {
            var (name, kind) = fixtures[random.Next(fixtures.Length)];
            var bytes = Fixture.Bytes(name);
            if (i % 2 == 0)
            {
                for (var flips = random.Next(1, 8); flips > 0; flips--)
                {
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(32, 127);
                }
            }
            else
            {
                bytes = new byte[random.Next(0, 300)];
                random.NextBytes(bytes);
            }

            var result = await h.Adapter.ParseCallbackAsync(new ProviderCallback("mpesa", kind, bytes, providerAccount: DarajaHarness.ShortCode), Ct);
            Assert.True(result.Accepted || result.RejectionReason is not null);
        }
    }

    [Fact]
    public void A_validation_request_is_read_and_answered_in_Daraja_format()
    {
        var request = MpesaValidation.Parse(Fixture.Bytes("c2b-validation.json"));

        Assert.Equal(("RKTQDM7W6U", "600123", Money.Kes(900m), "LN-1"), (request.TransactionId, request.ShortCode, request.Amount, request.AccountReference));
        Assert.Equal("""{"ResultCode":"0","ResultDesc":"Accepted"}""", Encoding.UTF8.GetString(MpesaValidation.Accept()));
        Assert.Equal("""{"ResultCode":"C2B00012","ResultDesc":"Rejected"}""", Encoding.UTF8.GetString(MpesaValidation.Reject(MpesaRejection.InvalidAccountNumber)));
        Assert.Throws<FormatException>(() => MpesaValidation.Parse("{}"u8.ToArray()));
        Assert.Throws<FormatException>(() => MpesaValidation.Parse("nope"u8.ToArray()));
    }
}
