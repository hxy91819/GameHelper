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

        return CreateNormalized(source, dataKey, executable, displayName);
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
        return CreateNormalized(source, dataKey, executable, displayName);
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
        string displayName)
    {
        return new GameConfig
        {
            DataKey = dataKey,
            Executable = executable,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            IsEnabled = source.IsEnabled,
            HdrEnabled = source.HdrEnabled
        };
    }
}
