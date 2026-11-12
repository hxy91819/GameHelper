using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GameHelper.Desktop;

internal enum PrivateCollectionPasswordMode { Enter, Setup, Manage }

/// <summary>Password prompt for entering and managing the private collection.</summary>
internal sealed class PrivateCollectionPasswordWindow : Window
{
    private readonly PrivateCollectionAccess _access;
    private readonly PrivateCollectionPasswordForm _form;
    private readonly PasswordBox _current = Field("当前密码");
    private readonly PasswordBox _next = Field("新密码");
    private readonly PasswordBox _confirm = Field("确认新密码");
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _panel = new() { Margin = new(24) };

    internal PrivateCollectionPasswordWindow(PrivateCollectionAccess access, PrivateCollectionPasswordMode mode)
    {
        _access = access;
        _form = new(access);
        Title = "私人收藏 · 密码";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Page");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        Content = _panel;
        if (mode == PrivateCollectionPasswordMode.Enter) BuildEnter();
        else BuildManage();
    }

    internal PasswordBox CurrentPassword => _current;
    internal PasswordBox NewPassword => _next;
    internal PasswordBox ConfirmPassword => _confirm;
    internal TextBlock ErrorText => _error;

    /// <summary>Requires a password, or lets the first private-collection visit create one.</summary>
    internal static bool Enter(Window owner, PrivateCollectionAccess access)
    {
        var dialog = new PrivateCollectionPasswordWindow(access,
            access.HasPassword ? PrivateCollectionPasswordMode.Enter : PrivateCollectionPasswordMode.Setup) { Owner = owner };
        dialog.ShowDialog();
        return access.IsUnlocked;
    }

    internal static void Manage(Window owner, PrivateCollectionAccess access) =>
        new PrivateCollectionPasswordWindow(access, PrivateCollectionPasswordMode.Manage) { Owner = owner }.ShowDialog();

    private void BuildEnter()
    {
        Heading("输入密码");
        Description("私人收藏已启用密码保护。解锁后可浏览本分类；返回普通游戏库后重新锁定。");
        _panel.Children.Add(Label("密码"));
        _panel.Children.Add(_current);
        _current.MaxLength = PrivateCollectionPasswordStore.MaximumLength;
        _panel.Children.Add(_error);
        AddButtons(("取消", (_, _) => Close(), false, false), ("解锁", OnUnlock, true, false));
        Loaded += (_, _) => _current.Focus();
    }

    private void BuildManage()
    {
        Heading(_access.HasPassword ? "私人收藏密码" : "设置私人收藏密码");
        if (!_access.HasPassword)
        {
            Description("首次进入私人收藏需要设置密码。密码只保存在本机，不随 sync 上传；4–32 位字母或数字，不区分大小写。");
            AddNewPasswordFields();
            _panel.Children.Add(_error);
            AddButtons(("取消", (_, _) => Close(), false, false), ("设置密码", OnSetup, true, false));
            Loaded += (_, _) => _next.Focus();
            return;
        }
        Description("修改或取消密码都需要先输入当前密码。密码只保存在本机。");
        _panel.Children.Add(Label("当前密码"));
        _panel.Children.Add(_current);
        _current.MaxLength = PrivateCollectionPasswordStore.MaximumLength;
        AddNewPasswordFields();
        _panel.Children.Add(_error);
        AddButtons(("关闭", (_, _) => Close(), false, false), ("取消密码保护", OnRemove, false, true), ("保存新密码", OnChange, true, false));
        Loaded += (_, _) => _current.Focus();
    }

    private void AddNewPasswordFields()
    {
        _panel.Children.Add(Label("新密码"));
        _panel.Children.Add(_next);
        _panel.Children.Add(Label("确认新密码"));
        _panel.Children.Add(_confirm);
        _next.MaxLength = _confirm.MaxLength = PrivateCollectionPasswordStore.MaximumLength;
    }

    private void OnUnlock(object sender, RoutedEventArgs e)
    {
        if (_form.Unlock(_current.Password)) { DialogResult = true; return; }
        Fail(_form.Error!);
        _current.Clear();
        _current.Focus();
    }

    private void OnSetup(object sender, RoutedEventArgs e)
    {
        if (_form.Setup(_next.Password, _confirm.Password)) DialogResult = true;
        else Fail(_form.Error!);
    }

    private void OnChange(object sender, RoutedEventArgs e)
    {
        if (_form.Change(_current.Password, _next.Password, _confirm.Password)) DialogResult = true;
        else Fail(_form.Error!);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_current.Password.Length == 0) { Fail("请输入当前密码。"); return; }
        if (MessageBox.Show(this, "取消后进入私人收藏不再需要密码，确定吗？", "私人收藏密码",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_form.Remove(_current.Password)) DialogResult = true;
        else Fail(_form.Error!);
    }

    private void Fail(string message) => _error.Text = message;

    private void Heading(string text) => _panel.Children.Add(new TextBlock { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold });

    private void Description(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _panel.Children.Add(block);
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(0, 14, 0, 6) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return block;
    }

    private static PasswordBox Field(string automationName)
    {
        var box = new PasswordBox { Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 0, 0, 2) };
        box.SetResourceReference(BackgroundProperty, "Input");
        box.SetResourceReference(ForegroundProperty, "TextPrimary");
        box.SetResourceReference(BorderBrushProperty, "Border");
        AutomationProperties.SetName(box, automationName);
        return box;
    }

    private void AddButtons(params (string Text, RoutedEventHandler Click, bool Primary, bool Danger)[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var (text, click, primary, danger) in buttons)
        {
            var button = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), MinWidth = 96, IsDefault = primary };
            if (!primary) button.IsCancel = text == "取消";
            if (danger) button.SetResourceReference(BackgroundProperty, "Danger");
            button.Click += click;
            row.Children.Add(button);
        }
        _panel.Children.Add(row);
    }
}
