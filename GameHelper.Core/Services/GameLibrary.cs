using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

namespace GameHelper.Core.Services;

/// <summary>Routes library changes through catalog invariants and the existing automation owner.</summary>
public sealed class GameLibrary(
    IGameConfiguration configuration, IGameCatalogService catalog, IGameAutomationService automation,
    IMonitorControlService monitor, IGameLaunchCoordinator launch, ISteamGameResolver steam,
    IPlaytimeSnapshotProvider history, TimeProvider? timeProvider = null, ILocalGameDiscovery? localGames = null,
    ISettingsService? settings = null, IMonitorModeController? monitorMode = null, ISyncService? sync = null, SessionRatings? ratings = null) : IGameLibrary
{
    private PlaytimeReport BuildReport(PlaytimeSnapshot snapshot, ReportPeriod period)
    {
        var games = configuration.Read().Games;
        var report = PlaytimeReportBuilder.Build(snapshot.Records, games, period, timeProvider ?? TimeProvider.System);
        var summary = SessionRatings.Summarize(snapshot.Ratings ?? [], period, timeProvider ?? TimeProvider.System);
        report = report with { AverageRating = summary.Average, RatingSamples = summary.Samples, HighestRatedSession = summary.Highest };
        if (snapshot.Journal is null) return report;
        var names = games.ToDictionary(g => g.DataKey, g => g.DisplayName, StringComparer.OrdinalIgnoreCase);
        return report with
        {
            JournalIncluded = true,
            ClearedGames = snapshot.Journal.Where(g => g.CompletedOn >= period.Start && g.CompletedOn < period.EndExclusive)
                .Select(g => g with { Name = names.TryGetValue(g.DataKey, out var name) && !string.IsNullOrWhiteSpace(name) ? name.Trim() : g.DataKey })
                .OrderBy(g => g.CompletedOn).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.DataKey, StringComparer.OrdinalIgnoreCase).ToList(),
            PreviousClearedGames = snapshot.Journal.Count(g => g.CompletedOn >= period.Previous.Start && g.CompletedOn < period.Previous.EndExclusive)
        };
    }

    public Task<GameLibraryResponse> ExecuteAsync(GameLibraryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? message = null;
        switch (request.Operation)
        {
            case GameLibraryOperation.RatingTimeline:
                return Task.FromResult(new GameLibraryResponse { Success = true, RatingTimeline = RequireRatings().Timeline(request.DataKey ?? "") });
            case GameLibraryOperation.SessionReviews:
                return Task.FromResult(new GameLibraryResponse { Success = true, SessionReviews = RequireRatings().Read(request.SessionReviews ?? new()) });
            case GameLibraryOperation.GetSessionRating:
                return Task.FromResult(new GameLibraryResponse { Success = true, RatedSession = RequireRatings().Get(request.SessionId ?? throw new ArgumentException("缺少会话身份。")) });
            case GameLibraryOperation.SaveSessionRating:
                return Task.FromResult(new GameLibraryResponse { Success = true, RatedSession = RequireRatings().Save(request.SessionRating ?? throw new ArgumentException("缺少评分。")) });
            case GameLibraryOperation.GetSettings:
                return Task.FromResult(new GameLibraryResponse { Success = true, Settings = RequireSettings().Get() });
            case GameLibraryOperation.UpdateSettings:
                var updated = RequireSettings().Update(request.Settings ?? throw new ArgumentException("缺少全局设置。"));
                return Task.FromResult(new GameLibraryResponse { Success = true, Settings = updated, Message = "全局设置已保存。" });
            case GameLibraryOperation.DdcSnapshot:
                if (configuration.Read().MonitorMode?.Enabled == true)
                    throw new InvalidOperationException("请先保存关闭显示器自动预设，再录制，以免自动切换影响采样。");
                var sample = monitorMode?.ReadSnapshot();
                return Task.FromResult(new GameLibraryResponse { Success = sample is not null, DdcSnapshot = sample,
                    Error = sample is null ? "无法读取显示器：请确认显示器在线、DDC/CI 已开启且所有显示器 HDR 已关闭。" : null });
            case GameLibraryOperation.SyncStatus:
                return Task.FromResult(new GameLibraryResponse { Success = true, SyncStatus = RequireSync().GetStatus() });
            case GameLibraryOperation.SyncValidate:
            case GameLibraryOperation.SyncPush:
                return ExecuteSyncAsync(request.Operation, cancellationToken);
            case GameLibraryOperation.Report:
                var reportSnapshot = history.GetReportSnapshot();
                if (reportSnapshot.ReadFailed)
                    return Task.FromResult(new GameLibraryResponse { Error = "无法读取历史文件，请稍后重试。" });
                return Task.FromResult(new GameLibraryResponse { Success = true,
                    Report = BuildReport(reportSnapshot, request.ReportPeriod ?? throw new ArgumentException("缺少报告期间。")) });
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
                    Actions = game.Actions, ClearActions = game.Actions is null,
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
                if (request.LaunchRequestId is { } launchId) launch.Launch(request.DataKey ?? string.Empty, launchId);
                else launch.Launch(request.DataKey ?? string.Empty);
                message = "已发起启动，等待识别游戏进程。";
                break;
            default: throw new ArgumentException("不支持的游戏库操作。");
        }
        var states = launch.GetGameStates();
        var identities = launch.GetLaunchIdentities() ?? new Dictionary<string, (Guid RequestId, int ProcessId)>();
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
                LaunchRequestId = identities.TryGetValue(game.DataKey, out var identity) ? identity.RequestId : null,
                LaunchProcessId = identities.TryGetValue(game.DataKey, out identity) ? identity.ProcessId : null,
                Installation = installations?.GetValueOrDefault(game.DataKey) ?? new(GameInstallationState.Unknown, "尚未检查本地安装位置"),
                LastPlayedAt = lastPlayed.TryGetValue(game.DataKey, out var playedAt) ? playedAt : null,
                TotalMinutes = records.Where(record => record.GameName.Equals(game.DataKey, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(record => record.Sessions).Sum(session => session.DurationMinutes)
            }).ToList()
        });
    }

    private SessionRatings RequireRatings() => ratings ?? throw new InvalidOperationException("当前存储不支持评分，请更新运行时并确认 SQLite 已激活。");

    private ISettingsService RequireSettings() => settings ?? throw new InvalidOperationException("当前运行时不支持全局设置，请更新运行时。");
    private ISyncService RequireSync() => sync ?? throw new InvalidOperationException("当前运行时不支持 GitHub 同步，请更新运行时。");

    private async Task<GameLibraryResponse> ExecuteSyncAsync(GameLibraryOperation operation, CancellationToken cancellationToken)
    {
        var service = RequireSync();
        var outcome = operation == GameLibraryOperation.SyncValidate
            ? await service.ValidateAsync(cancellationToken).ConfigureAwait(false)
            : await service.SyncNowAsync(true, cancellationToken).ConfigureAwait(false);
        var status = service.GetStatus();
        // sync.enabled is the master switch: skipped uploads must say so instead of looking successful.
        var message = outcome.Status switch
        {
            SyncOutcomeStatus.Uploaded => $"已推送统计提交：{outcome.CommitId}",
            SyncOutcomeStatus.Skipped when !status.Enabled => outcome.Detail,
            _ => outcome.Detail ?? (outcome.Success ? "操作完成。" : null)
        };
        return new GameLibraryResponse { Success = outcome.Success, Error = outcome.Error,
            Message = message, SyncOutcome = outcome, SyncStatus = status };
    }

    private async Task<GameLibraryResponse> ScanDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (localGames is null) throw new InvalidOperationException("当前运行时不支持目录扫描，请更新后重试。");
        return new GameLibraryResponse { Success = true, LocalScan = await localGames.ScanAsync(directory, configuration.Read().Games, cancellationToken).ConfigureAwait(false) };
    }
}
