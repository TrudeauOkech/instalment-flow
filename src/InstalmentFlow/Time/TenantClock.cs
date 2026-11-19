namespace InstalmentFlow;

/// <summary>
/// The single place where instants (UTC <see cref="DateTimeOffset"/>) are converted to and from a tenant's
/// business dates (<see cref="DateOnly"/>) in its time zone. Reads the current time only through
/// <see cref="TimeProvider"/>, so tests control time.
/// </summary>
/// <remarks>
/// Rules for local times that do not map to exactly one instant (daylight-saving transitions):
/// a time inside a gap moves forward to the first valid local time after the gap, and an ambiguous time
/// resolves to its earlier occurrence. Africa/Nairobi has no daylight saving, but tenants elsewhere may.
/// </remarks>
public sealed class TenantClock
{
    private static readonly TimeSpan s_gapStep = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_maximumGap = TimeSpan.FromHours(4);

    /// <summary>Creates a clock for a time zone, reading the current time from <paramref name="timeProvider"/>.</summary>
    public TenantClock(TimeZoneInfo timeZone, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeZone = timeZone;
        TimeProvider = timeProvider;
    }

    /// <summary>The tenant's business time zone.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>The source of the current time.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>The current instant, in UTC.</summary>
    public DateTimeOffset UtcNow => TimeProvider.GetUtcNow();

    /// <summary>The current business date in the tenant's time zone.</summary>
    public DateOnly Today => ToBusinessDate(UtcNow);

    /// <summary>Creates a clock from an IANA time zone ID such as <c>Africa/Nairobi</c>.</summary>
    /// <exception cref="TimeZoneNotFoundException">The time zone is not known on this system.</exception>
    public static TenantClock ForTimeZone(string timeZoneId, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        return new TenantClock(TimeZoneInfo.FindSystemTimeZoneById(timeZoneId), timeProvider ?? TimeProvider.System);
    }

    /// <summary>The business date on which <paramref name="instant"/> falls in the tenant's time zone.</summary>
    public DateOnly ToBusinessDate(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, TimeZone);
        return new DateOnly(local.Year, local.Month, local.Day);
    }

    /// <summary>The first instant of <paramref name="date"/> in the tenant's time zone, in UTC.</summary>
    public DateTimeOffset StartOfDay(DateOnly date) => ToInstant(date, TimeOnly.MinValue);

    /// <summary>The first instant of the day after <paramref name="date"/>; use as an exclusive upper bound.</summary>
    public DateTimeOffset EndOfDayExclusive(DateOnly date) => StartOfDay(date.AddDays(1));

    /// <summary>
    /// Converts a local date and time in the tenant's time zone to a UTC instant. Provider adapters use this
    /// for timestamps sent without an offset (for example Daraja <c>TransTime</c>, which is East Africa Time).
    /// </summary>
    public DateTimeOffset ToInstant(DateOnly date, TimeOnly time)
    {
#pragma warning disable RS0030 // TimeZoneInfo works in DateTime; this method is the sanctioned boundary.
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);

        var shifted = TimeSpan.Zero;
        while (TimeZone.IsInvalidTime(local))
        {
            shifted += s_gapStep;
            if (shifted > s_maximumGap)
            {
                throw new InvalidOperationException($"Could not resolve local time {date} {time} in {TimeZone.Id}.");
            }

            local = local.Add(s_gapStep);
        }

        // For an ambiguous time the larger offset is the earlier instant.
        var offset = TimeZone.IsAmbiguousTime(local)
            ? TimeZone.GetAmbiguousTimeOffsets(local).Max()
            : TimeZone.GetUtcOffset(local);
#pragma warning restore RS0030

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
