namespace InstalmentFlow;

/// <summary>Equal instalments with the rounding remainder on the last one, plus an optional deposit.</summary>
internal sealed class EqualInstalmentsGenerator : IScheduleGenerator
{
    internal const int MaxCount = 10_000;

    private readonly Frequency _frequency;
    private readonly int _count;
    private readonly DateOnly _firstDue;
    private readonly Deposit? _deposit;

    public EqualInstalmentsGenerator(Frequency frequency, int count, DateOnly firstDue, Deposit? deposit)
    {
        ArgumentNullException.ThrowIfNull(frequency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxCount);
        if (deposit is not null && deposit.DueDate > firstDue)
        {
            throw new RuleViolationException(ErrorCodes.InvalidSchedule, "The deposit must fall due on or before the first instalment.");
        }

        _frequency = frequency;
        _count = count;
        _firstDue = firstDue;
        _deposit = deposit;
    }

    public string Name => "equal";

    public IReadOnlyList<PlannedInstalment> Generate(ScheduleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var remaining = context.Components.ToList();
        var instalments = new List<PlannedInstalment>(_count + 1);

        if (_deposit is not null)
        {
            var principalIndex = remaining.FindIndex(c => c.Key == ComponentKey.Principal);
            if (principalIndex < 0 || _deposit.Amount >= remaining[principalIndex].Amount)
            {
                throw new RuleViolationException(ErrorCodes.InvalidSchedule, "A deposit must be less than the principal.");
            }

            instalments.Add(new PlannedInstalment(_deposit.DueDate, [Component.Principal(_deposit.Amount)]));
            remaining[principalIndex] = Component.Principal(remaining[principalIndex].Amount - _deposit.Amount);
        }

        var shares = remaining.Select(c => (c.Key, Amounts: SplitRemainderLast(c.Amount, _count))).ToList();
        for (var i = 0; i < _count; i++)
        {
            var amounts = shares
                .Where(s => s.Amounts[i].IsPositive)
                .Select(s => new Component(s.Key, s.Amounts[i]))
                .ToList();
            instalments.Add(new PlannedInstalment(_frequency.DueDate(_firstDue, i), amounts));
        }

        return instalments;
    }

    /// <summary>Equal shares rounded down to the minor unit; the last share takes the remainder.</summary>
    internal static IReadOnlyList<Money> SplitRemainderLast(Money total, int count)
    {
        var share = Money.Round(total.Amount / count, total.Currency, MidpointRounding.ToZero);
        var last = total - new Money(share.Amount * (count - 1), total.Currency);
        return [.. Enumerable.Repeat(share, count - 1), last];
    }
}

/// <summary>A schedule supplied instalment by instalment.</summary>
internal sealed class CustomScheduleGenerator : IScheduleGenerator
{
    private readonly IReadOnlyList<PlannedInstalment> _instalments;

    public CustomScheduleGenerator(IEnumerable<PlannedInstalment> instalments)
    {
        ArgumentNullException.ThrowIfNull(instalments);
        _instalments = instalments.ToList();
    }

    public string Name => "custom";

    public IReadOnlyList<PlannedInstalment> Generate(ScheduleContext context) => _instalments;
}
