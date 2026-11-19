using CsCheck;
using InstalmentFlow.Domain;

namespace InstalmentFlow.Tests;

public sealed class FrequencyTests
{
    private static readonly DateOnly s_jan31 = new(2027, 1, 31);

    [Fact]
    public void Daily_weekly_and_every_n_days()
    {
        var start = new DateOnly(2026, 12, 1);

        Assert.Equal(new DateOnly(2026, 12, 4), Frequency.Daily.DueDate(start, 3));
        Assert.Equal(new DateOnly(2026, 12, 22), Frequency.Weekly.DueDate(start, 3));
        Assert.Equal(new DateOnly(2026, 12, 10), Frequency.EveryDays(3).DueDate(start, 3));
        Assert.Equal(new DateOnly(2026, 12, 29), Frequency.EveryWeeks(2).DueDate(start, 2));
    }

    [Fact]
    public void Monthly_from_month_end_clamps_without_drifting()
    {
        Assert.Equal(
            [new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 31), new DateOnly(2027, 4, 30)],
            Enumerable.Range(0, 4).Select(i => Frequency.Monthly.DueDate(s_jan31, i)));
    }

    [Fact]
    public void Monthly_respects_leap_years()
    {
        Assert.Equal(new DateOnly(2028, 2, 29), Frequency.Monthly.DueDate(new DateOnly(2028, 1, 31), 1));
        Assert.Equal(new DateOnly(2027, 2, 28), Frequency.Monthly.DueDate(new DateOnly(2027, 1, 29), 1));
        Assert.Equal(new DateOnly(2029, 2, 28), Frequency.EveryMonths(12).DueDate(new DateOnly(2028, 2, 29), 1));
    }

    [Fact]
    public void Invalid_intervals_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Frequency.EveryDays(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Frequency.EveryMonths(367));
        Assert.Throws<ArgumentOutOfRangeException>(() => Frequency.Daily.DueDate(s_jan31, -1));
        Assert.Equal("every 3 days", Frequency.EveryDays(3).ToString());
        Assert.Equal("monthly", Frequency.Monthly.ToString());
    }
}

