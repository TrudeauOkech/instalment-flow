using CsCheck;

namespace InstalmentFlow.Tests;

/// <summary>Payment reversals (ADR-005, ADR-010): unwinding, deficits, duplicates, parking and invariants I-8 and I-9.</summary>
public sealed class ReversalFlowTests
{
    private static readonly DateOnly s_today = Flows.Today;

    private static CancellationToken Ct => Flows.Ct;

    // ----- The brief's scenario -----

    [Fact]
    public async Task Scenario3_reversing_500_after_settlement_restores_500_outstanding_with_history_intact()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 500m, "LN100001");
        Assert.Equal(ObligationStatus.Settled, (await f.LoanViewAsync("LN100001")).Status);

        var result = await f.ReverseAsync("TX1");

        Assert.Equal(ReversalOutcome.Reversed, result.Outcome);
        Assert.Equal((Money.Kes(500m), Money.Kes(0m), Money.Kes(500m), Money.Kes(0m)), (result.Amount, result.FromUnapplied, result.Unwound, result.Deficit));
        var loan = await f.LoanViewAsync("LN100001");
        Assert.Equal((ObligationStatus.Active, Money.Kes(500m)), (loan.Status, loan.Outstanding));
        Assert.Equal(InstalmentPaymentState.Unpaid, loan.Instalments[0].PaymentState);
        Assert.Null(loan.PaidThroughDate);

        // History is intact: the allocation and its reversal are both facts; nothing was edited away.
        var snapshot = f.Snapshot;
        Assert.Equal(Money.Kes(500m), Assert.Single(snapshot.Allocations).Amount);
        Assert.Equal(Money.Kes(500m), Assert.Single(snapshot.AllocationReversals).Amount);
        Assert.Contains(snapshot.Events, e => e.Type == "PaymentReversed");
        var payment = await f.Flow.Queries.GetPaymentAsync(PaymentRef.BySource(Flows.Source("TX1")), Ct);
        Assert.Equal((Money.Kes(0m), Money.Kes(0m), Money.Kes(500m)), (payment.Allocated, payment.Unapplied, payment.Reversed));
        f.AssertInvariants();
    }

    // ----- Partial reversals -----

    public static TheoryData<int, int, int, int> PartialReversals => new()
    {
        // KES 700 received against KES 500 due: 500 allocated, 200 unapplied.
        // reverse, from unapplied, unwound, outstanding afterwards
        { 150, 150, 0, 0 },
        { 200, 200, 0, 0 },
        { 350, 200, 150, 150 },
        { 700, 200, 500, 500 },
    };

    [Theory]
    [MemberData(nameof(PartialReversals))]
    public async Task Partial_reversal_takes_unapplied_funds_first_then_unwinds(int reverse, int fromUnapplied, int unwound, int outstanding)
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 700m, "LN100001");

        var result = await f.ReverseAsync("TX1", reverse);

        Assert.Equal((Money.Kes(fromUnapplied), Money.Kes(unwound)), (result.FromUnapplied, result.Unwound));
        Assert.Equal(reverse == 700 ? ReversalOutcome.Reversed : ReversalOutcome.PartiallyReversed, result.Outcome);
        Assert.Equal(Money.Kes(outstanding), (await f.LoanViewAsync("LN100001")).Outstanding);
        Assert.Equal(Money.Kes(200 - fromUnapplied), await f.UnappliedAsync(customer));
        f.AssertInvariants();
    }

    [Fact]
    public async Task Allocations_are_unwound_most_recent_first()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today.AddDays(-2), 100m, 100m, 100m);
        await f.PayAsync("TX1", 250m, "LN100001");   // #1 100, #2 100, #3 50

        var result = await f.ReverseAsync("TX1", 120m);

        Assert.Equal([(3, 50m), (2, 70m)], result.Unwinds.Select(u => (u.InstalmentSequence, u.Amount.Amount)));
        var loan = await f.LoanViewAsync("LN100001");
        Assert.Equal([0m, 70m, 100m], loan.Instalments.Select(i => i.Outstanding.Amount));
        Assert.Equal(s_today.AddDays(-2), loan.PaidThroughDate);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Repeated_partial_reversals_continue_where_the_last_one_stopped()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today.AddDays(-1), 100m, 100m);
        await f.PayAsync("TX1", 200m, "LN100001");

        await f.ReverseAsync("TX1", 30m);
        await f.ReverseAsync("TX1", 100m);
        var last = await f.ReverseAsync("TX1");

        Assert.Equal((ReversalOutcome.Reversed, Money.Kes(70m)), (last.Outcome, last.Amount));
        Assert.Equal([(1, 70m)], last.Unwinds.Select(u => (u.InstalmentSequence, u.Amount.Amount)));
        Assert.Equal(Money.Kes(200m), (await f.LoanViewAsync("LN100001")).Outstanding);
        f.AssertInvariants();
    }

    // ----- Re-allocation (doc 02 §4) -----

    [Theory]
    [InlineData(true, 50, 50)]
    [InlineData(false, 0, 100)]
    public async Task Reopened_instalments_are_paid_from_other_unapplied_funds_when_the_policy_allows(bool reallocate, int reallocated, int outstanding)
    {
        var f = new Flows(p => p with { ReallocateAfterReversal = reallocate });
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 100m, 100m);
        f.Time.Now = f.Time.Now.AddDays(1);
        await f.PayAsync("TX1", 100m, "LN100001");   // pays #1
        await f.PayAsync("TX2", 150m, "LN100001");   // pays #2; 50 held

        var result = await f.ReverseAsync("TX1");

        Assert.Equal(Money.Kes(reallocated), Money.Sum(Currency.KES, result.Reallocated.Select(l => l.Amount)));
        Assert.Equal(Money.Kes(outstanding), (await f.LoanViewAsync("LN100001")).Outstanding);
        Assert.Equal(Money.Kes(50 - reallocated), result.UnappliedAfter);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Reallocation_never_moves_funds_onto_another_loan_I21()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 100m);
        await f.LoanAsync(customer, "LN100002", s_today, 100m);
        await f.PayAsync("TX1", 100m, "LN100001");
        await f.PayAsync("TX2", 150m, "LN100002");   // 50 held, directed to LN100002

        var result = await f.ReverseAsync("TX1");

        Assert.Empty(result.Reallocated);
        Assert.Equal(Money.Kes(100m), (await f.LoanViewAsync("LN100001")).Outstanding);
        f.AssertInvariants();
    }

    // ----- Duplicates and limits -----

    [Fact]
    public async Task A_repeated_reversal_reference_is_a_duplicate_and_a_conflicting_one_is_rejected()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 500m, "LN100001");

        var first = await f.ReverseAsync("TX1", 100m, reversalRef: "RV1", key: "k1");
        var again = await f.ReverseAsync("TX1", 100m, reversalRef: "RV1", key: "k2");
        var conflict = await Assert.ThrowsAsync<RuleViolationException>(() => f.ReverseAsync("TX1", 200m, reversalRef: "RV1", key: "k3"));

        Assert.Equal((ReversalOutcome.Duplicate, first.ReversalId), (again.Outcome, again.ReversalId));
        Assert.Equal(ErrorCodes.ReversalConflict, conflict.ErrorCode);
        Assert.Single(f.Snapshot.PaymentReversals);
        Assert.Equal(Money.Kes(100m), (await f.LoanViewAsync("LN100001")).Outstanding);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_reversal_are_recorded_once()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 500m, "LN100001");

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Task.Run(() => f.ReverseAsync("TX1", 500m, reversalRef: "RV1", key: $"k{i}"))));

        Assert.Single(results, r => r.Outcome == ReversalOutcome.Reversed);
        Assert.Equal(4, results.Count(r => r.Outcome == ReversalOutcome.Duplicate));
        Assert.Single(f.Snapshot.PaymentReversals);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Reversals_can_never_exceed_the_payment_I8()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 300m, "LN100001");

        var tooMuch = await Assert.ThrowsAsync<RuleViolationException>(() => f.ReverseAsync("TX1", 301m));
        await f.ReverseAsync("TX1", 300m);
        var again = await Assert.ThrowsAsync<RuleViolationException>(() => f.ReverseAsync("TX1", 1m));

        Assert.Equal([ErrorCodes.ReversalExceedsPayment, ErrorCodes.ReversalExceedsPayment], new[] { tooMuch.ErrorCode, again.ErrorCode });
        f.AssertInvariants();
    }

    [Fact]
    public async Task Reversing_an_unknown_payment_id_is_not_found()
    {
        var f = new Flows();
        var error = await Assert.ThrowsAsync<NotFoundException>(() => f.Flow.Payments.ReverseAsync(new ReversePaymentRequest
        {
            IdempotencyKey = "r", Payment = new PaymentId(Guid.CreateVersion7()), Reason = "x",
        }, Ct));
        Assert.Equal(ErrorCodes.NotFound, error.ErrorCode);
    }

    // ----- Reversal before payment -----

    [Fact]
    public async Task A_reversal_that_arrives_first_is_parked_and_applied_when_the_payment_arrives()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);

        var parked = await f.ReverseAsync("TX1", reversalRef: "RV1");
        var again = await f.ReverseAsync("TX1", reversalRef: "RV1", key: "again");
        var recorded = await f.PayAsync("TX1", 500m, "LN100001");

        Assert.Equal((ReversalOutcome.Parked, ReversalOutcome.Duplicate), (parked.Outcome, again.Outcome));
        Assert.Equal((RecordOutcome.Reversed, Money.Kes(0m)), (recorded.Outcome, recorded.Allocated));
        Assert.Null(recorded.Exception);
        Assert.Equal(Money.Kes(500m), (await f.LoanViewAsync("LN100001")).Outstanding);
        Assert.Equal(parked.ReversalId, Assert.Single(f.Snapshot.PaymentReversals).Id);
        Assert.Empty(f.Snapshot.PendingReversals);
        Assert.Equal(ReversalOutcome.Duplicate, (await f.ReverseAsync("TX1", reversalRef: "RV1", key: "after")).Outcome);
        f.AssertInvariants();
    }

    [Fact]
    public async Task A_partial_parked_reversal_leaves_the_rest_to_be_allocated()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);

        await f.ReverseAsync("TX1", 200m);
        var recorded = await f.PayAsync("TX1", 500m, "LN100001");

        Assert.Equal((RecordOutcome.Allocated, Money.Kes(300m)), (recorded.Outcome, recorded.Allocated));
        Assert.Equal(Money.Kes(200m), (await f.LoanViewAsync("LN100001")).Outstanding);
        f.AssertInvariants();
    }

    [Fact]
    public async Task A_parked_reversal_larger_than_the_payment_is_applied_as_far_as_possible_and_raised()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);

        await f.ReverseAsync("TX1", 900m);
        var recorded = await f.PayAsync("TX1", 500m, "LN100001");

        Assert.Equal(RecordOutcome.Reversed, recorded.Outcome);
        Assert.Equal(PaymentExceptionReason.ParkedReversalNotApplied, recorded.Exception!.Reason);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Parking_and_recording_concurrently_never_lose_the_reversal()
    {
        for (var run = 0; run < 20; run++)
        {
            var f = new Flows();
            var customer = await f.CustomerAsync("C-1");
            await f.LoanAsync(customer, "LN100001", s_today, 500m);

            await Task.WhenAll(
                Task.Run(() => f.ReverseAsync("TX1", 100m, reversalRef: "RV1")),
                Task.Run(() => f.PayAsync("TX1", 500m, "LN100001")));

            Assert.Equal(Money.Kes(100m), Assert.Single(f.Snapshot.PaymentReversals).Amount);
            Assert.Empty(f.Snapshot.PendingReversals);
            Assert.Equal(Money.Kes(100m), (await f.LoanViewAsync("LN100001")).Outstanding);
            f.AssertInvariants();
        }
    }

    // ----- Unidentified funds -----

    [Fact]
    public async Task Reversing_unidentified_funds_empties_the_pool_and_lets_the_exception_be_dismissed()
    {
        var f = new Flows();
        var recorded = await f.PayAsync("TX1", 300m, "WRONG");

        var result = await f.ReverseAsync("TX1");
        var dismissed = await f.Flow.Exceptions.DismissAsync(new DismissExceptionRequest
        {
            IdempotencyKey = "dismiss", ExceptionId = recorded.Exception!.ExceptionId, Reason = "Reversed by provider", ResolvedBy = "ops",
        }, Ct);

        Assert.Equal((ReversalOutcome.Reversed, (AccountId?)null), (result.Outcome, result.AccountId));
        Assert.Equal(PaymentExceptionStatus.Dismissed, dismissed.Exception.Status);
        f.AssertInvariants();
    }

    // ----- Interleavings and properties -----

    [Fact]
    public async Task Reversals_interleaved_with_payments_and_allocations_keep_every_invariant()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today.AddDays(-9), [.. Enumerable.Repeat(100m, 20)]);
        for (var i = 0; i < 10; i++)
        {
            await f.PayAsync($"TX{i}", 70m, "LN100001");
        }

        var work = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            var n = i;
            work.Add(Task.Run(() => f.ReverseAsync($"TX{n}", n % 2 == 0 ? null : 35m)));
            work.Add(Task.Run(() => f.PayAsync($"TY{n}", 90m, "LN100001")));
            work.Add(Task.Run(() => f.Flow.Allocations.AllocateAsync(new AllocateFundsRequest { IdempotencyKey = $"sweep-{n}", Account = customer }, Ct)));
        }

        await Task.WhenAll(work);

        var loan = await f.LoanViewAsync("LN100001");
        var unapplied = await f.UnappliedAsync(customer);
        // Net money in: 10 × 70 − (5 × 70 + 5 × 35) reversed + 10 × 90 = 1,075. Applied or held, it nets off what is owed.
        Assert.Equal(Money.Kes(2_000m - 1_075m), loan.Outstanding - unapplied);
        f.AssertInvariants();
    }

    [Fact]
    public void Property_I9_receiving_then_reversing_a_payment_is_as_if_it_never_arrived()
    {
        var gen =
            from instalments in Gen.Int[1, 300].Array[1, 6]
            from before in Gen.Int[1, 400].Array[0, 4]
            from payment in Gen.Int[1, 800]
            from after in Gen.Int[1, 400].Array[0, 4]
            from reallocate in Gen.Bool
            select (instalments, before, payment, after, reallocate);

        gen.Sample(c =>
        {
            var (instalments, before, payment, after, reallocate) = c;

            // Receive P then reverse it straight away: every instalment and the credit are as they were before P.
            var with = Run(instalments, before, payment, [], reallocate);
            var without = Run(instalments, before, null, [], reallocate);
            Assert.Equal(without.Instalments, with.Instalments);
            Assert.Equal(without.Unapplied, with.Unapplied);

            // With later payments in between, the net position (outstanding less credit) is still unchanged.
            var later = Run(instalments, before, payment, after, reallocate);
            var never = Run(instalments, before, null, after, reallocate);
            Assert.Equal(never.Instalments.Sum() - never.Unapplied, later.Instalments.Sum() - later.Unapplied);
        }, iter: TestEngine.Iterations(150));
    }

    private static (decimal[] Instalments, decimal Unapplied) Run(int[] instalments, int[] before, int? payment, int[] after, bool reallocate)
    {
        var f = new Flows(p => p with { ReallocateAfterReversal = reallocate });
        var customer = f.CustomerAsync("C-1").GetAwaiter().GetResult();
        f.LoanAsync(customer, "LN100001", s_today.AddDays(-instalments.Length + 1), [.. instalments.Select(i => (decimal)i)]).GetAwaiter().GetResult();
        var n = 0;
        foreach (var amount in before)
        {
            f.PayAsync($"B{n++}", amount, "LN100001").GetAwaiter().GetResult();
        }

        if (payment is { } p)
        {
            f.PayAsync("P", p, "LN100001").GetAwaiter().GetResult();
        }

        foreach (var amount in after)
        {
            f.PayAsync($"A{n++}", amount, "LN100001").GetAwaiter().GetResult();
        }

        if (payment is not null)
        {
            f.ReverseAsync("P").GetAwaiter().GetResult();
        }

        f.AssertInvariants();
        var loan = f.LoanViewAsync("LN100001").GetAwaiter().GetResult();
        return ([.. loan.Instalments.Select(i => i.Outstanding.Amount)], f.UnappliedAsync(customer).GetAwaiter().GetResult().Amount);
    }
}
