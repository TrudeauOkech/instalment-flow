namespace InstalmentFlow;

/// <summary>
/// Instants are kept to the microsecond: the precision PostgreSQL stores (doc 05 §8). Clocks on Linux tick in 100 ns;
/// without this, a value held in memory and the same value read back would differ by a fraction of a microsecond.
/// </summary>
internal static class Microseconds
{
    public static DateTimeOffset Truncate(DateTimeOffset instant) => instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMicrosecond));
}

/// <summary>The engine's clock: the host's, read to the microsecond.</summary>
internal sealed class MicrosecondClock(TimeProvider inner) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => Microseconds.Truncate(inner.GetUtcNow());

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override long GetTimestamp() => inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);
}
