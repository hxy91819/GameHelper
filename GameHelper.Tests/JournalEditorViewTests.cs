using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class JournalEditorViewTests
{
    [WindowsOnlyFact]
    public Task Editor_RendersMultiSelectTagsAndLongerWrappingNote() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-journal-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LibraryJournalStore(Path.Combine(root, "config.yml"));
            store.Save("game", new("已通关,全成就", new DateOnly(2032, 2, 29), "完整旅程"));
            var editor = new JournalEditor(new JournalEditorViewModel(store, "game", "Dispatch", new Clock()));
            editor.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/GameHelper.Desktop;component/Themes/Dark.xaml", UriKind.Relative)));
            editor.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/GameHelper.Desktop;component/Themes/FormControls.xaml", UriKind.Relative)));
            WindowSnapshot.PrepareOffscreen(editor);
            try
            {
                editor.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var tags = Descendants<CheckBox>(editor).ToList();
                Assert.Equal(new[] { "未开始", "在玩", "已通关", "全成就", "搁置" }, tags.Select(tag => tag.Content?.ToString() ?? ""));
                Assert.True(tags[2].IsChecked);
                Assert.True(tags[3].IsChecked);

                Click(tags[0]);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("未开始", ((JournalEditorViewModel)editor.DataContext).Status);
                Assert.True(tags[0].IsChecked);
                Assert.All(tags.Skip(1), tag => Assert.False(tag.IsChecked));

                Click(tags[4]);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("搁置", ((JournalEditorViewModel)editor.DataContext).Status);
                Assert.True(tags[4].IsChecked);
                Assert.False(tags[0].IsChecked);
                Assert.False(((JournalEditorViewModel)editor.DataContext).IsCompleted);

                var note = Assert.Single(Descendants<TextBox>(editor), text => AutomationProperties.GetName(text) == "私人短评");
                Assert.Equal(500, note.MaxLength);
                Assert.Equal(112, note.Height);
                Assert.Equal(TextWrapping.Wrap, note.TextWrapping);
                Assert.Equal("完整旅程", note.Text);
                Assert.Contains("4 / 500", Descendants<TextBlock>(editor).Select(text => text.Text));

                if (Environment.GetEnvironmentVariable("GAMEHELPER_TEST_VISUAL_OUTPUT") is { Length: > 0 } output)
                {
                    Directory.CreateDirectory(output);
                    WindowSnapshot.Save(editor, Path.Combine(output, "journal-editor.png"));
                }
            }
            finally { editor.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    /// <summary>Same order as ToggleButton.OnClick: flip IsChecked locally, then raise Click for the window handler.</summary>
    private static void Click(CheckBox box)
    {
        // Real clicks change the current value; assigning IsChecked would replace the OneWay binding.
        box.SetCurrentValue(ToggleButton.IsCheckedProperty, box.IsChecked != true);
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2032, 2, 29, 18, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static Task OnSta(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
