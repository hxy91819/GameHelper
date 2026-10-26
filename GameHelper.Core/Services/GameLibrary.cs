using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

namespace GameHelper.Core.Services;

/// <summary>Routes library changes through catalog invariants and the existing automation owner.</summary>
public sealed class GameLibrary(
    IGameConfiguration configuration, IGameCatalogService catalog, IGameAutomationService automation,
    IMonitorControlService monitor, IGameLaunchCoordinator launch, ISteamGameResolver steam,
    IPlaytimeSnapshotProvider history, TimeProvider? timeProvider = null, ILocalGameDiscovery? localGames = null) : IGameLibrary
{
    public Task<GameLibraryResponse> ExecuteAsync(GameLibraryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? message = null;
        switch (request.Operation)
        {
            case GameLibraryOperation.Statistics:
                var snapshot = history.GetSnapshot();
                if (snapshot.ReadFailed)
                    return Task.FromResult(new GameLibraryResponse { Success = false, Error = "无法读取历史文件，请稍后刷新重试。" });
                return Task.FromResult(new GameLibraryResponse
                {
                    Success = true,
                    Statistics = PlaytimeDashboardBuilder.Build(snapshot.Records, configuration.Read().Games,
                        request.Statistics ?? new PlaytimeDashboardQuery(), timeProvider)
                });
            case GameLibraryOperation.List: break;
            case GameLibraryOperation.Save:
                var game = request.Game ?? throw new ArgumentException("缺少游戏设置。");
                catalog.Update(request.DataKey ?? string.Empty, new GameCatalogUpdateRequest
                {
                    Executable = ExecutableIdentity.Parse(game.Executable ?? string.Empty),
                    DisplayName = game.DisplayName, ClearDisplayName = string.IsNullOrWhiteSpace(game.DisplayName),
                    IsEnabled = game.IsEnabled, HdrEnabled = game.HdrEnabled,
                    SpeedMultiplier = game.SpeedMultiplier, ClearSpeedMultiplier = game.SpeedMultiplier is null,
                    SpeedHotkeys = game.SpeedHotkeys, ClearSpeedHotkeys = game.SpeedHotkeys is null,
                    Launch = game.Launch, ClearLaunch = game.Launch is null, ExpectedRevision = request.ExpectedRevision
                });
                automation.ReloadConfig();
                message = "设置已保存；已运行游戏的倍速在下次启动时应用。";
                break;
            case GameLibraryOperation.Add:
                var added = request.Game ?? throw new ArgumentException("缺少游戏程序。");
                catalog.Intake(new GameCatalogIntakeRequest
                {
                    Executable = ExecutableIdentity.Parse(added.Executable ?? string.Empty),
                    DisplayName = added.DisplayName, Launch = added.Launch
                });
                automation.ReloadConfig();
                message = "游戏已加入目录。";
                break;
            case GameLibraryOperation.Remove:
                catalog.Remove(request.DataKey ?? string.Empty);
                automation.ReloadConfig();
                message = "已从目录移除，历史游玩记录保留。";
                break;
            case GameLibraryOperation.DiscoverSteam:
                return Task.FromResult(new GameLibraryResponse { Success = true, SteamGames = steam.TryEnumerateInstalledGames() });
            case GameLibraryOperation.DiscoverDirectory:
                return ScanDirectoryAsync(request.Directory ?? string.Empty, cancellationToken);
            case GameLibraryOperation.ImportDirectory:
                if (request.LocalGames.Count > 1000) throw new ArgumentException("每次最多导入 1000 个候选程序。");
                var localRequests = request.LocalGames.DistinctBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase).Select(item =>
                {
                    if (!Path.IsPathFullyQualified(item.ExecutablePath) || !item.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        || !File.Exists(item.ExecutablePath)) throw new ArgumentException("游戏程序已不存在，请重新扫描目录。");
                    return new GameCatalogIntakeRequest
                    {
                        Executable = ExecutableIdentity.Parse(item.ExecutablePath), DisplayName = item.Name,
                        HdrEnabled = false, SpeedMultiplier = null, SkipExisting = true,
                        Launch = new GameLaunchOptions { Target = item.ExecutablePath }
                    };
                }).ToList();
                var localAdded = catalog.BatchIntake(localRequests).Count(result => result.WasAdded);
                automation.ReloadConfig();
                message = $"已添加 {localAdded} 款游戏；已有条目保持原设置，新增游戏的 HDR 和倍速默认关闭。";
                break;
            case GameLibraryOperation.ImportSteam:
                var chosen = request.SteamAppIds.ToHashSet(StringComparer.Ordinal);
                var requests = steam.TryEnumerateInstalledGames().Where(item => chosen.Contains(item.AppId))
                    .Select(item => new GameCatalogIntakeRequest
                    {
                        Executable = ExecutableIdentity.Parse(item.ExecutablePath), DisplayName = item.Name,
                        Launch = new GameLaunchOptions { Target = $"steam://rungameid/{item.AppId}" }, SkipExisting = true
                    }).ToList();
                var imported = catalog.BatchIntake(requests).Count(result => result.WasAdded);
                automation.ReloadConfig();
                message = $"导入 {imported} 款游戏；已有条目保持原设置。";
                break;
            case GameLibraryOperation.Launch:
                monitor.Start();
                launch.Launch(request.DataKey ?? string.Empty);
                message = "已发起启动，等待识别游戏进程。";
                break;
            default: throw new ArgumentException("不支持的游戏库操作。");
        }
        var states = launch.GetGameStates();
        // 松散 mock / 外部实现的健壮回退：会话开始时间缺失时保持界面旧行为（不显示计时）。
        var runningStarts = launch.GetRunningSessionStarts() ?? new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        var records = history.GetPlaytimeRecords();
        var lastPlayed = LibraryRecency.LastPlayed(records, timeProvider ?? TimeProvider.System);
        var configs = configuration.Read().Games;
        var installations = localGames?.Assess(configs);
        return Task.FromResult(new GameLibraryResponse
        {
            Success = true, Message = message, ConfigPath = (configuration as IConfigPathProvider)?.ConfigPath ?? string.Empty,
            MonitorRunning = monitor.IsRunning,
            Games = configs.Select(game => new LibraryGame
            {
                Config = game, Revision = GameConfigRevision.For(game), State = states.GetValueOrDefault(game.DataKey, game.IsEnabled ? "未运行" : "自动化已暂停"),
                RunningStartedAt = runningStarts.GetValueOrDefault(game.DataKey),
                Installation = installations?.GetValueOrDefault(game.DataKey) ?? new(GameInstallationState.Unknown, "尚未检查本地安装位置"),
                LastPlayedAt = lastPlayed.TryGetValue(game.DataKey, out var playedAt) ? playedAt : null,
                TotalMinutes = records.Where(record => record.GameName.Equals(game.DataKey, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(record => record.Sessions).Sum(session => session.DurationMinutes)
            }).ToList()
        });
    }

    private async Task<GameLibraryResponse> ScanDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (localGames is null) throw new InvalidOperationException("当前运行时不支持目录扫描，请更新后重试。");
        return new GameLibraryResponse { Success = true, LocalScan = await localGames.ScanAsync(directory, configuration.Read().Games, cancellationToken).ConfigureAwait(false) };
    }
}
