using System.Windows;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
namespace GameHelper.Desktop;

public partial class SessionRatingWindow : Window
{
    private readonly SessionRatingEditorViewModel _model;
    public SessionRatingWindow(IGameLibrary library, RatedSession session)
    {
        InitializeComponent(); DataContext = _model = new(library, session);
        var stars = new SessionRatingStars(_model);
        stars.StarClicked += async value =>
        {
            if (_model.Busy) return;
            _model.Stars = value;
            await SaveAsync(false);
        };
        StarsHost.Content = stars;
    }
    private async Task SaveAsync(bool clear)
    {
        if (_model.Busy) return;
        Actions.IsEnabled = false;
        try { if (await _model.SaveAsync(clear)) Close(); }
        finally { Actions.IsEnabled = true; }
    }
    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void OnClear(object sender, RoutedEventArgs e) => await SaveAsync(true);
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    private async void OnReload(object sender, RoutedEventArgs e)
    { if (_model.Busy) return; Actions.IsEnabled = false; try { await _model.ReloadAsync(); } finally { Actions.IsEnabled = true; } }
}
