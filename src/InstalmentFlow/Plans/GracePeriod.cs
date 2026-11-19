namespace InstalmentFlow;

/// <summary>
/// The number of calendar days after an instalment's due date before an unpaid instalment counts as overdue.
/// Days past due are still counted from the due date itself.
/// </summary>
public sealed record GracePeriod
{
    /// <summary>The maximum grace period, one year.</summary>
    public const int MaxDays = 366;

    private GracePeriod(int days) => Days = days;

    /// <summary>No grace: an unpaid instalment is overdue the day after it falls due.</summary>
    public static GracePeriod None { get; } = new(0);

    /// <summary>The number of grace days.</summary>
    public int Days { get; }

    /// <summary>A grace period of <paramref name="days"/> calendar days.</summary>
    public static GracePeriod OfDays(int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, MaxDays);
        return days == 0 ? None : new GracePeriod(days);
    }

    /// <summary>The last day of grace for an instalment due on <paramref name="dueDate"/>.</summary>
    public DateOnly LastGraceDay(DateOnly dueDate) => dueDate.AddDays(Days);

    /// <inheritdoc />
    public override string ToString() => Days == 1 ? "1 day" : $"{Days} days";
}
