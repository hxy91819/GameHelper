using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// 统计自动推送核心调度：
/// - 会话结束路径零额外磁盘写入，脏检查只做文件 mtime 对比；
/// - 默认每 <see cref="SyncSettings.IntervalMinutes"/> 分钟最多推送一次，失败后按固定退避重试；
/// - 仅在推送成功后写一次 sync-state.json。
/// </summary>
public sealed class SyncService : ISyncService
{
    /// <summary>推送失败后的最小重试间隔（分钟），避免后台循环持续失败时频繁打远端。</summary>
    public const int RetryBackoffMinutes = 60;

    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IGameConfiguration _configuration;
    private readonly IPlaytimeSnapshotProvider _snapshotProvider;
    private readonly IStatsUploadChannelProvider _channelProvider;
    private readonly StatsReportBuilder _reportBuilder;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncService> _logger;
    private readonly string? _playtimeCsvPath;
    private readonly string? _configPath;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncService(
        IGameConfiguration configuration,
        IPlaytimeSnapshotProvider snapshotProvider,
        IStatsUploadChannelProvider channelProvider,
        StatsReportBuilder reportBuilder,
        TimeProvider timeProvider,
        ILogger<SyncService> logger,
        string? playtimeCsvPath = null,
        string? statePath = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _channelProvider = channelProvider ?? throw new ArgumentNullException(nameof(channelProvider));
        _reportBuilder = reportBuilder ?? throw new ArgumentNullException(nameof(reportBuilder));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // 生产 DI 注册不传可选参数：两个路径都必须回退到默认数据目录，
        // 否则 mtime 脏检查对 playtime.csv 恒为 false，自动推送永远不会触发。
        _playtimeCsvPath = playtimeCsvPath ?? AppDataPath.GetPlaytimeCsvPath();
        _configPath = configuration is IConfigPathProvider pathProvider ? pathProvider.ConfigPath : null;
        _statePath = statePath ?? AppDataPath.GetSyncStatePath();
    }

    public async Task<SyncOutcome> SyncNowAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var (appConfig, settings) = ReadConfigWithSettings();
        if (settings is null)
        {
            return SyncOutcome.Skipped("sync 未配置或未启用（config.yml: sync.enabled: true）");
        }

