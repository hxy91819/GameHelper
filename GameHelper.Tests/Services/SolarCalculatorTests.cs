using System;
using GameHelper.Core.Utilities;
using Xunit;

namespace GameHelper.Tests.Services;

public class SolarCalculatorTests
{
    [Fact]
    public void Compute_BeijingMidOctober_ReturnsPlausibleDaylightWindow()
    {
        // Beijing (east longitude) on 2026-10-16, UTC+8. Rough almanac bounds, not exact
        // seconds: sunrise shortly before 07:00, sunset around 17:30 local time.
        var daylight = SolarCalculator.Compute(39.907, 116.391, new DateOnly(2026, 10, 16), 8.0);

        Assert.Equal(SolarDayKind.Normal, daylight.Kind);
        Assert.InRange(daylight.Sunrise.Hour, 5, 7);
        Assert.InRange(daylight.Sunset.Hour, 16, 18);

        var dayLengthHours = (daylight.Sunset.ToTimeSpan() - daylight.Sunrise.ToTimeSpan()).TotalHours;
        Assert.InRange(dayLengthHours, 10.0, 12.0);
    }

    [Fact]
    public void Compute_Equinox_Equator_ApproximatelyTwelveHourDay()
    {
        // Equator at the equinox: sunrise near 06:00, sunset near 18:00.
        var daylight = SolarCalculator.Compute(0.0, 100.0, new DateOnly(2026, 3, 20), 7.0);

        Assert.Equal(SolarDayKind.Normal, daylight.Kind);
        var dayLengthHours = (daylight.Sunset.ToTimeSpan() - daylight.Sunrise.ToTimeSpan()).TotalHours;
        Assert.InRange(dayLengthHours, 11.9, 12.2);
    }

    [Fact]
    public void Compute_PolarRegions_DegenerateToAlwaysDayOrAlwaysNight()
    {
        var midsummer = SolarCalculator.Compute(69.649, 18.956, new DateOnly(2026, 6, 21), 2.0);
        Assert.Equal(SolarDayKind.AlwaysDay, midsummer.Kind);

        var midwinter = SolarCalculator.Compute(69.649, 18.956, new DateOnly(2026, 12, 21), 1.0);
        Assert.Equal(SolarDayKind.AlwaysNight, midwinter.Kind);

        // Southern counterpart: Antarctic mid-January is polar day.
        var antarcticSummer = SolarCalculator.Compute(-80.0, 0.0, new DateOnly(2026, 1, 15), 0.0);
        Assert.Equal(SolarDayKind.AlwaysDay, antarcticSummer.Kind);
    }

    [Theory]
    [InlineData(90.1, 0.0)]
    [InlineData(0.0, 180.1)]
    public void Compute_OutOfRangeCoordinates_Throw(double latitude, double longitude)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => SolarCalculator.Compute(latitude, longitude, new DateOnly(2026, 10, 16), 8.0));
    }
}