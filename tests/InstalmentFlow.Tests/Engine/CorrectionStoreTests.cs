using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Tests;

/// <summary>
/// The store's own guards for corrections. Commands check first, but parking a reversal and confirming a refund
/// touch no aggregate version, so under concurrency only the store's atomic checks stand between them and a
/// double posting.
/// </summary>
public sealed class CorrectionStoreTests
{
    private static readonly DateOnly s_today = Flows.Today;

    private static CancellationToken Ct => Flows.Ct;

    [Fact]
    public async Task Concurrent_parking_of_one_reversal_is_recorded_once()
    {
        var f = new Flows();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => f.ReverseAsync("TX1", 100m, reversalRef: "RV1", key: $"k{i}"))));

        Assert.Single(results, r => r.Outcome == ReversalOutcome.Parked);
        Assert.Single(f.Snapshot.PendingReversals);
    }

    [Fact]
    public async Task Concurrent_completions_of_one_refund_complete_it_once()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 700m, "LN100001");
        var refund = await f.Flow.Refunds.RequestAsync(new RequestRefundRequest
        {
            IdempotencyKey = "r", Account = customer, Amount = Money.Kes(200m), Reason = "Overpayment", RequestedBy = "ops",
        }, Ct);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                await f.Flow.Refunds.CompleteAsync(new CompleteRefundRequest
                {
                    IdempotencyKey = $"c{i}", Refund = refund.Refund.RefundId, Completion = RefundCompletion.Confirmed,
                }, Ct);
                return null;
            }
            catch (RuleViolationException ex)
            {
                return ex.ErrorCode;
            }
        })));

        Assert.Single(attempts, a => a is null);
        Assert.All(attempts.Where(a => a is not null), a => Assert.Equal(ErrorCodes.RefundNotPending, a));
        f.AssertInvariants();
    }

    [Fact]
    public async Task The_store_rejects_a_reversal_reference_that_is_already_recorded()
    {
        var f = new Flows();
        await f.ReverseAsync("TX1", 100m, reversalRef: "RV1");

        var commit = new Commit { Tenant = Flows.Tenant, NewPendingReversals = [Pending("TX2", "RV1")] };

        await Assert.ThrowsAsync<DuplicateReversalException>(() => Store(f).CommitAsync(commit, Ct).AsTask());
    }

    [Fact]
    public async Task The_store_never_lets_a_payment_and_its_parked_reversal_miss_each_other()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        var recorded = await f.PayAsync("TX1", 100m, "LN100001");
        await f.ReverseAsync("TX9", 50m);

        // Parking against a payment that is already recorded…
        var late = new Commit { Tenant = Flows.Tenant, NewPendingReversals = [Pending("TX1", null)] };
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Store(f).CommitAsync(late, Ct).AsTask());

        // …and recording a payment without applying the reversal parked for it are both refused.
        var payment = f.Snapshot.Payments.Single(p => p.Id == recorded.PaymentId) with { Id = new PaymentId(Guid.CreateVersion7()), Source = Flows.Source("TX9") };
        var missed = new Commit { Tenant = Flows.Tenant, Payments = [payment] };
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Store(f).CommitAsync(missed, Ct).AsTask());
    }

    [Fact]
    public async Task The_store_keeps_the_refund_state_machine_and_reverses_a_charge_once()
    {
        var f = new Flows();
        var customer = await f.CustomerAsync("C-1");
        await f.LoanAsync(customer, "LN100001", s_today, 500m);
        await f.PayAsync("TX1", 700m, "LN100001");
        var refund = await f.Flow.Refunds.RequestAsync(new RequestRefundRequest
        {
            IdempotencyKey = "r", Account = customer, Amount = Money.Kes(200m), Reason = "Overpayment", RequestedBy = "ops",
        }, Ct);
        await f.Flow.Refunds.CompleteAsync(new CompleteRefundRequest { IdempotencyKey = "c", Refund = refund.Refund.RefundId, Completion = RefundCompletion.Confirmed }, Ct);
        var charge = await f.Flow.Charges.PostAsync(new PostChargeRequest
        {
            IdempotencyKey = "ch", Obligation = ObligationRef.ByReference("LN100001"), Type = ChargeType.Fee, Amount = Money.Kes(10m), Description = "Fee",
        }, Ct);
        await f.Flow.Charges.ReverseAsync(new ReverseChargeRequest { IdempotencyKey = "rc", Charge = charge.ChargeId, Reason = "Error" }, Ct);

        var stored = f.Snapshot.Refunds.Single();
        var refail = new Commit { Tenant = Flows.Tenant, Refunds = [stored with { Status = RefundStatus.Failed }] };
        var rerequest = new Commit { Tenant = Flows.Tenant, Refunds = [stored with { Status = RefundStatus.Requested }] };
        var rereverse = new Commit { Tenant = Flows.Tenant, ChargeReversals = [new ChargeReversalRecord(charge.ChargeId, "again", new JournalTxId(Guid.CreateVersion7()), DateTimeOffset.UnixEpoch)] };

        Assert.Equal(ErrorCodes.RefundNotPending, (await Assert.ThrowsAsync<RuleViolationException>(() => Store(f).CommitAsync(refail, Ct).AsTask())).ErrorCode);
        Assert.Equal(ErrorCodes.RefundNotPending, (await Assert.ThrowsAsync<RuleViolationException>(() => Store(f).CommitAsync(rerequest, Ct).AsTask())).ErrorCode);
        Assert.Equal(ErrorCodes.InvalidRequest, (await Assert.ThrowsAsync<RuleViolationException>(() => Store(f).CommitAsync(rereverse, Ct).AsTask())).ErrorCode);
        f.AssertInvariants();
    }

    private static ILedgerStore Store(Flows f) => f.Factory.LedgerStore;

    private static PendingReversal Pending(string paymentTransId, string? reversalRef) =>
        new(new ReversalId(Guid.CreateVersion7()), Flows.Source(paymentTransId),
            reversalRef is null ? null : new ExternalReference("mpesa:600123", reversalRef), Money.Kes(1m), "test", DateTimeOffset.UnixEpoch);
}
