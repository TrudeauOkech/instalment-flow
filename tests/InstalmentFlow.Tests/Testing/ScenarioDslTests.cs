using InstalmentFlow.Testing;

namespace InstalmentFlow.Tests;

/// <summary>The <c>InstalmentFlow.Testing</c> scenario DSL, exercised the way a host's test suite would use it.</summary>
public sealed class ScenarioDslTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ambiguous_reference_is_held_not_allocated()
    {
        var run = await Scenario.InMemory(tenant: "t1")
            .Given(a => a.Customer("C1").Loan("LN-100001", Plan.Daily(500m, count: 1)))
            .Given(a => a.Customer("C2").Loan("LN-100002", Plan.Daily(500m, count: 1)))
            .Given(a => a.Resolver(r => r.Map("LN-10000", "LN-100001", "LN-100002")))   // fuzzy legacy reference
            .When(a => a.PaybillPayment(500m, billRef: "LN-10000", transId: "TX1"))
            .Then(a => a.Payment("TX1").Outcome.ShouldBe(RecordOutcome.Unidentified))
            .Then(a => a.OpenException("TX1").Reason.ShouldBe(PaymentExceptionReason.AmbiguousReference))
            .Then(a => a.Loan("LN-100001").Outstanding.ShouldBe(Money.Kes(500m)))
            .RunAsync(Ct);

        await run.ShouldSatisfyAllInvariantsAsync();
    }

    [Fact]
    public async Task Duplicate_deliveries_reversal_and_refund_read_like_the_story()
    {
        var run = await Scenario.InMemory()
            .Given(a => a.Customer("C1").Loan("LN-100001", Plan.Instalments(300m, 300m)))
            .When(a => a.PaybillPaymentDelivered(times: 5, 450m, billRef: "LN-100001", transId: "TX1"))
            .Then(a => a.Payment("TX1").Outcome.ShouldBe(RecordOutcome.Duplicate))
            .Then(a => a.Unapplied("C1").ShouldBe(Money.Kes(150m)))
            .When(a => a.Refund("overpaid", "C1", 150m).RefundCompleted("overpaid", RefundCompletion.Confirmed))
            .Then(a => a.Refund("overpaid").Status.ShouldBe(RefundStatus.Confirmed))
            .When(a => a.DaysPass(1).Reversal("TX1"))
            .Then(a => a.Reversal("TX1").Deficit.ShouldBe(Money.Kes(150m)))
            .Then(a => a.Loan("LN-100001").Outstanding.ShouldBe(Money.Kes(600m)))
            .RunAsync(Ct);

        await run.ShouldSatisfyAllInvariantsAsync();
        Assert.Equal(new DateOnly(2026, 12, 2), run.Clock.Today);
    }

    [Fact]
    public async Task A_failing_check_says_what_differed()
    {
        var scenario = Scenario.InMemory()
            .Given(a => a.Customer("C1").Loan("LN-100001", Plan.Once(500m)))
            .When(a => a.PaybillPayment(200m, billRef: "LN-100001", transId: "TX1"))
            .Then(a => a.Loan("LN-100001").Outstanding.ShouldBe(Money.Kes(0m)));

        var error = await Assert.ThrowsAsync<ScenarioAssertionException>(() => scenario.RunAsync(Ct));

        Assert.Equal("Expected KES 0.00, but was KES 300.00.", error.Message);
    }

    [Fact]
    public async Task Charges_waivers_and_held_funds_can_be_scripted()
    {
        var run = await Scenario.InMemory(policy: p => p with { ChargeDue = ChargeDue.Immediately })
            .Given(a => a.Customer("C1").Loan("LN-100001", Plan.Weekly(1_000m, count: 4)))
            .When(a => a.Charge("late", "LN-100001", ChargeType.Penalty, 50m, chargeCode: "late"))
            .When(a => a.Waive("LN-100001", new ComponentKey(ComponentType.Penalty, "late"), 20m))
            .When(a => a.CustomerPayment("C1", 1_030m, transId: "TX1"))
            .Then(a => a.Payment("TX1").Exception!.Reason.ShouldBe(PaymentExceptionReason.AllocationInstructionRequired))
            .When(a => a.Do(flow => flow.Exceptions.AllocateToObligationAsync(new AllocateToObligationRequest
            {
                IdempotencyKey = "resolve", ExceptionId = Exceptional(flow), Obligation = ObligationRef.ByReference("LN-100001"), ResolvedBy = "ops",
            })))
            .Then(a => a.Loan("LN-100001").Arrears.ShouldBe(Money.Kes(0m)))
            .Then(a => a.Loan("LN-100001").Outstanding.ShouldBe(Money.Kes(3_000m)))
            .RunAsync(Ct);

        await run.ShouldSatisfyAllInvariantsAsync();
    }

    private static PaymentExceptionId Exceptional(IInstalmentFlow flow) =>
        flow.Exceptions.ListOpenAsync().ToListAsync().AsTask().GetAwaiter().GetResult().Single().ExceptionId;
}
