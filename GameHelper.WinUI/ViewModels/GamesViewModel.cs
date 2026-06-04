using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.WinUI.ViewModels;

public partial class GamesViewModel : ObservableObject
{
    private readonly IGameCatalogService _gameCatalogService;

    [ObservableProperty]
    private GameEntry? selectedGame;

    public GamesViewModel(IGameCatalogService gameCatalogService)
    {
        _gameCatalogService = gameCatalogService;
        Refresh();
    }

    public ObservableCollection<GameEntry> Games { get; } = new();

    [RelayCommand]
    private void Refresh()
    {
        Games.Clear();
        foreach (var game in _gameCatalogService.GetAll())
        {
            Games.Add(game);
        }
    }

    [RelayCommand]
    private void Add()
    {
        _gameCatalogService.Add(new GameEntryUpsertRequest
        {
            ExecutableName = $"new-game-{DateTimeOffset.Now.ToUnixTimeSeconds()}.exe",
            DisplayName = "New Game",
            IsEnabled = true
        });

        Refresh();
    }

    [RelayCommand]
    private void Update()
    {
        if (SelectedGame is null)
        {
            return;
        }

        _gameCatalogService.Update(SelectedGame.DataKey, new GameEntryUpsertRequest
        {
            ExecutableName = SelectedGame.ExecutableName,
            ExecutablePath = SelectedGame.ExecutablePath,
            DisplayName = string.IsNullOrWhiteSpace(SelectedGame.DisplayName)
                ? SelectedGame.DataKey
                : $"{SelectedGame.DisplayName} (Updated)",
            IsEnabled = SelectedGame.IsEnabled,
            HdrEnabled = SelectedGame.HdrEnabled
        });

        Refresh();
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedGame is null)
        {
            return;
        }

        _gameCatalogService.Delete(SelectedGame.DataKey);
        Refresh();
    }
}
