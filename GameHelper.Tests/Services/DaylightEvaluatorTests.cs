using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Xunit;

namespace GameHelper.Tests.Services;

public class DaylightEvaluatorTests
{
    private static List<MonitorModePresetData> Presets(params (string code, string value)[] pairs) =>
        pairs.Select(p => new MonitorModePresetData { Code = p.code, Value = p.value }).ToList();

    private static MonitorModeSettings FixedWindowSettings(string start, string end) => new()
    {
        Enabled = true,
        DaylightStart = start,
        DaylightEnd = end,
        GamePresets = Presets(("DC", "0x05"), ("E2", "0x04"), ("F0", "0x00")),
        ComfortPresets = Presets(("DC", "0x00"), ("E2", "0x1D"), ("F0", "0x0C"))
    };

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 10, 16, hour, minute, 0, TimeSpan.FromHours(8));

    private static DateTimeOffset AtDate(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(6, 59, false)]
    [InlineData(7, 0, true)]
    [InlineData(12, 0, true)]
    [InlineData(18, 29, true)]
    [InlineData(18, 30, false)]
    public void FixedWindow_IsHalfOpen(int hour, int minute, bool expected)
    {
        var evaluator = new DaylightEvaluator(FixedWindowSettings("07:00", "18:30"));

        Assert.Equal(expected, evaluator.IsDaylight(At(hour, minute)));
    }

    [Fact]
    public void FixedWindow_Defaults_WhenTimesMissing()
    {
        var settings = new MonitorModeSettings { Enabled = true, DaylightStart = null, DaylightEnd = null };

        var evaluator = new DaylightEvaluator(settings);

        Assert.False(evaluator.IsDaylight(At(6, 59)));
        Assert.True(evaluator.IsDaylight(At(12, 0)));
        Assert.False(evaluator.IsDaylight(At(19, 0)));
    }

    [Theory]
    [InlineData(23, 0, true)]
    [InlineData(6, 0, true)]
    [InlineData(8, 0, false)]
    public void FixedWindow_CrossesMidnight_WhenStartAfterEnd(int hour, int minute, bool expected)
    {
        var evaluator = new DaylightEvaluator(FixedWindowSettings("21:00", "07:00"));

        Assert.Equal(expected, evaluator.IsDaylight(At(hour, minute)));
    }

    [Fact]
    public void FixedWindow_EqualBounds_IsNeverDaylight()
    {
        var evaluator = new DaylightEvaluator(FixedWindowSettings("07:00", "07:00"));

        Assert.False(evaluator.IsDaylight(At(7, 0)));
    }

    [Fact]
    public void SolarWindow_UsesSunriseSunset()
    {
        // Beijing mid-October: 12:00 local is clearly day, 22:00 local is clearly night.
        var settings = FixedWindowSettings("07:00", "18:30");
        settings.Latitude = 39.907;
        settings.Longitude = 116.391;

        var evaluator = new DaylightEvaluator(settings);

        Assert.True(evaluator.IsDaylight(AtDate(2026, 10, 16, 12, 0)));
        Assert.False(evaluator.IsDaylight(AtDate(2026, 10, 16, 22, 0)));
    }

    [Fact]
    public void SolarWindow_DynamicCoordinates_UsedWhenNoManualConfig()
    {
        var settings = FixedWindowSettings("07:00", "18:30");
        // Beijing solar window (06:33–17:26 that day) disagrees with the fixed window
        // (07:00–18:30) at 06:45 and 18:00 — proving the dynamic coordinate takes effect.
        Func<UserCoordinate?> source = () => new UserCoordinate(39.907, 116.391);

        var evaluator = new DaylightEvaluator(settings, source);

        Assert.True(evaluator.IsDaylight(AtDate(2026, 10, 16, 6, 45)));
        Assert.False(evaluator.IsDaylight(AtDate(2026, 10, 16, 18, 0)));
    }

    [Fact]
    public void SolarWindow_ManualCoordinates_OverrideDynamicSource()
    {
        var settings = FixedWindowSettings("07:00", "18:30");
        settings.Latitude = 39.907;
        settings.Longitude = 116.391;
        // Garbage from the source must never be used when manual coordinates exist.
        Func<UserCoordinate?> source = () => new UserCoordinate(double.NaN, double.NaN);

        var evaluator = new DaylightEvaluator(settings, source);

        Assert.True(evaluator.IsDaylight(AtDate(2026, 10, 16, 12, 0)));
    }

    [Fact]
    public void FixedWindow_WhenDynamicSourceReturnsNothing()
    {
        var settings = FixedWindowSettings("07:00", "18:30");
        Func<UserCoordinate?> source = () => null;

        var evaluator = new DaylightEvaluator(settings, source);

        Assert.True(evaluator.IsDaylight(AtDate(2026, 10, 16, 12, 0)));
        Assert.False(evaluator.IsDaylight(AtDate(2026, 10, 16, 18, 30)));
    }

    [Fact]
    public void SolarWindow_PolarDay_IsAlwaysDaylight()
    {
        var settings = FixedWindowSettings("07:00", "18:30");
        settings.Latitude = 78.22;
        settings.Longitude = 15.65;

        var evaluator = new DaylightEvaluator(settings);

        Assert.True(evaluator.IsDaylight(AtDate(2026, 6, 21, 3, 0)));
        Assert.True(evaluator.IsDaylight(AtDate(2026, 6, 21, 23, 30)));
    }

    [Fact]
    public void Constructor_InvalidFixedTime_Throws()
    {
        var settings = FixedWindowSettings("25:00", "18:30");

        Assert.ThrowsAny<ArgumentException>(() => new DaylightEvaluator(settings));
    }
}