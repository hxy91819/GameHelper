using System.Diagnostics;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using GameHelper.Infrastructure.Artwork;

namespace GameHelper.Infrastructure.Resolvers;

/// <summary>Local filesystem evidence and bounded exe discovery, independent of launch/automation ownership.</summary>
public sealed class LocalGameDiscovery(ISteamGameResolver steam, TimeProvider? timeProvider = null) : ILocalGameDiscovery
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _steamGate = new();
    private IReadOnlyList<SteamInstalledGame> _installed = [];
    private DateTimeOffset? _checked;
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { "$RECYCLE.BIN", "System Volume Information", "node_modules", ".git" };

    public Task<LocalGameScan> ScanAsync(string directory, IReadOnlyList<GameConfig> existing, CancellationToken cancellationToken)
        => Task.Run(() => Scan(directory, existing, cancellationToken), cancellationToken);

    private static LocalGameScan Scan(string directory, IReadOnlyList<GameConfig> existing, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("请选择完整的本地目录。");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("扫描目录已不存在。");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("请选择实际目录，扫描不会跟随目录链接。");
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var found = new List<(string Path, string Folder, LocalExecutableClassifier.Evidence Evidence, long Size, bool Added)>();
        var singleGame = LocalExecutableClassifier.IsSingleGameRoot(root);
        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var skipped = 0;
        var visited = 0;
        var entries = 0;
        var truncated = false;
        var elapsed = Stopwatch.StartNew();
        while (pending.TryDequeue(out var next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > 10000 || entries > 50000 || found.Count >= 1000 || elapsed.Elapsed > TimeSpan.FromSeconds(25)) { truncated = true; break; }
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(next.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++entries > 50000 || elapsed.Elapsed > TimeSpan.FromSeconds(25) || found.Count >= 1000) { truncated = true; break; }
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var folderName = Path.GetFileName(path);
                        if (ExcludedDirectories.Contains(folderName)) continue;
                        // Unity resources do not contain the launch entry; avoid spending the scan budget on assets.
                        if (folderName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
                            && File.Exists(Path.Combine(next.Path, folderName[..^5] + ".exe"))) continue;
                        if (next.Depth < 8) pending.Enqueue((path, next.Depth + 1));
                        else truncated = true;
                        continue;
                    }
                    if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.Length > 1024) continue;
                    var relative = Path.GetRelativePath(root, path);
                    var nested = relative.Contains(Path.DirectorySeparatorChar);
                    var group = singleGame || !nested ? rootName : relative.Split(Path.DirectorySeparatorChar)[0];
                    var gameDirectory = singleGame || !nested ? root : Path.Combine(root, group);
                    var evidence = LocalExecutableClassifier.Inspect(path, gameDirectory, group);
                    var size = new FileInfo(path).Length;
                    found.Add((path, group, evidence, size, ConfigEntryMatcher.FindExistingForIntake(existing, ExecutableIdentity.Parse(path)) is not null));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        var candidates = found.GroupBy(item => item.Folder, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                var ranked = group.OrderBy(item => item.Evidence.Auxiliary).ThenByDescending(item => item.Evidence.Strength)
                    .ThenByDescending(item => item.Size).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
                var best = ranked[0];
                var unambiguous = !best.Evidence.Auxiliary && best.Evidence.Strength >= 60
                    && ranked.Count(item => !item.Evidence.Auxiliary && item.Evidence.Strength == best.Evidence.Strength) == 1;
                return ranked.Select(item => new LocalGameCandidate(item.Evidence.Name, item.Path, item.Folder,
                    unambiguous && item.Path == best.Path, item.Added)
                {
                    IsAuxiliary = item.Evidence.Auxiliary,
                    Evidence = item.Evidence.Reason + (!unambiguous && item.Evidence.Strength >= 60
                        ? "；同目录存在多个同等候选，请确认" : string.Empty)
                });
            })
            .OrderBy(item => item.Folder, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.IsAuxiliary)
            .ThenByDescending(item => item.Recommended).ToList();
        // Include JSON escaping in the budget, keeping even long Unicode paths below the 4 MiB IPC limit.
        var bounded = new List<LocalGameCandidate>(); var bytes = 0;
        foreach (var candidate in candidates)
        {
            bytes += JsonSerializer.SerializeToUtf8Bytes(candidate).Length + 1;
            if (bytes > 3 * 1024 * 1024) { truncated = true; break; }
            bounded.Add(candidate);
        }
        return new LocalGameScan(root, bounded, skipped, truncated);
    }

    public IReadOnlyDictionary<string, GameInstallation> Assess(IReadOnlyList<GameConfig> games)
    {
        IReadOnlyList<SteamInstalledGame> installed;
        lock (_steamGate)
        {
            if (_checked is null || _clock.GetUtcNow() - _checked >= TimeSpan.FromSeconds(30))
            {
                _installed = steam.TryEnumerateInstalledGames() ?? [];
                _checked = _clock.GetUtcNow();
            }
            installed = _installed;
        }
        return games.ToDictionary(game => game.DataKey, game => Assess(game, installed), StringComparer.OrdinalIgnoreCase);
    }

    private static GameInstallation Assess(GameConfig game, IReadOnlyList<SteamInstalledGame> installed)
    {
        if (game.ExecutablePath is { } executable && Path.IsPathFullyQualified(executable))
            return CheckFile(executable);
        var target = game.Launch?.Target;
        var id = SteamArtworkIdentity.Parse(target);
        if (id is null && target?.EndsWith(".url", StringComparison.OrdinalIgnoreCase) == true)
            id = SteamArtworkIdentity.Resolve(game.Executable ?? string.Empty, target);
        if (id is not null && installed.Any(item => item.AppId == id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) && File.Exists(item.ExecutablePath)))
            return new(GameInstallationState.Installed, "Steam 安装清单和本地程序已确认");
        if (game.ExecutableName is { } name && installed.Any(item => string.Equals(Path.GetFileName(item.ExecutablePath), name, StringComparison.OrdinalIgnoreCase) && File.Exists(item.ExecutablePath)))
            return new(GameInstallationState.Installed, "本地 Steam 清单中存在对应程序");
        if (target is not null && Path.IsPathFullyQualified(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return CheckFile(target);
        return new(GameInstallationState.Unknown, "仅有程序名或平台入口，尚不能确认本地安装位置");
    }

    private static GameInstallation CheckFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == 0 ? new(GameInstallationState.Installed, "本地程序存在") : new(GameInstallationState.Unknown, "程序路径指向目录，请重新选择游戏程序");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(GameInstallationState.Missing, "本地程序已不存在，历史记录仍然保留"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return new(GameInstallationState.Unknown, "当前无法访问程序位置，可检查磁盘或权限后刷新"); }
    }
}
