using System.IO;
using GameHelper.WinUI.Services;
using Microsoft.UI.Xaml;

namespace GameHelper.WinUI;

public partial class App : Application
{
    private Window? _window;
    private readonly string _logPath;

    public App()
    {
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameHelper",
            "winui-errors.log");

        InitializeComponent();
        ServiceLocator.Initialize();

        UnhandledException += (_, e) =>
        {
            Log($"UnhandledException: {e.Message}\n{e.Exception}");
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log($"AppDomain.UnhandledException: {e.ExceptionObject}");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("GameHelper.WinUI requires Windows.");
        }

        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log($"Failed to launch MainWindow: {ex}");
            throw;
        }
    }

    private void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(_logPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures.
        }
    }
}
