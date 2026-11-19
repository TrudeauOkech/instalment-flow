using CsCheck;
using InstalmentFlow.Domain;

namespace InstalmentFlow.Tests;

public sealed class InstalmentEvaluatorTests
{
    private static readonly DateOnly s_due = new(2026, 12, 1);
    private static readonly GracePeriod s_twoDays = GracePeriod.OfDays(2);

    public static TheoryData<int, InstalmentTiming, int> TimingByDayOffset => new()
    {
        { -1, InstalmentTiming.Upcoming, 0 },
        { 0, InstalmentTiming.Due, 0 },
        { 1, InstalmentTiming.InGrace, 1 },
        { 2, InstalmentTiming.InGrace, 2 },   // last grace day
        { 3, InstalmentTiming.Overdue, 3 },   // DPD counts from the due date, not the end of grace
        { 30, InstalmentTiming.Overdue, 30 },
    };

    [Theory]
    [MemberData(nameof(TimingByDayOffset))]
    public void Timing_and_days_past_due_across_the_grace_boundary(int dayOffset, InstalmentTiming timing, int daysPastDue)
    {
        var instalment = Instalment(due: 500m, allocated: 100m);
        var asOf = s_due.AddDays(dayOffset);

        Assert.Equal(timing, InstalmentEvaluator.Timing(instalment, asOf, s_twoDays));
        Assert.Equal(daysPastDue, InstalmentEvaluator.DaysPastDue(instalment, asOf));
    }

    public static TheoryData<decimal, decimal, InstalmentPaymentState> PaymentStates => new()
    {
        { 0m, 0m, InstalmentPaymentState.Unpaid },
        { 100m, 0m, InstalmentPaymentState.PartiallyPaid },
        { 0m, 100m, InstalmentPaymentState.PartiallyPaid },
        { 500m, 0m, InstalmentPaymentState.Paid },
        { 450m, 50m, InstalmentPaymentState.Paid },
        { 0m, 500m, InstalmentPaymentState.Waived },
    };

    [Theory]
    [MemberData(nameof(PaymentStates))]
    public void Payment_state(decimal allocated, decimal waived, InstalmentPaymentState expected)
    {
        Assert.Equal(expected, InstalmentEvaluator.PaymentState(Instalment(500m, allocated, waived)));
    }

    [Fact]
    public void Scenario_4_partial_payments_accumulate_to_settle_the_instalment()
    {
        // KES 100 + 150 + 50 + 200 against a KES 500 instalment.
        var running = 0m;
        var states = new List<InstalmentPaymentState>();
        foreach (var payment in new[] { 100m, 150m, 50m, 200m })
        {
            running += payment;
            states.Add(InstalmentEvaluator.PaymentState(Instalment(500m, running)));
        }

        Assert.Equal([InstalmentPaymentState.PartiallyPaid, InstalmentPaymentState.PartiallyPaid, InstalmentPaymentState.PartiallyPaid, InstalmentPaymentState.Paid], states);
        Assert.Equal(InstalmentTiming.Settled, InstalmentEvaluator.Timing(Instalment(500m, running), s_due.AddDays(10), s_twoDays));
        Assert.Equal(0, InstalmentEvaluator.DaysPastDue(Instalment(500m, running), s_due.AddDays(10)));
    }

    [Fact]
    public void Paid_through_date_is_the_end_of_the_unbroken_covered_run()
    {
        var instalments = new[]
        {
            Instalment(50m, 50m, sequence: 1),
            Instalment(50m, 25m, 25m, sequence: 2),
            Instalment(50m, 10m, sequence: 3),
            Instalment(50m, 50m, sequence: 4),   // paid, but after a gap
        };

        Assert.Equal(s_due.AddDays(1), InstalmentEvaluator.PaidThroughDate(instalments));
        Assert.Null(InstalmentEvaluator.PaidThroughDate([Instalment(50m, 0m, sequence: 1), Instalment(50m, 50m, sequence: 2)]));
        Assert.Equal(s_due.AddDays(1), InstalmentEvaluator.PaidThroughDate(instalments.Reverse()));
    }

    [Fact]
    public void Property_status_is_consistent_with_balances_and_dates()
    {
        var gen =
            from due in Gen.Int[1, 10_000]
            from allocated in Gen.Int[0, 10_000]
            from waived in Gen.Int[0, 10_000]
            from offset in Gen.Int[-400, 400]
            from grace in Gen.Int[0, 30]
            where allocated + waived <= due
            select (Due: (decimal)due, Allocated: (decimal)allocated, Waived: (decimal)waived, Offset: offset, Grace: GracePeriod.OfDays(grace));

        gen.Sample(x =>
        {
            var instalment = Instalment(x.Due, x.Allocated, x.Waived);
            var asOf = s_due.AddDays(x.Offset);
            var timing = InstalmentEvaluator.Timing(instalment, asOf, x.Grace);
            var state = InstalmentEvaluator.PaymentState(instalment);
            var dpd = InstalmentEvaluator.DaysPastDue(instalment, asOf);
            var outstanding = instalment.Outstanding.IsPositive;

            Assert.Equal(!outstanding, timing == InstalmentTiming.Settled);
            Assert.Equal(!outstanding, state is InstalmentPaymentState.Paid or InstalmentPaymentState.Waived);
            Assert.Equal(outstanding && x.Offset > x.Grace.Days, timing == InstalmentTiming.Overdue);
            Assert.Equal(outstanding && x.Offset > 0 ? x.Offset : 0, dpd);
            Assert.True(dpd == 0 || timing is InstalmentTiming.InGrace or InstalmentTiming.Overdue);
        });
    }

    internal static Instalment Instalment(decimal due, decimal allocated = 0m, decimal waived = 0m, int sequence = 1) =>
        new(sequence, s_due.AddDays(sequence - 1), [new InstalmentComponent(ComponentKey.Principal, Money.Kes(due), Money.Kes(allocated), Money.Kes(waived))]);
}
