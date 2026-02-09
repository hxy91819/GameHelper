using GameHelper.WinUI.Services;
using Microsoft.UI.Xaml;

namespace GameHelper.WinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        ServiceLocator.Initialize();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("GameHelper.WinUI requires Windows.");
        }

        _window = new MainWindow();
        _window.Activate();
    }
}
