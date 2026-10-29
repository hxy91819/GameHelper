using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Ipc;

namespace GameHelper.Desktop;

public partial class App : Application
{
    internal DesktopTheme? Theme { get; private set; }
    private Mutex? _instance;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activationWait;
    private bool _ownsInstance;
    private EventWaitHandle? _consoleActivate;
    private RegisteredWaitHandle? _consoleWait;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? snapshot = null;
        string? check = null;
        var background = e.Args.Contains("--background", StringComparer.Ordinal);
        var restore = e.Args.Contains("--restore", StringComparer.Ordinal);
        try
        {
            snapshot = Option(e.Args, "--snapshot");
            check = Option(e.Args, "--check");
            var config = Option(e.Args, "--config") ?? Path.Combine(
                Environment.GetEnvironmentVariable("GAMEHELPER_DATA_DIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GameHelper", "config.yml");
            if (background)
            {
                var response = await RuntimeConnection.EnsureAsync(config);
                if (check is not null) await File.WriteAllTextAsync(check, JsonSerializer.Serialize(response));
                Shutdown(0);
                return;
            }
            if (check is not null)
            {
                var response = await new GameLibraryPipe(config).ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.List });
                await File.WriteAllTextAsync(check, JsonSerializer.Serialize(response));
                Shutdown(response.Success ? 0 : 1);
                return;
            }
            // Complete first activation before opening any legacy Desktop documents or claiming the UI mutex.
            if (snapshot is null && !e.Args.Contains("--no-start-host", StringComparer.Ordinal))
                await RuntimeConnection.EnsureAsync(config);
            await Task.Run(() => GameHelper.Infrastructure.Storage.Sqlite.SqliteDesktopStore.PrepareAsync(new(), config));
            var sideData = await DesktopSideData.LoadAsync(config, readOnly: snapshot is not null);
            Theme = new DesktopTheme(Resources, sideData.ThemePreferences);
            Theme.Load(sideData.IsLight);
            var ratingId = Option(e.Args, "--rate-session");
            if (ratingId is not null)
            {
                var library = new GameLibraryPipe(config);
                var response = await library.ExecuteAsync(new() { Operation = GameLibraryOperation.GetSessionRating, SessionId = Guid.Parse(ratingId) });
                if (!response.Success || response.RatedSession is null) throw new InvalidOperationException(response.Error);
                // Dedicated transient window may coexist with the main library; optimistic revision handles edits.
                MainWindow = new SessionRatingWindow(library, response.RatedSession);
                MainWindow.Show();
                return;
            }
            if (snapshot is null && !ClaimInstance(config, activate: !restore, console: e.Args.Contains("--console-mode"))) { Shutdown(0); return; }
            var model = new LibraryViewModel(new GameLibraryPipe(config), snapshot is null ? config : null, null, metadataConfigPath: config, sideData: sideData);
            var window = new MainWindow(model);
            window.ShowActivated = snapshot is null && !restore;
            if (snapshot is not null || (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMEHELPER_DATA_DIR"))
                && Environment.GetEnvironmentVariable("GAMEHELPER_TEST_OFFSCREEN") == "1")) WindowSnapshot.PrepareOffscreen(window);
            MainWindow = window;
            window.Show();
            await model.RefreshAsync();
            if (!model.Connected && window.IsVisible && snapshot is null && !e.Args.Contains("--no-start-host", StringComparer.Ordinal))
            {
                await RuntimeConnection.EnsureAsync(config);
                if (window.IsVisible) await model.RefreshAsync();
            }
            if (model.Connected) await model.LoadWeeklyAsync();
            if (snapshot is null && !restore && model.Connected)
            {
                var settings = await new GameLibraryPipe(config).ExecuteAsync(new() { Operation = GameLibraryOperation.GetSettings });
                if (e.Args.Contains("--console-mode") || settings.Settings?.ConsoleModeOnLibraryOpen == true) window.OpenConsoleMode();
            }
            if (snapshot is not null)
            {
                if (!model.Connected) throw new InvalidOperationException(model.Status);
                await window.CaptureAsync(snapshot);
                Shutdown(0);
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "GameHelper.Desktop-error.txt"), ex.ToString());
            if (snapshot is null && check is null && !background && !restore) MessageBox.Show(ex.Message, "GameHelper 游戏库", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private bool ClaimInstance(string config, bool activate, bool console = false)
    {
        var name = @"Local\" + GameLibraryPipe.NameFor(config) + ".Desktop";
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        _consoleActivate = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".ConsoleMode");
        _instance = new Mutex(false, name);
        try { _ownsInstance = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsInstance = true; }
        if (!_ownsInstance) { if (activate) { if (console) _consoleActivate.Set(); else _activate.Set(); } return false; }
        _consoleWait = ThreadPool.RegisterWaitForSingleObject(_consoleActivate, (_, _) => Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.OpenConsoleMode()), null, Timeout.Infinite, false);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) =>
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (MainWindow is null) return;
                if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
                MainWindow.Activate();
                try
                {
                    var settings = await new GameLibraryPipe(config).ExecuteAsync(new() { Operation = GameLibraryOperation.GetSettings });
                    if (settings.Settings?.ConsoleModeOnLibraryOpen == true) (MainWindow as MainWindow)?.OpenConsoleMode();
                }
                catch { /* Keep the desktop available when runtime is offline. */ }
            });
        }, null, Timeout.Infinite, false);
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _consoleWait?.Unregister(null); _consoleActivate?.Dispose();
        _activationWait?.Unregister(null);
        _activate?.Dispose();
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{name} 缺少参数。");
        return args[index + 1];
    }
}
