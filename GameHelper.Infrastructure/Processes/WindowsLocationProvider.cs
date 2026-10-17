using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Processes;

/// <summary>
/// Reads the device coordinate from the Windows Location COM API
/// (<c>LocationDisp.LatLongReportFactory</c>) — the desktop-app channel of the system
/// location service, gated by the "Location" privacy switch and the "Allow desktop apps
/// to access your location" toggle in Windows Settings. Never throws; any failure
/// (permission denied, no providers, factory missing) yields null.
/// </summary>
public sealed class WindowsLocationProvider : IUserLocationSource
{
    private const string FactoryProgId = "LocationDisp.LatLongReportFactory";

    private readonly ILogger<WindowsLocationProvider>? _logger;

    public WindowsLocationProvider(ILogger<WindowsLocationProvider>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public UserCoordinate? TryGetLocation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        object? factory = null;
        object? report = null;
        try
        {
            var factoryType = Type.GetTypeFromProgID(FactoryProgId);
            if (factoryType is null)
            {
                _logger?.LogDebug("Location COM factory {ProgId} is not registered; automatic location is unavailable.", FactoryProgId);
                return null;
            }

            factory = Activator.CreateInstance(factoryType);
            // The factory's dispatch interface exposes the latest report as a property;
            // there is no "GetReport" dispatch method on it.
            report = factoryType.InvokeMember("LatLongReport", BindingFlags.GetProperty, null, factory!, null);
            if (report is null)
            {
                _logger?.LogDebug("Location service returned no report (position fix unavailable).");
                return null;
            }

            var reportType = report.GetType();
            var latitude = Convert.ToDouble(
                reportType.InvokeMember("Latitude", BindingFlags.GetProperty, null, report, null),
                CultureInfo.InvariantCulture);
            var longitude = Convert.ToDouble(
                reportType.InvokeMember("Longitude", BindingFlags.GetProperty, null, report, null),
                CultureInfo.InvariantCulture);

            if (double.IsNaN(latitude) || double.IsNaN(longitude)
                || double.IsInfinity(latitude) || double.IsInfinity(longitude)
                || Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
            {
                _logger?.LogDebug("Location service returned an out-of-range coordinate ({Lat}, {Lon}); ignoring.", latitude, longitude);
                return null;
            }

            _logger?.LogInformation("Location service reported the device coordinate: ({Lat:F3}, {Lon:F3}).", latitude, longitude);
            return new UserCoordinate(latitude, longitude);
        }
        catch (Exception ex)
        {
            // 0x80070005 (E_ACCESSDENIED) when the system "Location" switch is off; other
            // failures cover no-fix and platform quirks. All degrade to "unavailable".
            _logger?.LogDebug(ex, "Automatic location is unavailable; check the system 'Location' switch and desktop-app location permission.");
            return null;
        }
        finally
        {
            // Best-effort RCW release; a single failure must not block the remaining cleanup.
            foreach (var comObject in new[] { report, factory })
            {
                try
                {
                    if (comObject is not null && Marshal.IsComObject(comObject))
                    {
                        Marshal.ReleaseComObject(comObject);
                    }
                }
                catch (Exception)
                {
                    // Finalizer will reclaim the RCW.
                }
            }
        }
    }
}