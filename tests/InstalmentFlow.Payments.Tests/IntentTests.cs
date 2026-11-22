using InstalmentFlow.Payments.Simulation;

namespace InstalmentFlow.Payments.Tests;

/// <summary>Payment intents (doc 02 §6.2): a timeout is not a failure, and the same key never asks the provider twice.</summary>
public sealed class IntentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_approved_push_is_confirmed_by_its_callback_and_paid_into_the_obligation()
    {
        var h = await Harness.CreateAsync();

        var intent = await h.InitiateAsync("push-1");
        Assert.Equal(PaymentIntentStatus.Submitted, intent.Status);
        Assert.Equal(Harness.Loan, intent.AccountReference);   // the obligation's payment reference, by default
        Assert.Equal(h.LoanId, intent.ObligationId);

        await h.DeliverAndProcessAsync();

        var confirmed = await h.TenantPayments.Intents.GetAsync(intent.IntentId, Ct);
        Assert.Equal(PaymentIntentStatus.Confirmed, confirmed.Status);
        var payment = Assert.Single(await h.PaymentsAsync());
        Assert.Equal(confirmed.PaymentId, payment.PaymentId);
        Assert.Equal(PaymentTargetKind.Obligation, payment.TargetKind);
        Assert.Equal(Money.Kes(400m), await h.LoanPaidAsync());
        await h.AssertLedgerVerifiesAsync();
    }

    [Fact]
    public async Task An_ambiguous_intent_never_initiates_twice_and_is_confirmed_when_the_callback_arrives()
    {
        var h = await Harness.CreateAsync();
        h.Simulator.NextPush(new SimulatedPush { Faults = SimulatedFaults.TimeoutThenSuccess, CallbackDelay = TimeSpan.FromMinutes(3) });

        var first = await h.InitiateAsync("push-1");
        var retried = await h.InitiateAsync("push-1");   // the customer's app retries
        var again = await h.InitiateAsync("push-1");

        Assert.Equal(PaymentIntentStatus.Ambiguous, first.Status);
        Assert.Equal(first.IntentId, retried.IntentId);
        Assert.Equal(first.IntentId, again.IntentId);
        Assert.Equal(1, h.Simulator.PushRequests);

        h.Clock.Advance(TimeSpan.FromMinutes(3));
        await h.DeliverAndProcessAsync();
        Assert.Equal(PaymentIntentStatus.Confirmed, (await h.TenantPayments.Intents.GetAsync(first.IntentId, Ct)).Status);
        Assert.Single(await h.PaymentsAsync());
    }

    [Fact]
    public async Task Timeout_then_success_with_a_lost_callback_is_resolved_by_a_status_query()
    {
        var h = await Harness.CreateAsync(b => b.Intents.FirstCheck = TimeSpan.FromMinutes(1));
        h.Simulator.NextPush(new SimulatedPush { Faults = SimulatedFaults.TimeoutThenSuccess | SimulatedFaults.Drop });
        var intent = await h.InitiateAsync("push-1");
        Assert.Null(intent.CorrelationId);   // the timeout lost the provider's ID; the query uses our merchant reference

        Assert.Equal(0, (await h.Payments.IntentResolver.ResolveDueAsync(Ct)).Checked);   // not due yet
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var resolved = await h.Payments.IntentResolver.ResolveDueAsync(Ct);
        await h.Payments.Inbox.ProcessPendingAsync(Ct);

        Assert.Equal(1, resolved.Confirmed);
        var confirmed = await h.TenantPayments.Intents.GetAsync(intent.IntentId, Ct);
        Assert.Equal(PaymentIntentStatus.Confirmed, confirmed.Status);
        Assert.Equal(h.Simulator.Transactions.Single().TransactionId, confirmed.TransactionId);
        Assert.Single(await h.PaymentsAsync());
        Assert.Equal(1, h.Simulator.PushRequests);
    }

    [Fact]
    public async Task A_callback_and_a_status_query_for_the_same_collection_record_one_payment()
    {
        var h = await Harness.CreateAsync(b => b.Intents.FirstCheck = TimeSpan.FromMinutes(1));
        h.Simulator.NextPush(new SimulatedPush { CallbackDelay = TimeSpan.FromMinutes(5) });
        var intent = await h.InitiateAsync("push-1");

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Payments.IntentResolver.ResolveDueAsync(Ct);
        await h.Payments.Inbox.ProcessPendingAsync(Ct);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.DeliverAndProcessAsync();

        Assert.Single(await h.PaymentsAsync());
        Assert.Equal(PaymentIntentStatus.Confirmed, (await h.TenantPayments.Intents.GetAsync(intent.IntentId, Ct)).Status);
        Assert.Equal(Money.Kes(400m), await h.LoanPaidAsync());
    }

    [Fact]
    public async Task Scenario4_a_lost_push_reply_then_a_manual_paybill_payment_records_both_and_keeps_the_extra_as_credit()
    {
        var h = await Harness.CreateAsync();
        h.Simulator.NextPush(new SimulatedPush { Faults = SimulatedFaults.TimeoutThenSuccess });
        var intent = await h.InitiateAsync("push-1", amount: 400m);   // "checking…": the customer approves the push
        h.PayBill(400m);   // and, unsure, also pays by paybill

        await h.DeliverAndProcessAsync();

        Assert.Equal(PaymentIntentStatus.Confirmed, (await h.TenantPayments.Intents.GetAsync(intent.IntentId, Ct)).Status);
        Assert.Equal(2, (await h.PaymentsAsync()).Count);
        Assert.Equal(Money.Kes(500m), await h.LoanPaidAsync());   // the due instalment
        Assert.Equal(Money.Kes(300m), (await h.Flow.Queries.GetAccountAsync(AccountRef.ById(h.Customer), cancellationToken: Ct)).Unapplied.Single());
        await h.AssertLedgerVerifiesAsync();
    }

    [Fact]
    public async Task A_declined_push_fails_and_records_nothing()
    {
        var h = await Harness.CreateAsync();
        h.Simulator.NextPush(new SimulatedPush { Outcome = SimulatedPushOutcome.Decline });

        var intent = await h.InitiateAsync("push-1");
        await h.DeliverAndProcessAsync();

        var failed = await h.TenantPayments.Intents.GetAsync(intent.IntentId, Ct);
        Assert.Equal(PaymentIntentStatus.Failed, failed.Status);
        Assert.NotNull(failed.FailureReason);
        Assert.Empty(await h.PaymentsAsync());
    }

    [Fact]
    public async Task A_refused_request_fails_at_once()
    {
        var h = await Harness.CreateAsync();
        h.Simulator.NextPush(new SimulatedPush { Outcome = SimulatedPushOutcome.Reject });

        var intent = await h.InitiateAsync("push-1");

        Assert.Equal(PaymentIntentStatus.Failed, intent.Status);
        Assert.Equal(0, (await h.Payments.IntentResolver.ResolveDueAsync(Ct)).Checked);
    }

    [Fact]
    public async Task An_unanswered_push_is_checked_with_back_off_and_expires_after_its_time_to_live()
    {
        var h = await Harness.CreateAsync(b =>
        {
            b.Intents.FirstCheck = TimeSpan.FromMinutes(1);
            b.Intents.MaxCheckInterval = TimeSpan.FromMinutes(8);
            b.Intents.TimeToLive = TimeSpan.FromMinutes(30);
        });
        h.Simulator.NextPush(new SimulatedPush { Outcome = SimulatedPushOutcome.NoResponse });
        var intent = await h.InitiateAsync("push-1");

        var checks = 0;
        for (var minute = 1; minute <= 30; minute++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            checks += (await h.Payments.IntentResolver.ResolveDueAsync(Ct)).Checked;
        }

        var expired = await h.TenantPayments.Intents.GetAsync(intent.IntentId, Ct);
        Assert.Equal(PaymentIntentStatus.Expired, expired.Status);
        Assert.InRange(checks, 4, 8);   // 1, 3, 7, 15, 23, 30 minutes: doubling, capped, and a last check at expiry
        Assert.Equal(checks, expired.StatusChecks);
        Assert.Empty(await h.PaymentsAsync());
    }

    [Fact]
    public async Task A_late_confirmation_after_expiry_is_still_recorded()
    {
        var h = await Harness.CreateAsync(b => b.Intents.TimeToLive = TimeSpan.FromMinutes(10));
        h.Simulator.NextPush(new SimulatedPush { CallbackDelay = TimeSpan.FromMinutes(20), AnswerAfter = TimeSpan.FromMinutes(15), Faults = SimulatedFaults.None });
        var intent = await h.InitiateAsync("push-1");
        h.Clock.Advance(TimeSpan.FromMinutes(11));
        await h.Payments.IntentResolver.ResolveDueAsync(Ct);
        Assert.Equal(PaymentIntentStatus.Expired, (await h.TenantPayments.Intents.GetAsync(intent.IntentId, Ct)).Status);

        h.Clock.Advance(TimeSpan.FromMinutes(30));
        await h.DeliverAndProcessAsync();

        Assert.Equal(PaymentIntentStatus.Confirmed, (await h.TenantPayments.Intents.GetAsync(intent.IntentId, Ct)).Status);
        Assert.Single(await h.PaymentsAsync());
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_collection_is_refused()
    {
        var h = await Harness.CreateAsync();
        await h.InitiateAsync("push-1", amount: 400m);

        var error = await Assert.ThrowsAsync<RuleViolationException>(() => h.InitiateAsync("push-1", amount: 500m));

        Assert.Equal(PaymentsErrorCodes.IntentKeyReused, error.ErrorCode);
        Assert.Equal(1, h.Simulator.PushRequests);
    }

    [Fact]
    public async Task Concurrent_initiations_with_one_key_ask_the_provider_once()
    {
        var h = await Harness.CreateAsync();

        var intents = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => h.InitiateAsync("push-1"), Ct)));

        Assert.Single(intents.Select(i => i.IntentId).Distinct());
        Assert.Equal(1, h.Simulator.PushRequests);
    }

    [Fact]
    public async Task A_collection_needs_a_known_target_and_a_provider_that_supports_it()
    {
        var h = await Harness.CreateAsync();
        Task Initiate(PaymentTarget target, string provider = "sim") => h.TenantPayments.Intents.InitiateAsync(new InitiateCollectionRequest
        {
            IdempotencyKey = Guid.NewGuid().ToString(), Provider = provider, ProviderAccount = Harness.Paybill, Payer = "254712345678",
            Amount = Money.Kes(100m), Target = target,
        }, Ct);

        Assert.Equal(PaymentsErrorCodes.InvalidIntentTarget, (await Assert.ThrowsAsync<RuleViolationException>(() => Initiate(PaymentTarget.Unknown))).ErrorCode);
        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => Initiate(PaymentTarget.ForObligation(ObligationRef.ByReference("NOPE"))))).ErrorCode);
        Assert.Equal(PaymentsErrorCodes.UnknownProvider, (await Assert.ThrowsAsync<RuleViolationException>(() => Initiate(PaymentTarget.ForCustomer(AccountRef.ById(h.Customer)), "airtel"))).ErrorCode);
        Assert.Equal(0, h.Simulator.PushRequests);
    }

    [Fact]
    public async Task Intents_of_one_tenant_are_invisible_to_another()
    {
        var h = await Harness.CreateAsync();
        var intent = await h.InitiateAsync("push-1");

        await Assert.ThrowsAsync<NotFoundException>(() => h.Payments.ForTenant(Harness.Other).Intents.GetAsync(intent.IntentId, Ct));
        Assert.Null(await h.Payments.ForTenant(Harness.Other).Intents.FindAsync("push-1", Ct));
    }
}
