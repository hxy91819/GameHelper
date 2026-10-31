using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace GameHelper.Tests;

public sealed class DesktopFormControlTests
{
    [WindowsOnlyFact]
    public Task SharedDateAndChoiceTemplatesKeepPopupSelectionAndCalendarViews() => OnDispatcher(async () =>
    {
        var window = new Window { Width = 420, Height = 240, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/GameHelper.Desktop;component/Themes/Dark.xaml", UriKind.Relative)));
        window.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/GameHelper.Desktop;component/Themes/FormControls.xaml", UriKind.Relative)));
        var choices = new ComboBox { ItemsSource = new[] { "未开始", "已通关" }, SelectedIndex = 0 };
        var date = new DatePicker(); var panel = new StackPanel(); panel.Children.Add(choices); panel.Children.Add(date); window.Content = panel;
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            choices.IsDropDownOpen = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = Assert.IsType<Popup>(choices.Template.FindName("PART_Popup", choices)); Assert.True(list.IsOpen);
            choices.SelectedIndex = 1; choices.IsDropDownOpen = false; Assert.Equal("已通关", choices.SelectedItem);
            date.IsDropDownOpen = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var popup = Assert.IsType<Popup>(date.Template.FindName("PART_Popup", date));
            var calendar = Assert.IsType<Calendar>(popup.Child); calendar.ApplyTemplate();
            var item = Assert.IsType<CalendarItem>(calendar.Template.FindName("PART_CalendarItem", calendar)); item.ApplyTemplate();
            Assert.NotNull(item.Template.Resources[CalendarItem.DayTitleTemplateResourceKey]);
            var month = Assert.IsType<Grid>(item.Template.FindName("PART_MonthView", item));
            var year = Assert.IsType<Grid>(item.Template.FindName("PART_YearView", item));
            calendar.DisplayMode = CalendarMode.Year; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Visible, year.Visibility); Assert.Equal(Visibility.Hidden, month.Visibility);
            calendar.DisplayMode = CalendarMode.Decade; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Assert.Equal(Visibility.Visible, year.Visibility);
            calendar.DisplayMode = CalendarMode.Month; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Assert.Equal(Visibility.Visible, month.Visibility);
            calendar.SelectedDate = new DateTime(2026, 10, 15); date.IsDropDownOpen = false;
            Assert.Equal(calendar.SelectedDate, date.SelectedDate);
            date.IsEnabled = false; Assert.Equal(0.45, date.Opacity);
        }
        finally { window.Close(); }
    });

    private static Task OnDispatcher(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); done.SetResult(); }
                catch (Exception ex) { done.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
