using System.Text;
using InstalmentFlow.Payments.Simulation;

namespace InstalmentFlow.Payments.Tests;

/// <summary>The inbox stores each provider event once, before processing, and processes it with retries (doc 03 §6).</summary>
public sealed class InboxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_paybill_callback_is_recorded_and_allocated_to_the_quoted_reference()
    {
        var h = await Harness.CreateAsync();
        var paid = h.PayBill(300m);

        var report = await h.DeliverAndProcessAsync();

        Assert.Equal(1, report.Processed);
        var payment = Assert.Single(await h.PaymentsAsync());
        Assert.Equal(paid.TransactionId, payment.Source.Value);
        Assert.Equal(Money.Kes(300m), payment.Allocated);
        Assert.Equal(Money.Kes(300m), await h.LoanPaidAsync());
        Assert.Equal("2547****0000", payment.Metadata["payer"]);
        await h.AssertLedgerVerifiesAsync();
    }

    [Fact]
    public async Task A_duplicated_callback_is_stored_and_recorded_once()
    {
        var h = await Harness.CreateAsync();
        h.PayBill(300m, faults: SimulatedFaults.Duplicate);

        await h.Simulator.DeliverDueAsync(Ct);
        var report = await h.Payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal(1, report.Processed);
        Assert.Single(await h.PaymentsAsync());
        Assert.Single(await h.Payments.Inbox.ListAsync(InboxStatus.Processed, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_callback_received_again_after_processing_changes_nothing()
    {
        var h = await Harness.CreateAsync();
        var body = """{"type":"payment","account":"600123","transactionId":"SIMX1","amount":"250","currency":"KES","time":"2026-03-02T05:00:00Z","reference":"LN-1"}"""u8.ToArray();
        var callback = new ProviderCallback("sim", "payment", body, new Dictionary<string, string> { [SimulatedProvider.SignatureHeader] = h.Simulator.Sign(body) });

        var first = await h.Payments.Inbox.ReceiveAsync(callback, Ct);
        await h.Payments.Inbox.ProcessPendingAsync(Ct);
        var again = await h.Payments.Inbox.ReceiveAsync(callback, Ct);

        Assert.Equal(InboxReceiptOutcome.Accepted, first.Outcome);
        Assert.Equal(InboxReceiptOutcome.Duplicate, again.Outcome);
        Assert.Equal(0, (await h.Payments.Inbox.ProcessPendingAsync(Ct)).Processed);
        Assert.Equal(Money.Kes(250m), await h.LoanPaidAsync());
    }

    [Fact]
    public async Task A_forged_or_malformed_callback_is_rejected_and_never_stored()
    {
        var h = await Harness.CreateAsync();
        var body = """{"type":"payment","account":"600123","transactionId":"FAKE1","amount":"50000","currency":"KES","time":"2026-03-02T05:00:00Z","reference":"LN-1"}"""u8.ToArray();

        var forged = await h.Payments.Inbox.ReceiveAsync(new ProviderCallback("sim", "payment", body, new Dictionary<string, string> { [SimulatedProvider.SignatureHeader] = "00" }), Ct);
        var unsigned = await h.Payments.Inbox.ReceiveAsync(new ProviderCallback("sim", "payment", body), Ct);
        var garbage = Encoding.UTF8.GetBytes("{not json");
        var malformed = await h.Payments.Inbox.ReceiveAsync(new ProviderCallback("sim", "payment", garbage, new Dictionary<string, string> { [SimulatedProvider.SignatureHeader] = h.Simulator.Sign(garbage) }), Ct);

        Assert.All(new[] { forged, unsigned, malformed }, r => Assert.Equal(InboxReceiptOutcome.Rejected, r.Outcome));
        Assert.Equal(0, (await h.Payments.Inbox.ProcessPendingAsync(Ct)).Processed);
        Assert.Empty(await h.PaymentsAsync());
    }

    [Fact]
    public async Task A_callback_for_an_unknown_provider_is_refused()
    {
        var h = await Harness.CreateAsync();

        var error = await Assert.ThrowsAsync<RuleViolationException>(() => h.Payments.Inbox.ReceiveAsync(new ProviderCallback("mpesa", "c2b", "{}"u8.ToArray()), Ct));

        Assert.Equal(PaymentsErrorCodes.UnknownProvider, error.ErrorCode);
    }

    [Fact]
    public async Task A_callback_delayed_by_hours_is_recorded_when_it_arrives_with_the_original_value_time()
    {
        var h = await Harness.CreateAsync();
        var paid = h.PayBill(300m, delay: TimeSpan.FromHours(5));

        Assert.Equal(0, (await h.DeliverAndProcessAsync()).Processed);
        h.Clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(1, (await h.DeliverAndProcessAsync()).Processed);

        var payment = Assert.Single(await h.PaymentsAsync());
        Assert.Equal(paid.ValueAt, payment.ValueAt);
        Assert.True(payment.RecordedAt > payment.ValueAt);
    }

    [Fact]
    public async Task A_reversal_delivered_before_its_payment_still_ends_with_the_payment_reversed()
    {
        var h = await Harness.CreateAsync();
        var paid = h.PayBill(300m, faults: SimulatedFaults.Reorder);   // held back until the next callback
        h.Simulator.Reverse(paid.TransactionId);

        await h.Simulator.DeliverDueAsync(Ct);
        await h.Payments.Inbox.ProcessPendingAsync(Ct);

        var payment = Assert.Single(await h.PaymentsAsync());
        Assert.Equal(Money.Kes(300m), payment.Reversed);
        Assert.Equal(Money.Kes(0m), await h.LoanPaidAsync());
        await h.AssertLedgerVerifiesAsync();
    }

    [Fact]
    public async Task A_payment_without_a_reference_is_recorded_as_unidentified_never_refused()
    {
        var h = await Harness.CreateAsync();
        h.PayBill(120m, reference: null);

        await h.DeliverAndProcessAsync();

        var payment = Assert.Single(await h.PaymentsAsync());
        Assert.Null(payment.AccountId);
        Assert.NotNull(payment.ExceptionId);
        await h.AssertLedgerVerifiesAsync();
    }

    [Fact]
    public async Task An_unroutable_event_waits_in_the_inbox_and_is_processed_once_its_account_is_mapped()
    {
        var h = await Harness.CreateAsync(b => b.Inbox.RetryDelay = TimeSpan.FromMinutes(1));
        h.PayBill(200m, account: "700700");

        var first = await h.DeliverAndProcessAsync();
        Assert.Equal((0, 1), (first.Processed, first.Retrying));
        var waiting = Assert.Single(await h.Payments.Inbox.ListAsync(InboxStatus.Pending, cancellationToken: Ct));
        Assert.Contains("700700", waiting.Error, StringComparison.Ordinal);

        await h.Payments.Routing.MapAsync("sim", "700700", Harness.Tenant, Ct);
        Assert.Equal(0, (await h.Payments.Inbox.ProcessPendingAsync(Ct)).Processed);   // not due yet
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await h.Payments.Inbox.ProcessPendingAsync(Ct)).Processed);
        Assert.Equal(Harness.Tenant, (await h.Payments.Inbox.ListAsync(InboxStatus.Processed, cancellationToken: Ct)).Single().Tenant?.Value);
    }

    [Fact]
    public async Task Retries_back_off_and_end_in_failed_which_can_be_requeued()
    {
        var h = await Harness.CreateAsync(b =>
        {
            b.Inbox.MaxAttempts = 3;
            b.Inbox.RetryDelay = TimeSpan.FromMinutes(1);
        });
        h.PayBill(200m, account: "700700");
        await h.Simulator.DeliverDueAsync(Ct);

        await h.Payments.Inbox.ProcessPendingAsync(Ct);   // attempt 1; next in 1 min
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Payments.Inbox.ProcessPendingAsync(Ct);   // attempt 2; next in 2 min
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, (await h.Payments.Inbox.ProcessPendingAsync(Ct)).Retrying);   // backed off
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await h.Payments.Inbox.ProcessPendingAsync(Ct)).Failed);   // attempt 3: gives up

        var failed = Assert.Single(await h.Payments.Inbox.ListAsync(InboxStatus.Failed, cancellationToken: Ct));
        await h.Payments.Routing.MapAsync("sim", "700700", Harness.Tenant, Ct);
        Assert.True(await h.Payments.Inbox.RequeueAsync(failed.EntryId, Ct));
        Assert.Equal(1, (await h.Payments.Inbox.ProcessPendingAsync(Ct)).Processed);
    }

    [Fact]
    public async Task An_event_a_rule_refuses_fails_at_once_and_waits_for_a_person()
    {
        var h = await Harness.CreateAsync();
        var paid = h.PayBill(300m);
        await h.DeliverAndProcessAsync();
        h.Simulator.Reverse(paid.TransactionId, Money.Kes(500m));   // more than was paid (I-8)

        var report = await h.DeliverAndProcessAsync();

        Assert.Equal(1, report.Failed);
        var failed = Assert.Single(await h.Payments.Inbox.ListAsync(InboxStatus.Failed, cancellationToken: Ct));
        Assert.StartsWith(ErrorCodes.ReversalExceedsPayment, failed.Error, StringComparison.Ordinal);
        Assert.Equal(Money.Kes(0m), Assert.Single(await h.PaymentsAsync()).Reversed);
    }

    [Fact]
    public async Task An_entry_left_by_a_crashed_processor_is_processed_once_its_lease_runs_out()
    {
        var h = await Harness.CreateAsync(b => b.Inbox.Lease = TimeSpan.FromMinutes(2));
        h.PayBill(300m);
        await h.Simulator.DeliverDueAsync(Ct);
        var claimed = await h.Payments.Store.ClaimInboxAsync(h.Clock.Now, h.Clock.Now.AddMinutes(2), 10, Ct);   // and then the process dies
        Assert.Single(claimed);

        Assert.Equal(0, (await h.Payments.Inbox.ProcessPendingAsync(Ct)).Processed);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, (await h.Payments.Inbox.ProcessPendingAsync(Ct)).Processed);
        Assert.Single(await h.PaymentsAsync());
    }

    [Fact]
    public async Task Parallel_processors_record_every_payment_exactly_once()
    {
        var h = await Harness.CreateAsync(b => b.Inbox.BatchSize = 3);
        for (var i = 0; i < 30; i++)
        {
            h.PayBill(10m, faults: i % 3 == 0 ? SimulatedFaults.Duplicate : SimulatedFaults.None);
        }

        await h.Simulator.DeliverDueAsync(Ct);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => h.Payments.Inbox.ProcessPendingAsync(Ct), Ct)));
        h.Clock.Advance(TimeSpan.FromHours(2));
        await h.Payments.Inbox.ProcessPendingAsync(Ct);   // anything a contended processor left for retry

        Assert.Equal(30, (await h.PaymentsAsync()).Count);
        Assert.Equal(Money.Kes(300m), await h.LoanPaidAsync());
        await h.AssertLedgerVerifiesAsync();
    }
}
