using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

/// <summary>Builds fresh tray snapshots and routes actions through the same library launch command.</summary>
public sealed class TrayMenuService(IGameLibrary library, ITrayHdrControl hdr, IBreakReminderService reminders,
    TimeProvider clock, Action openLibrary, Action exit, Action? openConsoleMode = null)
{
    public async Task<IReadOnlyList<TrayMenuItem>> GetMenuAsync(CancellationToken cancellationToken = default)
    {
        var response = await library.ExecuteAsync(new() { Operation = GameLibraryOperation.List }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSuccess(response);
        var paused = reminders.PausedUntil is { } until ? TimeZoneInfo.ConvertTime(until, clock.LocalTimeZone) : (DateTimeOffset?)null;
        return Build(response.Games, hdr.Read(), reminders.IsRunning, paused, clock.GetLocalNow().DateTime);
    }

    public static IReadOnlyList<TrayMenuItem> Build(IReadOnlyList<LibraryGame> games, TrayHdrState hdr,
        bool remindersEnabled, DateTimeOffset? pausedUntil, DateTime now)
    {
        var rows = new List<TrayMenuItem>();
        foreach (var game in games.Where(g => IsRunning(g.State)).OrderBy(g => g.Config.DataKey, StringComparer.Ordinal))
        {
            var duration = game.RunningStartedAt is { } start
                ? $"本局 {Math.Max(0, (long)(now - start).TotalMinutes)} 分钟" : "本局时长未知";
            rows.Add(new($"{Name(game)} · {duration}"));
        }
        rows.Add(new("打开 GameHelper 游戏库", TrayMenuAction.OpenLibrary, true));
        rows.Add(new("主机模式", TrayMenuAction.OpenConsoleMode, true));
        var state = hdr.Enabled is { } enabled ? (enabled ? "已开启" : "已关闭") : "状态未知";
        rows.Add(new($"主显示器 HDR：{state}{(hdr.Reason is null ? "" : $"（{hdr.Reason}）")}", TrayMenuAction.ToggleHdr, hdr.CanToggle));
        if (remindersEnabled)
        {
            if (pausedUntil is { } until) rows.Add(new($"休息提醒已暂停至 {until:MM-dd HH:mm}"));
            rows.Add(new("暂停休息提醒 30 分钟", TrayMenuAction.Pause30Minutes, true));
            rows.Add(new("暂停休息提醒到明天", TrayMenuAction.PauseUntilTomorrow, true));
            rows.Add(new("恢复休息提醒", TrayMenuAction.ResumeReminders, pausedUntil is not null));
        }
        var recent = games.Where(g => g.LastPlayedAt is not null).OrderByDescending(g => g.LastPlayedAt)
            .ThenBy(g => g.Config.DataKey, StringComparer.Ordinal).Take(5).ToList();
        if (recent.Count > 0) rows.Add(new("最近游戏"));
        rows.AddRange(recent.Select(game => new TrayMenuItem(Name(game), TrayMenuAction.LaunchGame,
            !IsRunning(game.State) && game.State != "启动中", game.Config.DataKey)));
        rows.Add(new("退出 GameHelper 后台监控", TrayMenuAction.Exit, true));
        return rows;
    }

    public async Task DispatchAsync(TrayMenuItem item, CancellationToken cancellationToken = default)
    {
        if (!item.Enabled) return;
        cancellationToken.ThrowIfCancellationRequested();
        switch (item.Action)
        {
            case TrayMenuAction.OpenConsoleMode: (openConsoleMode ?? openLibrary)(); break;
            case TrayMenuAction.OpenLibrary: openLibrary(); break;
            case TrayMenuAction.Exit: exit(); break;
            case TrayMenuAction.ToggleHdr: hdr.Toggle(); break;
            case TrayMenuAction.Pause30Minutes: reminders.PauseUntil(clock.GetUtcNow().AddMinutes(30)); break;
            case TrayMenuAction.PauseUntilTomorrow:
                var tomorrow = clock.GetLocalNow().Date.AddDays(1);
                var utc = TimeZoneInfo.ConvertTimeToUtc(tomorrow, clock.LocalTimeZone);
                reminders.PauseUntil(new DateTimeOffset(utc));
                break;
            case TrayMenuAction.ResumeReminders: reminders.Resume(); break;
            case TrayMenuAction.LaunchGame:
                var latest = await library.ExecuteAsync(new() { Operation = GameLibraryOperation.List }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureSuccess(latest);
                var game = latest.Games.SingleOrDefault(g => g.Config.DataKey == item.DataKey)
                    ?? throw new InvalidOperationException("游戏已从游戏库移除，请重新打开菜单。");
                if (IsRunning(game.State) || game.State == "启动中") throw new InvalidOperationException("游戏已运行或正在启动。");
                EnsureSuccess(await library.ExecuteAsync(new() { Operation = GameLibraryOperation.Launch, DataKey = item.DataKey }, cancellationToken).ConfigureAwait(false));
                break;
        }
    }

    private static void EnsureSuccess(GameLibraryResponse response)
    {
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "游戏库操作失败。");
    }

    private static bool IsRunning(string state) => state.StartsWith("运行中", StringComparison.Ordinal);

    private static string Name(LibraryGame game) => string.IsNullOrWhiteSpace(game.Config.DisplayName) ? game.Config.DataKey : game.Config.DisplayName;
}
