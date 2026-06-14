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
        var executableName = (source.ExecutableName ?? source.Name ?? string.Empty).Trim();
        var executablePath = (source.ExecutablePath ?? string.Empty).Trim();
        var displayName = (source.DisplayName ?? source.Alias ?? string.Empty).Trim();
        var dataKey = (source.DataKey ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(dataKey))
        {
            if (!string.IsNullOrWhiteSpace(executableName))
            {
                dataKey = executableName;
                logger?.LogWarning("Config entry missing DataKey; fallback to ExecutableName='{ExecutableName}'.", executableName);
            }
            else if (!string.IsNullOrWhiteSpace(displayName))
            {
                dataKey = displayName;
                logger?.LogWarning("Config entry missing DataKey; fallback to DisplayName='{DisplayName}'.", displayName);
            }
            else if (missingDataKeyAction == MissingDataKeyAction.Throw)
            {
                throw new InvalidDataException("Configuration entry is missing required DataKey.");
            }
            else
            {
                logger?.LogWarning("Skip config entry: DataKey/ExecutableName/DisplayName are all missing.");
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(executablePath) && !string.IsNullOrWhiteSpace(executableName))
        {
            logger?.LogWarning("Config entry '{DataKey}' has no ExecutablePath; fallback matching will use ExecutableName only.", dataKey);
        }

        return CreateNormalized(source, ConfigIdentity.EnsureEntryId(source.EntryId), dataKey, executableName, executablePath, displayName);
    }

    public static GameConfig NormalizeForSave(GameConfig source, ILogger? logger = null)
    {
        var dataKey = (source.DataKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            throw new InvalidDataException("Cannot save config entry without DataKey.");
        }

        var executableName = (source.ExecutableName ?? source.Name ?? string.Empty).Trim();
        var executablePath = (source.ExecutablePath ?? string.Empty).Trim();
        var displayName = (source.DisplayName ?? source.Alias ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(executablePath) && !string.IsNullOrWhiteSpace(executableName))
        {
            logger?.LogWarning("Config entry '{DataKey}' is saved without ExecutablePath; fallback matching will use ExecutableName only.", dataKey);
        }

        return CreateNormalized(source, ConfigIdentity.EnsureEntryId(source.EntryId), dataKey, executableName, executablePath, displayName);
    }

    public static void RepairDuplicateIdentities(IReadOnlyList<GameConfig> configs, ILogger? logger = null, string? logContext = null)
    {
        var usedEntryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedDataKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in configs)
        {
            var originalEntryId = config.EntryId;
            config.EntryId = ConfigIdentity.EnsureUniqueEntryId(config.EntryId, usedEntryIds);
            if (logger is not null && !string.Equals(originalEntryId, config.EntryId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Adjusted duplicate EntryId '{EntryId}' to '{NewEntryId}'{Context}.", originalEntryId, config.EntryId, logContext ?? string.Empty);
            }

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
        string entryId,
        string dataKey,
        string executableName,
        string executablePath,
        string displayName)
    {
        return new GameConfig
        {
            EntryId = entryId,
            DataKey = dataKey,
            ExecutableName = string.IsNullOrWhiteSpace(executableName) ? null : executableName,
            ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            IsEnabled = source.IsEnabled,
            HDREnabled = source.HDREnabled
        };
    }
}