        var configError = settings.Validate();
        if (configError is not null)
        {
            return SyncOutcome.FailedWithError(configError);
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SyncState state;
        try
        {
            state = LoadState();
            if (!state.MatchesTarget(settings))
            {
                state = CreateFreshState(settings);
            }

            if (!force)
            {
                var skip = ShouldSkip(settings, state, nowUtc);
                if (skip is not null)
                {
                    return skip;
                }
            }

            var report = BuildReport(settings, appConfig);
            if (report.SessionCount == 0)
            {
                return SyncOutcome.Skipped("暂无游玩数据");
            }

            if (!force && string.Equals(state.ContentHash, report.ContentHash, StringComparison.Ordinal))
            {
                MarkSuccess(settings, state, nowUtc, report.ContentHash);
                return SyncOutcome.Skipped("内容与上次推送一致，跳过上传");
            }

            var channel = _channelProvider.GetChannel(settings);
            var commitMessage = $"Update game stats (auto) - {nowUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
            var result = await channel
                .UploadAsync(settings, report.Files, commitMessage, cancellationToken)
                .ConfigureAwait(false);

            MarkSuccess(settings, state, nowUtc, report.ContentHash);
            _logger.LogInformation(
                "统计已推送到 {Provider} {Repo}（{FileCount} 个文件）",
                settings.Provider,
                settings.NormalizedRepo,
                report.Files.Count);
            return result.NoChanges
                ? SyncOutcome.Skipped("远端已是最新，未产生新提交")
                : SyncOutcome.UploadedCommit(result.CommitId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "统计推送到 {Repo} 失败", settings.NormalizedRepo);
            TryRecordFailure(settings, nowUtc, ex.Message);
            return SyncOutcome.FailedWithError(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SyncOutcome> ValidateAsync(CancellationToken cancellationToken = default)
    {
        var (_, settings) = ReadConfigWithSettings();
        if (settings is null)
        {
            return SyncOutcome.Skipped("sync 未配置或未启用（config.yml: sync.enabled: true）");
        }

        var configError = settings.Validate();
        if (configError is not null)
        {
            return SyncOutcome.FailedWithError(configError);
        }

        try
        {
            var channel = _channelProvider.GetChannel(settings);
            await channel.ValidateAsync(settings, cancellationToken).ConfigureAwait(false);
            return new SyncOutcome(
                SyncOutcomeStatus.Validated,
                Detail: $"渠道可用：{settings.Provider} {settings.NormalizedRepo}（method={settings.NormalizedMethod}）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SyncOutcome.FailedWithError(ex.Message);
        }
    }

    public SyncStatusInfo GetStatus()
    {
        var appConfig = _configuration.Read();
        var settings = appConfig.SyncSettings;
        var state = LoadState();
        var configError = settings?.Validate();
        var targetMatches = settings is not null && state.MatchesTarget(settings);

        // 换过目标（或从未成功推送）时按全新状态评估待推送，避免沿用旧目标的时间戳漏判。
        var effectiveState = targetMatches ? state : new SyncState();

        return new SyncStatusInfo
        {
            Configured = settings is not null,
            Enabled = settings?.Enabled ?? false,
            Provider = settings?.Provider ?? string.Empty,
            Method = settings?.NormalizedMethod ?? string.Empty,
            Repo = settings?.NormalizedRepo ?? string.Empty,
            Directory = settings?.NormalizedDirectory ?? string.Empty,
            IntervalMinutes = settings?.IntervalMinutes ?? 0,
            ConfigError = configError,
            LastSuccessUtc = targetMatches ? state.LastSuccessUtc : default,
            LastAttemptUtc = targetMatches ? state.LastAttemptUtc : default,
            LastError = targetMatches ? state.LastError : null,
            HasPendingData = configError is null && HasLocalChanges(effectiveState)
        };
    }

    /// <summary>
    /// 读取配置并解析可用的 sync 设置；未配置或未启用时 <paramref name="appConfig"/> 仍返回完整配置。
    /// </summary>
    private (AppConfig AppConfig, SyncSettings? Settings) ReadConfigWithSettings()
    {
        var appConfig = _configuration.Read();
        var settings = appConfig.SyncSettings is { Enabled: true } enabled ? enabled : null;
        return (appConfig, settings);
    }

    private SyncOutcome? ShouldSkip(SyncSettings settings, SyncState state, DateTime nowUtc)
    {
        var dueByInterval = state.LastSuccessUtc == default
            || nowUtc - state.LastSuccessUtc >= TimeSpan.FromMinutes(settings.IntervalMinutes);
        if (!dueByInterval)
        {
            return SyncOutcome.Skipped($"距上次成功推送不足 {settings.IntervalMinutes} 分钟");
        }

        if (!HasLocalChanges(state))
        {
            return SyncOutcome.Skipped("本地暂无新数据");
        }

        if (state.LastError is not null
            && state.LastAttemptUtc != default
            && nowUtc - state.LastAttemptUtc < TimeSpan.FromMinutes(RetryBackoffMinutes))
        {
            return SyncOutcome.Skipped($"上次推送失败，{RetryBackoffMinutes} 分钟内不自动重试（可 sync now --force）");
        }

        return null;
    }

    private StatsReport BuildReport(SyncSettings settings, AppConfig appConfig)
    {
        var snapshot = _snapshotProvider.GetSnapshot();
        var generatedAtLocal = _timeProvider.GetLocalNow().LocalDateTime;
        return _reportBuilder.Build(
            snapshot.Records,
            appConfig.Games ?? new List<GameConfig>(),
            generatedAtLocal,
            settings.IncludeRawCsv);
    }

    /// <summary>本地数据是否比上次成功推送更新（时长 CSV 或配置任一更新即视为有新内容）。</summary>
    private bool HasLocalChanges(SyncState state)
    {
        var referenceUtc = state.LastSuccessUtc == default ? DateTime.MinValue : state.LastSuccessUtc;
        return IsModifiedAfter(_playtimeCsvPath, referenceUtc) || IsModifiedAfter(_configPath, referenceUtc);
    }

    private static bool IsModifiedAfter(string? path, DateTime referenceUtc)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        return File.GetLastWriteTimeUtc(path) > referenceUtc;
    }

    private static SyncState CreateFreshState(SyncSettings settings) => new()
    {
        Provider = settings.Provider,
        Repo = settings.NormalizedRepo,
        Method = settings.NormalizedMethod
    };

    private void MarkSuccess(SyncSettings settings, SyncState state, DateTime nowUtc, string contentHash)
    {
        state.Provider = settings.Provider;
        state.Repo = settings.NormalizedRepo;
        state.Method = settings.NormalizedMethod;
        state.LastSuccessUtc = nowUtc;
        state.LastAttemptUtc = nowUtc;
        state.LastError = null;
        state.ContentHash = contentHash;
        TrySaveState(state);
    }

    private void TryRecordFailure(SyncSettings settings, DateTime nowUtc, string error)
    {
        try
        {
            var state = LoadState();
            if (!state.MatchesTarget(settings))
            {
                state = CreateFreshState(settings);
            }

            state.LastAttemptUtc = nowUtc;
            state.LastError = error;
            TrySaveState(state);
        }
        catch (Exception saveEx)
        {
            _logger.LogDebug(saveEx, "保存 sync 失败状态时出错");
        }
    }

    private SyncState LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return new SyncState();
            }

            var json = File.ReadAllText(_statePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<SyncState>(json, StateJsonOptions) ?? new SyncState();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取 sync 状态失败，按全新状态处理");
            return new SyncState();
        }
    }

    private void TrySaveState(SyncState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(state, StateJsonOptions), Encoding.UTF8);
                File.Move(tempPath, _statePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "保存 sync 状态失败");
        }
    }
}
