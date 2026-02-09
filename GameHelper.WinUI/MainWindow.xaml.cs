using GameHelper.WinUI.ViewModels;
using Microsoft.UI.Xaml;

namespace GameHelper.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainWindowViewModel(
            Services.ServiceLocator.GetRequiredService<ShellViewModel>(),
            Services.ServiceLocator.GetRequiredService<SettingsViewModel>(),
            Services.ServiceLocator.GetRequiredService<GamesViewModel>(),
            Services.ServiceLocator.GetRequiredService<StatsViewModel>());
        InitializeComponent();
    }
}
