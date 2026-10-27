using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using GameHelper.Core.Validation;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Providers;

internal static class ConfigEntryNormalizer
{
    /// <summary>Normalize an entry read from disk; incomplete entries are skipped with a warning.</summary>
    public static GameConfig? NormalizeLoaded(GameConfig source, ILogger? logger = null)
    {
        var executable = (source.Executable ?? string.Empty).Trim();
        var displayName = (source.DisplayName ?? string.Empty).Trim();
        var dataKey = (source.DataKey ?? string.Empty).Trim();

        var errors = AppConfigValidator.ValidateGameIdentity(source);
        if (errors.Count > 0)
        {
            logger?.LogWarning("Skip config entry '{DataKey}': {Errors}", dataKey, string.Join(" ", errors));
            return null;
        }

        return CreateNormalized(source, dataKey, executable, displayName, logger, rejectInvalidSpeed: false);
    }

    public static GameConfig NormalizeForSave(GameConfig source, ILogger? logger = null)
    {
        var errors = AppConfigValidator.ValidateGame(source);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        var dataKey = (source.DataKey ?? string.Empty).Trim();
        var executable = (source.Executable ?? string.Empty).Trim();
        var displayName = (source.DisplayName ?? string.Empty).Trim();
        return CreateNormalized(source, dataKey, executable, displayName, logger, rejectInvalidSpeed: true);
    }

    public static void RepairDuplicateDataKeys(IReadOnlyList<GameConfig> configs, ILogger? logger = null, string? logContext = null)
    {
        var usedDataKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in configs)
        {
            var originalDataKey = config.DataKey;
            config.DataKey = ConfigIdentity.EnsureUniqueDataKey(config.DataKey, usedDataKeys);
            if (logger is not null && !string.Equals(originalDataKey, config.DataKey, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Adjusted duplicate DataKey '{DataKey}' to '{NewDataKey}'{Context}.", originalDataKey, config.DataKey, logContext ?? string.Empty);
            }
        }
    }

    private static GameConfig CreateNormalized(
        GameConfig source,
        string dataKey,
        string executable,
        string displayName,
        ILogger? logger,
        bool rejectInvalidSpeed)
    {
        var speedMultiplier = source.SpeedMultiplier;
        if (speedMultiplier.HasValue &&
            !AppConfigValidator.IsValidSpeed(speedMultiplier))
        {
            if (rejectInvalidSpeed)
            {
                throw new InvalidDataException(
                    $"Cannot save config entry '{dataKey}' with speed '{speedMultiplier.Value}'. Speed must be greater than zero.");
            }

            logger?.LogWarning(
                "Ignore invalid speed {Speed} for config entry '{DataKey}'; speed must be greater than zero.",
                speedMultiplier.Value,
                dataKey);
            speedMultiplier = null;
        }

        var hotkeys = source.SpeedHotkeys;
        try { SpeedHotkey.Validate(hotkeys); }
        catch (ArgumentException ex)
        {
            if (rejectInvalidSpeed) throw new InvalidDataException($"Invalid speedHotkeys for '{dataKey}': {ex.Message}", ex);
            logger?.LogWarning(ex, "Ignore invalid speedHotkeys for {DataKey}", dataKey);
            hotkeys = null;
        }

        var normalized = source.Copy();
        normalized.DataKey = dataKey;
        normalized.Executable = executable;
        normalized.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName;
        normalized.SpeedMultiplier = speedMultiplier;
        normalized.SpeedHotkeys = hotkeys?.ToList();
        normalized.SpeedAttach = NormalizeSpeedAttach(source.SpeedAttach, dataKey, logger, rejectInvalidSpeed);
        return normalized;
    }

    private static string? NormalizeSpeedAttach(string? source, string dataKey, ILogger? logger, bool rejectInvalidSpeed)
    {
        var value = source?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (AppConfigValidator.IsValidSpeedAttach(value))
        {
            return value;
        }

        if (rejectInvalidSpeed)
        {
            throw new InvalidDataException(
                $"Cannot save config entry '{dataKey}' with speedAttach '{source}'. Expected '{GameConfig.SpeedAttachPolicyAuto}' or '{GameConfig.SpeedAttachPolicyImmediate}'.");
        }

        logger?.LogWarning(
            "Ignore invalid speedAttach '{Value}' for config entry '{DataKey}'; expected '{Auto}' or '{Immediate}'.",
            source,
            dataKey,
            GameConfig.SpeedAttachPolicyAuto,
            GameConfig.SpeedAttachPolicyImmediate);
        return null;
    }
}
