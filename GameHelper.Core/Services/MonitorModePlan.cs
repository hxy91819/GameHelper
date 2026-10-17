using System;
using System.Collections.Generic;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// Immutable, validated snapshot of the monitor-mode configuration: the DDC/CI recipe
/// for each preset plus the daylight policy. Rebuilt on every config load/reload so the
/// rest of the pipeline never re-parses user input at runtime.
/// </summary>
public sealed class MonitorModePlan
{
    private readonly Dictionary<byte, uint> _gameTarget;
    private readonly Dictionary<byte, uint> _comfortTarget;

    /// <summary>Recipe applied (in order) when the game preset is wanted.</summary>
    public IReadOnlyList<MonitorModeWrite> GameWrites { get; }

    /// <summary>Recipe applied (in order) when the comfort preset is wanted.</summary>
    public IReadOnlyList<MonitorModeWrite> ComfortWrites { get; }

    /// <summary>Daylight policy derived from coordinates or fixed times.</summary>
    public DaylightEvaluator Daylight { get; }

    /// <summary>Distinct VCP codes to probe when detecting the currently applied preset.</summary>
    public IReadOnlyList<MonitorModeWrite> ProbeWrites { get; }

    public MonitorModePlan(MonitorModeSettings settings, Func<UserCoordinate?>? dynamicCoordinates = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        GameWrites = Parse(settings.GamePresets, nameof(settings.GamePresets));
        ComfortWrites = Parse(settings.ComfortPresets, nameof(settings.ComfortPresets));
        Daylight = new DaylightEvaluator(settings, dynamicCoordinates);

        _gameTarget = BuildTarget(GameWrites);
        _comfortTarget = BuildTarget(ComfortWrites);

        var probes = new List<MonitorModeWrite>();
        foreach (var write in GameWrites)
        {
            if (!probes.Exists(p => p.Code == write.Code))
            {
                probes.Add(write);
            }
        }

        foreach (var write in ComfortWrites)
        {
            if (!probes.Exists(p => p.Code == write.Code))
            {
                probes.Add(write);
            }
        }

        ProbeWrites = probes;
    }

    /// <summary>Parses and validates settings; returns null (feature off) when invalid, warning via logger.</summary>
    public static MonitorModePlan? TryCreate(
        MonitorModeSettings? settings,
        ILogger logger,
        Func<UserCoordinate?>? dynamicCoordinates = null)
    {
        if (settings is null || !settings.Enabled)
        {
            return null;
        }

        try
        {
            return new MonitorModePlan(settings, dynamicCoordinates);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "monitorMode configuration is invalid; automatic preset switching stays disabled.");
            return null;
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "monitorMode configuration is invalid; automatic preset switching stays disabled.");
            return null;
        }
    }

    /// <summary>Whether every probe code of the recipe currently holds its target value.</summary>
    public bool MatchesTarget(IReadOnlyDictionary<byte, uint> state, MonitorMode mode)
    {
        var target = mode == MonitorMode.Game ? _gameTarget : _comfortTarget;
        foreach (var pair in target)
        {
            if (!state.TryGetValue(pair.Key, out var current) || current != pair.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<byte, uint> BuildTarget(IReadOnlyList<MonitorModeWrite> writes)
    {
        var target = new Dictionary<byte, uint>();
        foreach (var write in writes)
        {
            target[write.Code] = write.Value;
        }

        return target;
    }

    private static List<MonitorModeWrite> Parse(List<MonitorModePresetData> presets, string fieldName)
    {
        if (presets is null || presets.Count == 0)
        {
            throw new ArgumentException($"monitorMode.{fieldName} must contain at least one preset.");
        }

        var writes = new List<MonitorModeWrite>(presets.Count);
        foreach (var preset in presets)
        {
            if (!MonitorModeWrite.TryParse(preset?.Code, preset?.Value, out var write))
            {
                throw new ArgumentException(
                    $"monitorMode.{fieldName} contains an invalid code/value pair: '{preset?.Code}' = '{preset?.Value}'.");
            }

            writes.Add(write);
        }

        return writes;
    }
}