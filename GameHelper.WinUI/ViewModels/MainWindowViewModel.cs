namespace GameHelper.WinUI.ViewModels;

public sealed class MainWindowViewModel
{
    public MainWindowViewModel(
        ShellViewModel shell,
        SettingsViewModel settings,
        GamesViewModel games,
        StatsViewModel stats)
    {
        Shell = shell;
        Settings = settings;
        Games = games;
        Stats = stats;
    }

    public ShellViewModel Shell { get; }

    public SettingsViewModel Settings { get; }

    public GamesViewModel Games { get; }

    public StatsViewModel Stats { get; }
}
