using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

namespace GameHelper.Core.Services;

/// <summary>
/// Owns Game Catalog Intake invariants and commits each mutation as one Game Configuration change.
/// </summary>
public sealed class GameCatalogService : IGameCatalogService
{
    private readonly IGameConfiguration _configuration;

    public GameCatalogService(IGameConfiguration configuration)
    {
        _configuration = configuration;
    }

    public IReadOnlyList<GameEntry> List() => _configuration.Read().Games
        .OrEmpty()
        .Where(config => config.ExecutableIdentity is not null)
        .OrderBy(config => config.DisplayName ?? config.DataKey, StringComparer.OrdinalIgnoreCase)
        .Select(ToEntry)
        .ToList();

    public GameCatalogIntakePreview PreviewIntake(GameCatalogIntakeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.Executable);

        var configs = CreateWorkingMapByDataKey(_configuration.Read().Games.OrEmpty());
        var existing = FindExisting(configs.Values, request.Executable);
        var suggestedDataKey = existing?.DataKey ?? DataKeyGenerator.GenerateUniqueDataKey(
            request.Executable,
            request.ProductName,
            configs.Values.Select(config => config.DataKey));

        return new GameCatalogIntakePreview
        {
            Executable = request.Executable,
            ExistingEntry = existing is null ? null : ToEntry(existing),
            SuggestedDataKey = suggestedDataKey,
            IsRequestedDataKeyAvailable = IsDataKeyAvailable(
                request.DataKey,
                existing?.DataKey,
                configs.Values)
        };
    }

    public GameCatalogIntakeResult Intake(GameCatalogIntakeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.Executable);

        GameCatalogIntakeResult? result = null;
        _configuration.Change(config =>
        {
            var configs = CreateWorkingMapByDataKey(config.Games.OrEmpty());
            result = Intake(configs, request);
            config.Games = configs.Values.ToList();
        });

        return result!;
    }

    public IReadOnlyList<GameCatalogIntakeResult> BatchIntake(IEnumerable<GameCatalogIntakeRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var batch = requests.ToList();
        foreach (var request in batch)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateIdentity(request.Executable);
        }

        if (batch.Count == 0)
        {
            return Array.Empty<GameCatalogIntakeResult>();
        }

        var results = new List<GameCatalogIntakeResult>(batch.Count);
        _configuration.Change(config =>
        {
            var configs = CreateWorkingMapByDataKey(config.Games.OrEmpty());
            foreach (var request in batch)
            {
                results.Add(Intake(configs, request));
            }

            config.Games = configs.Values.ToList();
        });

        return results;
    }

    public GameEntry Update(string dataKey, GameCatalogUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            throw new ArgumentException("Data key is required.", nameof(dataKey));
        }

        ArgumentNullException.ThrowIfNull(request);
        if (request.Executable is not null)
        {
            ValidateIdentity(request.Executable);
        }

        GameEntry? result = null;
        _configuration.Change(config =>
        {
            var configs = CreateWorkingMapByDataKey(config.Games.OrEmpty());
            var existing = FindByDataKey(configs.Values, dataKey) ??
                throw new KeyNotFoundException($"Game '{dataKey}' not found.");
            var identity = request.Executable ?? existing.ExecutableIdentity ??
                throw new InvalidOperationException($"Game '{dataKey}' has no executable identity.");

            var updated = new GameConfig
            {
                DataKey = existing.DataKey,
                Executable = identity.Value,
                DisplayName = request.ClearDisplayName ? null : request.DisplayName ?? existing.DisplayName,
                IsEnabled = request.IsEnabled ?? existing.IsEnabled,
                HdrEnabled = request.HdrEnabled ?? existing.HdrEnabled
            };

            configs[updated.DataKey] = updated;
            config.Games = configs.Values.ToList();
            result = ToEntry(updated);
        });

        return result!;
    }

    public bool Remove(string dataKey)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            return false;
        }

        var removed = false;
        _configuration.Change(config =>
        {
            var configs = CreateWorkingMapByDataKey(config.Games.OrEmpty());
            var existing = FindByDataKey(configs.Values, dataKey);
            if (existing is not null)
            {
                removed = configs.Remove(existing.DataKey);
            }

            config.Games = configs.Values.ToList();
        });

        return removed;
    }

    private static GameCatalogIntakeResult Intake(
        IDictionary<string, GameConfig> configs,
        GameCatalogIntakeRequest request)
    {
        var existing = FindExisting(configs.Values, request.Executable);
        var previousExecutablePath = existing?.ExecutablePath;
        var dataKey = ResolveDataKey(request, existing, configs.Values);
        var updated = new GameConfig
        {
            DataKey = dataKey,
            Executable = request.Executable.Value,
            DisplayName = request.DisplayName ?? existing?.DisplayName,
            IsEnabled = request.IsEnabled,
            HdrEnabled = request.HdrEnabled ?? existing?.HdrEnabled ?? false
        };

        if (existing is not null &&
            !string.Equals(existing.DataKey, dataKey, StringComparison.OrdinalIgnoreCase))
        {
            configs.Remove(existing.DataKey);
        }

        configs[dataKey] = updated;
        return new GameCatalogIntakeResult
        {
            Entry = ToEntry(updated),
            WasAdded = existing is null,
            PreviousExecutablePath = previousExecutablePath
        };
    }

    private static string ResolveDataKey(
        GameCatalogIntakeRequest request,
        GameConfig? existing,
        IEnumerable<GameConfig> configs)
    {
        var requested = request.DataKey?.Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            return existing?.DataKey ?? DataKeyGenerator.GenerateUniqueDataKey(
                request.Executable,
                request.ProductName,
                configs.Select(config => config.DataKey));
        }

        if (!IsDataKeyAvailable(requested, existing?.DataKey, configs))
        {
            throw new InvalidOperationException($"DataKey '{requested}' is already used by another game.");
        }

        return requested;
    }

    private static bool IsDataKeyAvailable(
        string? requestedDataKey,
        string? currentDataKey,
        IEnumerable<GameConfig> configs)
    {
        if (string.IsNullOrWhiteSpace(requestedDataKey))
        {
            return true;
        }

        var requested = requestedDataKey.Trim();
        return configs.All(config =>
            !string.Equals(config.DataKey, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(config.DataKey, currentDataKey, StringComparison.OrdinalIgnoreCase));
    }

    private static GameConfig? FindExisting(IEnumerable<GameConfig> configs, ExecutableIdentity identity) =>
        ConfigEntryMatcher.FindExistingForIntake(configs, identity);

    private static GameEntry ToEntry(GameConfig config) => new()
    {
        DataKey = config.DataKey,
        Executable = config.ExecutableIdentity ??
            throw new InvalidOperationException($"Game '{config.DataKey}' has no executable identity."),
        DisplayName = config.DisplayName,
        IsEnabled = config.IsEnabled,
        HdrEnabled = config.HdrEnabled
    };

    private static Dictionary<string, GameConfig> CreateWorkingMapByDataKey(IEnumerable<GameConfig> configs)
    {
        var result = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
        var usedDataKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs)
        {
            var dataKey = ConfigIdentity.EnsureUniqueDataKey(config.DataKey, usedDataKeys);
            result[dataKey] = new GameConfig
            {
                DataKey = dataKey,
                Executable = config.Executable,
                DisplayName = config.DisplayName,
                IsEnabled = config.IsEnabled,
                HdrEnabled = config.HdrEnabled
            };
        }

        return result;
    }

    private static GameConfig? FindByDataKey(IEnumerable<GameConfig> configs, string dataKey) =>
        configs.FirstOrDefault(config =>
            string.Equals(config.DataKey, dataKey, StringComparison.OrdinalIgnoreCase));

    private static void ValidateIdentity(ExecutableIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Name))
        {
            throw new ArgumentException("Executable identity must contain a file name.", nameof(identity));
        }
    }
}

internal static class GameCatalogEnumerableExtensions
{
    public static IEnumerable<GameConfig> OrEmpty(this IEnumerable<GameConfig>? configs) =>
        configs ?? Array.Empty<GameConfig>();
}
