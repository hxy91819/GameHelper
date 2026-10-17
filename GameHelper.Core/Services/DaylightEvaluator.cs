using System;
using System.Globalization;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

namespace GameHelper.Core.Services;

/// <summary>
/// Decides whether a given local instant is inside "daylight" according to the
/// monitor-mode configuration: solar sunrise/sunset when coordinates are configured,
/// otherwise a fixed local time window. Constructed from a validated
/// <see cref="MonitorModeSettings"/>; instances are immutable and thread-safe.
/// </summary>
public sealed class DaylightEvaluator
{
    private readonly bool _useSolar;
    private readonly double _latitude;
    private readonly double _longitude;
    private readonly Func<UserCoordinate?>? _dynamicCoordinates;
    private readonly TimeOnly _fixedStart;
    private readonly TimeOnly _fixedEnd;

    /// <summary>
    /// Builds the evaluator. When coordinates are absent from settings,
    /// <paramref name="dynamicCoordinates"/> (if provided) is consulted per evaluation;
    /// whenever it yields a coordinate, the solar window is used, otherwise the fixed
    /// local time window applies.
    /// </summary>
    public DaylightEvaluator(MonitorModeSettings settings, Func<UserCoordinate?>? dynamicCoordinates = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _dynamicCoordinates = dynamicCoordinates;
        _useSolar = settings.Latitude.HasValue && settings.Longitude.HasValue;
        if (_useSolar)
        {
            _latitude = settings.Latitude!.Value;
            _longitude = settings.Longitude!.Value;
            return;
        }

        _fixedStart = Parse(string.IsNullOrWhiteSpace(settings.DaylightStart) ? "07:00" : settings.DaylightStart!, nameof(settings.DaylightStart));
        _fixedEnd = Parse(string.IsNullOrWhiteSpace(settings.DaylightEnd) ? "18:30" : settings.DaylightEnd!, nameof(settings.DaylightEnd));
    }

    /// <summary>Whether the instant is inside the daylight window in local time.</summary>
    public bool IsDaylight(DateTimeOffset localNow)
    {
        var coordinate = ResolveSolarCoordinate();
        if (coordinate is not null)
        {
            var window = SolarCalculator.Compute(
                coordinate.Value.Latitude,
                coordinate.Value.Longitude,
                DateOnly.FromDateTime(localNow.LocalDateTime),
                localNow.Offset.TotalHours);

            return window.Kind switch
            {
                SolarDayKind.AlwaysDay => true,
                SolarDayKind.AlwaysNight => false,
                _ => localNow.TimeOfDay >= window.Sunrise.ToTimeSpan() && localNow.TimeOfDay < window.Sunset.ToTimeSpan()
            };
        }

        var timeOfDay = localNow.TimeOfDay;
        if (_fixedStart == _fixedEnd)
        {
            return false;
        }

        if (_fixedStart < _fixedEnd)
        {
            return timeOfDay >= _fixedStart.ToTimeSpan() && timeOfDay < _fixedEnd.ToTimeSpan();
        }

        // Window crosses midnight (e.g. 21:00 -> 07:00).
        return timeOfDay >= _fixedStart.ToTimeSpan() || timeOfDay < _fixedEnd.ToTimeSpan();
    }

    private UserCoordinate? ResolveSolarCoordinate()
    {
        if (_useSolar)
        {
            return new UserCoordinate(_latitude, _longitude);
        }

        return _dynamicCoordinates?.Invoke();
    }

    private static TimeOnly Parse(string text, string fieldName)
    {
        if (!TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"'{text}' is not a valid HH:mm time for {fieldName}.");
        }

        return value;
    }
}