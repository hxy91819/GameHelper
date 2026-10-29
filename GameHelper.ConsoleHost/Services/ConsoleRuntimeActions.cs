using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;

namespace GameHelper.ConsoleHost.Services;

/// <summary>Transient eligibility delivered by the existing observed-completion notifier.</summary>
internal sealed class ConsoleRatingInvitations(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> pending = new();
    internal void Add(Guid id)
    {
        foreach (var pair in pending.Where(p => p.Value < clock.GetUtcNow().AddDays(-1))) pending.TryRemove(pair.Key, out _);
        pending[id] = clock.GetUtcNow();
    }
    internal bool Contains(Guid id) => pending.ContainsKey(id);
    internal void Remove(Guid id) => pending.TryRemove(id, out _);
}

/// <summary>IPC adapter to existing tray actions and runtime-owned process identities.</summary>
internal sealed class ConsoleRuntimeActions(IGameLibrary library, IGameLaunchCoordinator launch, IHdrController hdr,
    IBreakReminderService reminders, TimeProvider clock, SessionRatings ratings, ConsoleRatingInvitations invitations)
{
    internal static bool Handles(GameLibraryOperation op) => op is GameLibraryOperation.ConsoleMenu or GameLibraryOperation.ConsoleAction
        or GameLibraryOperation.FocusGame or GameLibraryOperation.StopGame or GameLibraryOperation.ConsoleRating;
    private TrayMenuService Menus => new(library, new TrayHdrControl(hdr), reminders, clock, () => { }, () => { });
    public async Task<GameLibraryResponse> ExecuteAsync(GameLibraryRequest request, CancellationToken token)
    {
        if (request.Operation == GameLibraryOperation.ConsoleMenu)
        {
            var rows = await Menus.GetMenuAsync(token);
            return new() { Success = true, ConsoleMenu = rows.Where(r => r.Action is TrayMenuAction.ToggleHdr or
                TrayMenuAction.Pause30Minutes or TrayMenuAction.ResumeReminders).ToArray() };
        }
        if (request.Operation == GameLibraryOperation.ConsoleAction)
        {
            if (request.TrayAction is not (TrayMenuAction.ToggleHdr or TrayMenuAction.Pause30Minutes or TrayMenuAction.ResumeReminders))
                throw new ArgumentException("不支持的主机菜单动作。");
            var item = (await Menus.GetMenuAsync(token)).FirstOrDefault(r => r.Action == request.TrayAction && r.Enabled)
                ?? throw new InvalidOperationException("该操作当前不可用，请重新打开菜单。");
            await Menus.DispatchAsync(item, token); return new() { Success = true };
        }
        if (request.Operation == GameLibraryOperation.ConsoleRating)
        {
            var id = request.SessionId ?? throw new ArgumentException("缺少会话身份。");
            if (!invitations.Contains(id)) return new() { Success = true };
            var session = ratings.Get(id); invitations.Remove(id);
            return new() { Success = true, RatedSession = session.Rating is null ? session : null };
        }
        var processes = launch.GetRunningProcesses().Where(p => p.DataKey == request.DataKey && p.SessionId == request.SessionId).ToArray();
        if (processes.Length == 0) throw new InvalidOperationException("该游戏会话已结束。");
        var acted = false;
        foreach (var entry in processes)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(entry.ProcessId);
                // Never act on a recycled PID or an identity the monitor could not verify.
                if (entry.StartedAtUtc is null || process.StartTime.ToUniversalTime() != entry.StartedAtUtc || process.HasExited) continue;
                if (request.Operation == GameLibraryOperation.StopGame) { process.Kill(); acted = true; }
                else if (request.Operation == GameLibraryOperation.FocusGame && process.MainWindowHandle != 0)
                { ShowWindow(process.MainWindowHandle, 9); acted = SetForegroundWindow(process.MainWindowHandle); if (acted) break; }
            }
            catch (ArgumentException) { /* Process exited between snapshot and lookup. */ }
        }
        if (!acted) throw new InvalidOperationException("无法操作该游戏窗口或进程；请刷新后重试。");
        return new() { Success = true };
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
