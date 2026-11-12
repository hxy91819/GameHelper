using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class PrivateCollectionPasswordViewTests
{
    [WindowsOnlyFact]
    public Task SetupDialogWritesPasswordAndUnlocksTheCollection() => OnSta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-private-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var access = new PrivateCollectionAccess(new PrivateCollectionPasswordStore(Path.Combine(root, "config.yml")));
            var dialog = new PrivateCollectionPasswordWindow(access, PrivateCollectionPasswordMode.Setup);
            Prepare(dialog);
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                Capture(dialog, "private-password-setup.png");
                dialog.NewPassword.Password = "abcd12";
                dialog.ConfirmPassword.Password = "ABCD12";
                Click(dialog, "设置密码");
            }), DispatcherPriority.ApplicationIdle);
            Assert.True(dialog.ShowDialog());
            Assert.True(access.IsUnlocked);
            Assert.True(access.Store.HasPassword);
        }
        finally { Directory.Delete(root, true); }
    });

    [WindowsOnlyFact]
    public Task WrongPasswordKeepsTheDialogOpenWithAnInlineError() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-private-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var access = new PrivateCollectionAccess(new PrivateCollectionPasswordStore(Path.Combine(root, "config.yml")));
            access.SetPassword("abcd12");
            access.Lock();
            var dialog = new PrivateCollectionPasswordWindow(access, PrivateCollectionPasswordMode.Enter);
            Prepare(dialog);
            try
            {
                dialog.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Capture(dialog, "private-password-enter.png");
                dialog.CurrentPassword.Password = "wrong1";
                Click(dialog, "解锁");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Capture(dialog, "private-password-wrong.png");
                Assert.True(dialog.IsVisible);
                Assert.False(access.IsUnlocked);
                Assert.Equal("密码不正确，请重试。", dialog.ErrorText.Text);
                Assert.Equal(string.Empty, dialog.CurrentPassword.Password);
            }
            finally { dialog.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    private static void Prepare(Window window)
    {
        window.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/GameHelper.Desktop;component/Themes/Dark.xaml", UriKind.Relative)));
        window.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/GameHelper.Desktop;component/Themes/FormControls.xaml", UriKind.Relative)));
        WindowSnapshot.PrepareOffscreen(window);
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("GAMEHELPER_TEST_VISUAL_OUTPUT") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        WindowSnapshot.Save(window, Path.Combine(output, name));
    }

    private static void Click(Window window, string content)
    {
        var button = Descendants<Button>(window).Single(button => button.Content as string == content);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
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
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static Task OnSta(Action test) => OnSta(() => { test(); return Task.CompletedTask; });
}
