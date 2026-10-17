using System;

namespace GameHelper.Core.Utilities;

/// <summary>Kind of daylight a solar day degenerates into at extreme latitudes.</summary>
public enum SolarDayKind
{
    /// <summary>Sun rises and sets normally.</summary>
    Normal = 0,

    /// <summary>Sun never sets (polar day); daylight all day.</summary>
    AlwaysDay = 1,

    /// <summary>Sun never rises (polar night); night all day.</summary>
    AlwaysNight = 2
}

/// <summary>Solar daylight window for one calendar day in local time.</summary>
public readonly record struct SolarDaylight(SolarDayKind Kind, TimeOnly Sunrise, TimeOnly Sunset)
{
    public static SolarDaylight Normal(TimeOnly sunrise, TimeOnly sunset) => new(SolarDayKind.Normal, sunrise, sunset);
    public static SolarDaylight Always(SolarDayKind kind) => new(kind, default, default);
}

/// <summary>
/// Self-contained NOAA "general solar calculations" implementation for sunrise/sunset
/// times, accurate to roughly a minute. Longitude is degrees east positive, latitude
/// degrees north positive. Deliberately time-zone agnostic: callers pass the UTC offset
/// in hours (fractional offsets supported) together with the local calendar date.
/// </summary>
public static class SolarCalculator
{
    private const double SolarZenithOfficialDegrees = 90.833; // sunrise/sunset edge incl. refraction

    /// <summary>
    /// Computes the daylight window for one local calendar day.
    /// </summary>
    /// <exception cref="ArgumentException">Latitude/longitude outside their valid range.</exception>
    public static SolarDaylight Compute(double latitude, double longitude, DateOnly localDate, double utcOffsetHours)
    {
        if (latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be within [-90, 90] degrees.");
        }

        if (longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be within [-180, 180] degrees.");
        }

        var degreesToRadians = Math.PI / 180.0;

        // Fractional year (NOAA), evaluated at local noon of the requested day.
        var gamma = 2.0 * Math.PI / 365.0 * (localDate.DayOfYear - 1);

        // Equation of time (minutes) and solar declination (radians), NOAA coefficients.
        var eqTimeMin = 229.18 * (
            0.000075
            + 0.001868 * Math.Cos(gamma)
            - 0.032077 * Math.Sin(gamma)
            - 0.014615 * Math.Cos(2 * gamma)
            - 0.040849 * Math.Sin(2 * gamma));

        var declination = 0.006918
            - 0.399912 * Math.Cos(gamma)
            + 0.070257 * Math.Sin(gamma)
            - 0.006758 * Math.Cos(2 * gamma)
            + 0.000907 * Math.Sin(2 * gamma)
            - 0.002697 * Math.Cos(3 * gamma)
            + 0.00148 * Math.Sin(3 * gamma);

        var latRad = latitude * degreesToRadians;
        var zenithRad = SolarZenithOfficialDegrees * degreesToRadians;

        // Within ~0.1 degree of the pole the hour-angle formula degenerates; classify by declination sign.
        if (Math.Abs(latitude) >= 89.9)
        {
            var northernSummer = latitude > 0 ? declination > 0 : declination < 0;
            return SolarDaylight.Always(northernSummer ? SolarDayKind.AlwaysDay : SolarDayKind.AlwaysNight);
        }

        var cosHourAngle =
            Math.Cos(zenithRad) / (Math.Cos(latRad) * Math.Cos(declination))
            - Math.Tan(latRad) * Math.Tan(declination);

        if (cosHourAngle > 1)
        {
            return SolarDaylight.Always(SolarDayKind.AlwaysNight);
        }

        if (cosHourAngle < -1)
        {
            return SolarDaylight.Always(SolarDayKind.AlwaysDay);
        }

        var hourAngleDeg = Math.Acos(cosHourAngle) / degreesToRadians;

        var sunriseUtcMin = 720.0 - 4.0 * (longitude + hourAngleDeg) - eqTimeMin;
        var sunsetUtcMin = 720.0 - 4.0 * (longitude - hourAngleDeg) - eqTimeMin;

        return SolarDaylight.Normal(
            ToLocalTimeOnly(sunriseUtcMin, utcOffsetHours),
            ToLocalTimeOnly(sunsetUtcMin, utcOffsetHours));
    }

    private static TimeOnly ToLocalTimeOnly(double utcMinutes, double utcOffsetHours)
    {
        var localMinutes = utcMinutes + utcOffsetHours * 60.0;
        var wrapped = ((long)Math.Round(localMinutes) % 1440 + 1440) % 1440;
        return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(wrapped));
    }
}