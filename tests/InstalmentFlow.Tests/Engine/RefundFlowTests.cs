namespace InstalmentFlow.Tests;

/// <summary>Refunds, recorded only (ADR-010), and their interplay with reversals (the reversal deficit, ADR-005).</summary>
public sealed class RefundFlowTests
{
    private static readonly DateOnly s_today = Flows.Today;

    private static CancellationToken Ct => Flows.Ct;

    [Fact]
    public async Task An_overpayment_is_refunded_and_confirmed()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 700m, "LN100001");

        var requested = await Request(f, customer, 200m);
        Assert.Equal((RefundStatus.Requested, Money.Kes(0m)), (requested.Refund.Status, requested.UnappliedAfter));
        f.AssertInvariants();

        var confirmed = await Complete(f, requested.Refund.RefundId, RefundCompletion.Confirmed);

        Assert.Equal(RefundStatus.Confirmed, confirmed.Refund.Status);
        Assert.Equal(Money.Kes(0m), await f.UnappliedAsync(customer));
        var payment = await f.Flow.Queries.GetPaymentAsync(PaymentRef.BySource(Flows.Source("TX1")), Ct);
        Assert.Equal(Money.Kes(200m), payment.Refunded);
        f.AssertInvariants();
    }

    [Fact]
    public async Task A_failed_refund_returns_the_credit()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 700m, "LN100001");
        var requested = await Request(f, customer, 200m);

        var failed = await Complete(f, requested.Refund.RefundId, RefundCompletion.Failed);

        Assert.Equal((RefundStatus.Failed, Money.Kes(200m)), (failed.Refund.Status, failed.UnappliedAfter));
        Assert.Equal("Wallet closed", failed.Refund.FailureReason);
        Assert.Equal(Money.Kes(200m), await f.UnappliedAsync(customer));
        f.AssertInvariants();
    }

    [Fact]
    public async Task Refunds_are_bounded_and_complete_exactly_once()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 600m, "LN100001");

        var tooMuch = await Assert.ThrowsAsync<RuleViolationException>(() => Request(f, customer, 101m));
        var refund = await Request(f, customer, 100m);
        var replay = await Request(f, customer, 100m);
        await Complete(f, refund.Refund.RefundId, RefundCompletion.Confirmed);
        var twice = await Assert.ThrowsAsync<RuleViolationException>(() => Complete(f, refund.Refund.RefundId, RefundCompletion.Failed, key: "again"));

        Assert.Equal(ErrorCodes.InsufficientFunds, tooMuch.ErrorCode);
        Assert.Equal(refund.Refund.RefundId, replay.Refund.RefundId);
        Assert.Equal(ErrorCodes.RefundNotPending, twice.ErrorCode);
        Assert.Equal(RefundStatus.Confirmed, (await f.Flow.Refunds.GetAsync(refund.Refund.RefundId, Ct)).Status);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Refunds_take_the_oldest_funds_first()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today.AddDays(30), 500m);   // nothing due: both payments are held
        var second = await f.Flow.Payments.RecordAsync(Flows.Paybill("TX2", 100m, "LN100001") with { ValueAt = new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero) }, Ct);
        var first = await f.PayAsync("TX1", 100m, "LN100001");   // recorded later, but its money moved earlier

        var refund = await Request(f, customer, 150m);

        Assert.Equal([(first.PaymentId, 100m), (second.PaymentId, 50m)], refund.Refund.Portions.Select(p => (p.PaymentId, p.Amount.Amount)));
        f.AssertInvariants();
    }

    // ----- Reversal deficit -----

    [Fact]
    public async Task Reversal_after_a_refund_records_a_deficit_the_customer_owes()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 700m, "LN100001");
        var refund = await Request(f, customer, 200m);
        await Complete(f, refund.Refund.RefundId, RefundCompletion.Confirmed);

        var reversal = await f.ReverseAsync("TX1");

        Assert.Equal((Money.Kes(0m), Money.Kes(500m), Money.Kes(200m)), (reversal.FromUnapplied, reversal.Unwound, reversal.Deficit));
        var account = await f.Flow.Queries.GetAccountAsync(customer, cancellationToken: Ct);
        Assert.Equal(Money.Kes(200m), Assert.Single(account.ReversalReceivable));
        Assert.Equal(Money.Kes(500m), (await f.LoanViewAsync("LN100001")).Outstanding);
        Assert.Contains(f.Snapshot.Events, e => e.Type == "ReversalDeficitRecorded");
        f.AssertInvariants();
    }

    [Fact]
    public async Task A_refund_that_fails_after_its_payment_was_reversed_settles_the_deficit_instead_of_returning_credit()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 700m, "LN100001");
        var refund = await Request(f, customer, 200m);
        await f.ReverseAsync("TX1");   // 200 of it was pending refund: a deficit

        var failed = await Complete(f, refund.Refund.RefundId, RefundCompletion.Failed);

        Assert.Equal(Money.Kes(0m), failed.UnappliedAfter);
        Assert.Empty((await f.Flow.Queries.GetAccountAsync(customer, cancellationToken: Ct)).ReversalReceivable);
        f.AssertInvariants();
    }

    // ----- From the exception queue -----

    [Fact]
    public async Task Unidentified_funds_can_be_marked_for_refund_and_a_failed_payout_reopens_the_exception()
    {
        var f = new Flows();
        var recorded = await f.PayAsync("TX1", 300m, "WRONG");

        var marked = await f.Flow.Exceptions.MarkForRefundAsync(new MarkForRefundRequest
        {
            IdempotencyKey = "mark", ExceptionId = recorded.Exception!.ExceptionId, Reason = "Paid the wrong paybill", ResolvedBy = "ops",
        }, Ct);
        Assert.Equal(ExceptionResolutionKind.MarkedForRefund, marked.Exception.Resolution!.Kind);
        var refund = await f.Flow.Refunds.GetAsync(marked.RefundId!.Value, Ct);
        Assert.Equal(("mpesa:600123", Money.Kes(300m)), (refund.UnidentifiedSource, refund.Amount));
        f.AssertInvariants();

        await Complete(f, refund.RefundId, RefundCompletion.Failed);

        var reopened = await f.Flow.Exceptions.ListOpenAsync(cancellationToken: Ct).ToListAsync(Ct);
        Assert.Equal(PaymentExceptionReason.RefundFailed, Assert.Single(reopened).Reason);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Held_funds_with_an_open_exception_are_refunded_only_through_the_exception_queue()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 300m);
        await f.LoanAsync(customer, "LN100002", s_today, 300m);
        var held = await f.Flow.Payments.RecordAsync(Flows.Request("TX1", 200m, PaymentTarget.ForCustomer(customer)), Ct);

        var blocked = await Assert.ThrowsAsync<RuleViolationException>(() => Request(f, customer, 200m));
        var marked = await f.Flow.Exceptions.MarkForRefundAsync(new MarkForRefundRequest
        {
            IdempotencyKey = "mark", ExceptionId = held.Exception!.ExceptionId, Reason = "Customer asked for it back", ResolvedBy = "ops",
        }, Ct);
        await Complete(f, marked.RefundId!.Value, RefundCompletion.Confirmed);

        Assert.Equal(ErrorCodes.InsufficientFunds, blocked.ErrorCode);
        Assert.Equal(Money.Kes(0m), await f.UnappliedAsync(customer));
        Assert.Empty(await f.Flow.Exceptions.ListOpenAsync(cancellationToken: Ct).ToListAsync(Ct));
        f.AssertInvariants();
    }

    private static Task<RefundResult> Request(Flows f, AccountId customer, decimal amount) =>
        f.Flow.Refunds.RequestAsync(new RequestRefundRequest
        {
            IdempotencyKey = $"refund-{amount}", Account = customer, Amount = Money.Kes(amount), Reason = "Overpayment", RequestedBy = "ops",
        }, Ct);

    private static Task<RefundResult> Complete(Flows f, RefundId refund, RefundCompletion completion, string? key = null) =>
        f.Flow.Refunds.CompleteAsync(new CompleteRefundRequest
        {
            IdempotencyKey = key ?? $"complete-{refund}",
            Refund = refund,
            Completion = completion,
            FailureReason = completion == RefundCompletion.Failed ? "Wallet closed" : null,
            ProviderReference = completion == RefundCompletion.Confirmed ? new ExternalReference("mpesa-b2c", "B2C1") : null,
        }, Ct);
}