public sealed class ScheduleBuilderTests
{
    private static readonly DateOnly s_dec1 = new(2026, 12, 1);
    private static readonly DateTimeOffset s_now = new(2026, 11, 18, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UC01_phone_financing_deposit_plus_270_daily_instalments_of_50()
    {
        var plan = Build(
            PlanSpec.Equal(Frequency.Daily, 270, s_dec1, deposit: new Deposit(Money.Kes(1_500m), new DateOnly(2026, 11, 18))),
            Component.Principal(Money.Kes(15_000m)));

        Assert.Equal(271, plan.Instalments.Length);
        Assert.Equal(Money.Kes(1_500m), plan.Instalments[0].Due);
        Assert.Equal(new DateOnly(2026, 11, 18), plan.Instalments[0].DueDate);
        Assert.All(plan.Instalments.Skip(1), i => Assert.Equal(Money.Kes(50m), i.Due));
        Assert.Equal(s_dec1, plan.Instalments[1].DueDate);
        Assert.Equal(s_dec1.AddDays(269), plan.Instalments[^1].DueDate);
        Assert.Equal(Enumerable.Range(1, 271), plan.Instalments.Select(i => i.Sequence));
        Assert.Equal("equal", plan.Generator);
    }

    [Fact]
    public void Rounding_remainder_goes_to_the_last_instalment()
    {
        var plan = Build(PlanSpec.Equal(Frequency.Monthly, 3, s_dec1), Component.Principal(Money.Kes(1_000m)));

        Assert.Equal([Money.Kes(333.33m), Money.Kes(333.33m), Money.Kes(333.34m)], plan.Instalments.Select(i => i.Due));
    }

    [Fact]
    public void Each_component_is_split_separately()
    {
        var plan = Build(
            PlanSpec.Equal(Frequency.Monthly, 4, s_dec1),
            Component.Principal(Money.Kes(10_000m)),
            Component.Interest(Money.Kes(500m)),
            Component.Fee(Money.Kes(200m), "insurance"));

        var first = plan.Instalments[0].Components;
        Assert.Equal(
            [(ComponentKey.Principal, Money.Kes(2_500m)), (ComponentKey.Interest, Money.Kes(125m)), (new ComponentKey(ComponentType.Fee, "insurance"), Money.Kes(50m))],
            first.Select(c => (c.Key, c.Due)));
        Assert.Equal(Money.Kes(10_700m), Money.Sum(Currency.KES, plan.Instalments.Select(i => i.Due)));
    }

    [Fact]
    public void Too_many_instalments_for_a_small_amount_is_rejected()
    {
        // UGX 100 over 270 instalments: 269 instalments would have nothing due.
        var ex = Assert.Throws<RuleViolationException>(() =>
            Build(PlanSpec.Equal(Frequency.Daily, 270, s_dec1), Component.Principal(new Money(100m, Currency.UGX))));

        Assert.Equal(ErrorCodes.InvalidSchedule, ex.ErrorCode);
    }

    [Fact]
    public void Deposit_rules()
    {
        Assert.Equal(ErrorCodes.InvalidSchedule, Assert.Throws<RuleViolationException>(() =>
            Build(PlanSpec.Equal(Frequency.Daily, 10, s_dec1, deposit: new Deposit(Money.Kes(1_000m), s_dec1)), Component.Principal(Money.Kes(1_000m)))).ErrorCode);
        Assert.Equal(ErrorCodes.InvalidSchedule, Assert.Throws<RuleViolationException>(() =>
            PlanSpec.Equal(Frequency.Daily, 10, s_dec1, deposit: new Deposit(Money.Kes(100m), s_dec1.AddDays(1)))).ErrorCode);
    }

    [Fact]
    public void Custom_schedule_totals_must_equal_the_obligation_I13()
    {
        var spec = PlanSpec.Custom(
        [
            new PlannedInstalment(s_dec1, [Component.Principal(Money.Kes(600m))]),
            new PlannedInstalment(s_dec1.AddMonths(1), [Component.Principal(Money.Kes(300m))]),
        ]);

        var ex = Assert.Throws<RuleViolationException>(() => Build(spec, Component.Principal(Money.Kes(1_000m))));

        Assert.Equal(ErrorCodes.InvalidSchedule, ex.ErrorCode);
        Assert.Contains("I-13", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_schedule_rejects_components_the_obligation_does_not_owe()
    {
        var spec = PlanSpec.Custom([new PlannedInstalment(s_dec1, [Component.Principal(Money.Kes(1_000m)), Component.Fee(Money.Kes(10m))])]);

        Assert.Throws<RuleViolationException>(() => Build(spec, Component.Principal(Money.Kes(1_000m))));
    }

    [Fact]
    public void Custom_schedule_rejects_decreasing_dates_duplicates_and_empty_instalments()
    {
        var principal = Component.Principal(Money.Kes(1_000m));
        Assert.Throws<RuleViolationException>(() => Build(PlanSpec.Custom(
        [
            new PlannedInstalment(s_dec1.AddDays(1), [Component.Principal(Money.Kes(500m))]),
            new PlannedInstalment(s_dec1, [Component.Principal(Money.Kes(500m))]),
        ]), principal));
        Assert.Throws<RuleViolationException>(() => Build(PlanSpec.Custom(
            [new PlannedInstalment(s_dec1, [Component.Principal(Money.Kes(500m)), Component.Principal(Money.Kes(500m))])]), principal));
        Assert.Throws<RuleViolationException>(() => Build(PlanSpec.Custom(
            [new PlannedInstalment(s_dec1, []), new PlannedInstalment(s_dec1, [principal])]), principal));
        Assert.Throws<RuleViolationException>(() => Build(PlanSpec.Custom([]), principal));
    }

    [Fact]
    public void Currency_of_every_instalment_must_match()
    {
        var spec = PlanSpec.Custom([new PlannedInstalment(s_dec1, [Component.Principal(Money.Of(1_000m, "USD"))])]);

        Assert.Throws<CurrencyMismatchException>(() => Build(spec, Component.Principal(Money.Kes(1_000m))));
    }

    [Fact]
    public void Business_calendar_moves_due_dates()
    {
        var plan = Build(PlanSpec.Equal(Frequency.Daily, 7, new DateOnly(2026, 12, 4)), [Component.Principal(Money.Kes(700m))], new WeekdayCalendar());

        // Fri 4 Dec; Sat 5 and Sun 6 move to Mon 7 alongside Mon 7 itself.
        Assert.Equal(
            [new DateOnly(2026, 12, 4), new DateOnly(2026, 12, 7), new DateOnly(2026, 12, 7), new DateOnly(2026, 12, 7)],
            plan.Instalments.Take(4).Select(i => i.DueDate));
    }

    [Fact]
    public void Plan_without_explicit_grace_uses_the_default()
    {
        var plan = ScheduleBuilder.Build(1, PlanSpec.Equal(Frequency.Daily, 1, s_dec1), [Component.Principal(Money.Kes(1m))], Currency.KES, GracePeriod.OfDays(3), BusinessCalendars.None, s_now);
        var explicitGrace = ScheduleBuilder.Build(1, PlanSpec.Equal(Frequency.Daily, 1, s_dec1, GracePeriod.OfDays(1)), [Component.Principal(Money.Kes(1m))], Currency.KES, GracePeriod.OfDays(3), BusinessCalendars.None, s_now);

        Assert.Equal(3, plan.Grace.Days);
        Assert.Equal(1, explicitGrace.Grace.Days);
    }

    [Fact]
    public void Property_equal_schedules_preserve_totals_count_order_and_remainder_last()
    {
        var gen =
            from currency in Gen.OneOf(Currency.KES, Currency.UGX)
            from minorUnits in Gen.Long[1_000L, 100_000_000L]
            from count in Gen.Int[1, 400]
            from unit in Gen.OneOf(Frequency.Daily, Frequency.Weekly, Frequency.Monthly, Frequency.EveryDays(3))
            where minorUnits >= count
            select (Total: new Money(minorUnits / Scale(currency), currency), Count: count, Frequency: unit);

        gen.Sample(x =>
        {
            var plan = Build(PlanSpec.Equal(x.Frequency, x.Count, s_dec1), Component.Principal(x.Total));
            var dues = plan.Instalments.Select(i => i.Due).ToList();

            Assert.Equal(x.Count, plan.Instalments.Length);
            Assert.Equal(x.Total, Money.Sum(x.Total.Currency, dues));
            Assert.True(plan.Instalments.Zip(plan.Instalments.Skip(1)).All(p => p.First.DueDate < p.Second.DueDate));
            Assert.All(dues.Take(dues.Count - 1), d => Assert.Equal(dues[0], d));
            Assert.True(dues[^1] >= dues[0]);
            Assert.True(dues[^1].Amount - dues[0].Amount < x.Count / Scale(x.Total.Currency));
        });
    }

    private static PlanVersion Build(PlanSpec spec, params Component[] components) =>
        Build(spec, components, BusinessCalendars.None);

    private static PlanVersion Build(PlanSpec spec, Component[] components, IBusinessCalendar calendar) =>
        ScheduleBuilder.Build(1, spec, components, components[0].Amount.Currency, GracePeriod.None, calendar, s_now);

    private static decimal Scale(Currency currency) => currency.MinorUnits == 0 ? 1m : 100m;

    private sealed class WeekdayCalendar : IBusinessCalendar
    {
        public DateOnly AdjustDueDate(DateOnly scheduledDate) => scheduledDate.DayOfWeek switch
        {
            DayOfWeek.Saturday => scheduledDate.AddDays(2),
            DayOfWeek.Sunday => scheduledDate.AddDays(1),
            _ => scheduledDate,
        };
    }
}
