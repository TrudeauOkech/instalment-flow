namespace InstalmentFlow.Tests;

/// <summary>A clock that stays where you put it.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>A provider whose Nairobi business date is <paramref name="date"/> (at 09:00 EAT).</summary>
    public static FixedTimeProvider AtNairobiDate(DateOnly date) =>
        new(new DateTimeOffset(date.Year, date.Month, date.Day, 6, 0, 0, TimeSpan.Zero));
}
