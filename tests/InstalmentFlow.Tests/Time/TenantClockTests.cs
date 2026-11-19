namespace InstalmentFlow.Tests;

public sealed class TenantClockTests
{
    private static readonly DateOnly s_date = new(2026, 12, 1);

    [Theory]
    [InlineData("2026-12-01T20:59:59Z", "2026-12-01")]   // 23:59:59 EAT, last second of 1 Dec
    [InlineData("2026-12-01T21:00:00Z", "2026-12-02")]   // 00:00:00 EAT, first second of 2 Dec
    [InlineData("2026-11-30T21:00:00Z", "2026-12-01")]
    [InlineData("2026-12-01T12:00:00+03:00", "2026-12-01")]
    public void Nairobi_business_date_boundaries(string instant, string expected)
    {
        var clock = TenantClock.ForTimeZone("Africa/Nairobi");

        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            clock.ToBusinessDate(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Nairobi_day_runs_from_21_00_UTC_the_previous_day()
    {
        var clock = TenantClock.ForTimeZone("Africa/Nairobi");

        Assert.Equal(new DateTimeOffset(2026, 11, 30, 21, 0, 0, TimeSpan.Zero), clock.StartOfDay(s_date));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 21, 0, 0, TimeSpan.Zero), clock.EndOfDayExclusive(s_date));
        Assert.Equal(TimeSpan.Zero, clock.StartOfDay(s_date).Offset);
    }

    [Fact]
    public void Daraja_TransTime_style_local_time_converts_to_utc()
    {
        // Daraja TransTime "20261201235959" is East Africa Time without an offset.
        var clock = TenantClock.ForTimeZone("Africa/Nairobi");

        Assert.Equal(new DateTimeOffset(2026, 12, 1, 20, 59, 59, TimeSpan.Zero), clock.ToInstant(s_date, new TimeOnly(23, 59, 59)));
    }

    [Fact]
    public void Now_and_today_come_from_the_time_provider()
    {
        var provider = new FixedTimeProvider(new DateTimeOffset(2026, 12, 1, 21, 30, 0, TimeSpan.Zero));
        var clock = TenantClock.ForTimeZone("Africa/Nairobi", provider);

        Assert.Equal(provider.Now, clock.UtcNow);
        Assert.Equal(new DateOnly(2026, 12, 2), clock.Today);
    }

    [Fact]
    public void Local_time_in_a_daylight_saving_gap_moves_forward()
    {
        // London, 29 Mar 2026: 01:00 GMT jumps to 02:00 BST; 01:30 does not exist.
        var clock = TenantClock.ForTimeZone("Europe/London");

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero),
            clock.ToInstant(new DateOnly(2026, 3, 29), new TimeOnly(1, 30)));
    }

    [Fact]
    public void Ambiguous_local_time_resolves_to_the_earlier_instant()
    {
        // London, 25 Oct 2026: 01:30 happens twice (BST then GMT); the earlier is 00:30 UTC.
        var clock = TenantClock.ForTimeZone("Europe/London");

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero),
            clock.ToInstant(new DateOnly(2026, 10, 25), new TimeOnly(1, 30)));
    }

    [Fact]
    public void Unknown_time_zone_fails_fast()
    {
        Assert.ThrowsAny<TimeZoneNotFoundException>(() => TenantClock.ForTimeZone("Mars/Olympus_Mons"));
    }
}
