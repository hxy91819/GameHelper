using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.WinUI.ViewModels;

public partial class StatsViewModel : ObservableObject
{
    private readonly IStatisticsService _statisticsService;

    public StatsViewModel(IStatisticsService statisticsService)
    {
        _statisticsService = statisticsService;
        Refresh();
    }

    public ObservableCollection<GameStatsSummary> Stats { get; } = new();

    [RelayCommand]
    private void Refresh()
    {
        Stats.Clear();
        foreach (var item in _statisticsService.GetOverview())
        {
            Stats.Add(item);
        }
    }
}
