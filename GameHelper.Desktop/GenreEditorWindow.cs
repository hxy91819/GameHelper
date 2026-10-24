using System.Windows;
using System.Windows.Controls;
using GameHelper.Infrastructure.Artwork;

namespace GameHelper.Desktop;

internal sealed class GenreEditorWindow : Window
{
    internal GenreEditorWindow(LibraryViewModel model, LibraryGameItem game)
    {
        Title = "游戏类型 · " + game.Name;
        Width = 440; Height = 280; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = "用逗号分隔多个类型，留空表示未分类。", TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = string.Join("，", game.Genres), Margin = new Thickness(0, 16, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(input, "游戏类型，用逗号分隔");
        panel.Children.Add(input);
        var status = new TextBlock { Text = "手动类型会保留，不随 Steam 更新覆盖。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        status.SetResourceReference(ForegroundProperty, "Muted"); panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "保存类型", Margin = new Thickness(0, 0, 8, 0) };
        var automatic = new Button { Content = "恢复 Steam 自动类型" };
        buttons.Children.Add(save); buttons.Children.Add(automatic); panel.Children.Add(buttons);
        async Task Apply(string[]? values)
        {
            save.IsEnabled = automatic.IsEnabled = false;
            try { await model.SaveGenresAsync(game, values); Close(); }
            catch (Exception ex) { status.Text = "类型未能保存：" + ex.Message; save.IsEnabled = automatic.IsEnabled = true; }
        }
        save.Click += async (_, _) => await Apply(SteamGenreClient.Normalize(input.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries)));
        automatic.Click += async (_, _) => await Apply(null);
    }
}
