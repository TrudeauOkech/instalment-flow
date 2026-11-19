namespace InstalmentFlow;

/// <summary>The unit of a repayment <see cref="Frequency"/>.</summary>
public enum FrequencyUnit
{
    /// <summary>Calendar days.</summary>
    Day = 1,

    /// <summary>Weeks of seven days.</summary>
    Week = 2,

    /// <summary>Calendar months. A due date that does not exist in a month moves to its last day.</summary>
    Month = 3,
}

/// <summary>How often instalments fall due: every <see cref="Interval"/> <see cref="Unit"/>s.</summary>
public sealed record Frequency
{
    /// <summary>The maximum interval.</summary>
    public const int MaxInterval = 366;

    private Frequency(FrequencyUnit unit, int interval)
    {
        Unit = unit;
        Interval = interval;
    }

    /// <summary>Every day.</summary>
    public static Frequency Daily { get; } = new(FrequencyUnit.Day, 1);

    /// <summary>Every week.</summary>
    public static Frequency Weekly { get; } = new(FrequencyUnit.Week, 1);

    /// <summary>Every calendar month.</summary>
    public static Frequency Monthly { get; } = new(FrequencyUnit.Month, 1);

    /// <summary>The unit.</summary>
    public FrequencyUnit Unit { get; }

    /// <summary>The number of units between instalments.</summary>
    public int Interval { get; }

    /// <summary>Every <paramref name="days"/> days.</summary>
    public static Frequency EveryDays(int days) => Create(FrequencyUnit.Day, days);

    /// <summary>Every <paramref name="weeks"/> weeks.</summary>
    public static Frequency EveryWeeks(int weeks) => Create(FrequencyUnit.Week, weeks);

    /// <summary>Every <paramref name="months"/> months.</summary>
    public static Frequency EveryMonths(int months) => Create(FrequencyUnit.Month, months);

    /// <summary>
    /// The due date of the instalment at zero-based <paramref name="index"/>, counted from <paramref name="first"/>.
    /// Monthly dates are always computed from the first date, so a schedule starting on 31 January falls due on
    /// 28 or 29 February and then 31 March, not 28 March.
    /// </summary>
    public DateOnly DueDate(DateOnly first, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var steps = checked(index * Interval);
        return Unit switch
        {
            FrequencyUnit.Day => first.AddDays(steps),
            FrequencyUnit.Week => first.AddDays(checked(steps * 7)),
            FrequencyUnit.Month => first.AddMonths(steps),
            _ => throw new InvalidOperationException($"Unknown frequency unit {Unit}."),
        };
    }

    /// <inheritdoc />
    public override string ToString() => (Unit, Interval) switch
    {
        (FrequencyUnit.Day, 1) => "daily",
        (FrequencyUnit.Week, 1) => "weekly",
        (FrequencyUnit.Month, 1) => "monthly",
        _ => $"every {Interval} {Unit.ToString().ToLowerInvariant()}s",
    };

    private static Frequency Create(FrequencyUnit unit, int interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(interval, MaxInterval);
        return new Frequency(unit, interval);
    }
}
