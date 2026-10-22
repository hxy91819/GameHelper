using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Providers;

internal enum MissingDataKeyAction
{
    Skip,
    Throw
}

internal static class ConfigEntryNormalizer
{
    public static GameConfig? NormalizeLoaded(
        GameConfig source,
        MissingDataKeyAction missingDataKeyAction,
        ILogger? logger = null)
    {
        var executable = (source.Executable ?? string.Empty).Trim();
        var displayName = (source.DisplayName ?? string.Empty).Trim();
        var dataKey = (source.DataKey ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(dataKey))
        {
            if (missingDataKeyAction == MissingDataKeyAction.Throw)
            {
                throw new InvalidDataException("Configuration entry is missing required DataKey.");
            }

            logger?.LogWarning("Skip config entry: DataKey is missing.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(executable))
        {
            if (missingDataKeyAction == MissingDataKeyAction.Throw)
            {
                throw new InvalidDataException($"Configuration entry '{dataKey}' is missing required Executable.");
            }

            logger?.LogWarning("Skip config entry '{DataKey}': Executable is missing.", dataKey);
            return null;
        }

        if (!ExecutableIdentity.TryCreate(executable, out _))
        {
            if (missingDataKeyAction == MissingDataKeyAction.Throw)
            {
                throw new InvalidDataException($"Configuration entry '{dataKey}' has an invalid Executable identity.");
            }

            logger?.LogWarning("Skip config entry '{DataKey}': Executable identity is invalid.", dataKey);
            return null;
        }

        return CreateNormalized(source, dataKey, executable, displayName, logger, rejectInvalidSpeed: false);
    }

    public static GameConfig NormalizeForSave(GameConfig source, ILogger? logger = null)
    {
        var dataKey = (source.DataKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            throw new InvalidDataException("Cannot save config entry without DataKey.");
        }

        var executable = (source.Executable ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidDataException($"Cannot save config entry '{dataKey}' without Executable.");
        }

        if (!ExecutableIdentity.TryCreate(executable, out _))
        {
            throw new InvalidDataException($"Cannot save config entry '{dataKey}' with an invalid Executable identity.");
        }

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
            (!double.IsFinite(speedMultiplier.Value) || speedMultiplier.Value <= 0))
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

        return new GameConfig
        {
            DataKey = dataKey,
            Executable = executable,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            IsEnabled = source.IsEnabled,
            HdrEnabled = source.HdrEnabled,
            SpeedMultiplier = speedMultiplier,
            SpeedAttach = NormalizeSpeedAttach(source.SpeedAttach, dataKey, logger, rejectInvalidSpeed),
            Launch = source.Launch,
            AddedAt = source.AddedAt
        };
    }

    private static string? NormalizeSpeedAttach(string? source, string dataKey, ILogger? logger, bool rejectInvalidSpeed)
    {
        var value = source?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value is GameConfig.SpeedAttachPolicyAuto or GameConfig.SpeedAttachPolicyImmediate)
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
