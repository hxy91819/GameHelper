using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Win32;

namespace GameHelper.Desktop;

public partial class GlobalSettingsView : UserControl
{
    private IGameLibrary? _library;
    private bool _busy;
    private GlobalSettingsViewModel? Draft => DataContext as GlobalSettingsViewModel;
    public GlobalSettingsView()
    {
        InitializeComponent();
        MonitorTypes.ItemsSource = Enum.GetValues<ProcessMonitorType>();
        SyncMethods.ItemsSource = new[] { "git", "api" };
    }
    public void Connect(IGameLibrary library) => _library = library;
    public Task EnsureLoadedAsync() => Draft is null ? LoadAsync() : Task.CompletedTask;
    private async Task<GameLibraryResponse> SendAsync(GameLibraryOperation operation, UpdateAppSettingsRequest? settings = null)
    {
        var response = await _library!.ExecuteAsync(new GameLibraryRequest { Operation = operation, Settings = settings });
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "操作失败，请重试。");
        return response;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _library is null) return;
        _busy = true; Form.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { Result.Text = ex.Message; }
        finally { _busy = false; Form.IsEnabled = Draft is not null; }
    }
    public Task LoadAsync() => RunAsync(async () =>
    {
        var response = await SendAsync(GameLibraryOperation.GetSettings);
        Apply(response.Settings ?? throw new InvalidOperationException("运行时没有返回设置，请更新后重试。"));
        Result.Text = "已载入全局设置。";
        await ShowSyncAsync(GameLibraryOperation.SyncStatus);
    });
    private void Apply(AppSettingsSnapshot settings)
    {
        DataContext = new GlobalSettingsViewModel(settings);
        Token.Password = settings.SyncSettings?.Token ?? string.Empty;
        UpdateRecipeSummary();
    }
    private void UpdateRecipeSummary() => Recipes.Text = Draft is null ? "" :
        "游戏：" + string.Join(" · ", Draft.MonitorMode.GamePresets.Select(p => $"{p.Code}={p.Value}")) +
        "\n舒适：" + string.Join(" · ", Draft.MonitorMode.ComfortPresets.Select(p => $"{p.Code}={p.Value}"));
    private static bool HasInvalidInput(DependencyObject parent)
    {
        if (Validation.GetHasError(parent)) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (HasInvalidInput(VisualTreeHelper.GetChild(parent, i))) return true;
        return false;
    }
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (Draft is null) return;
        if (HasInvalidInput(Form)) { Result.Text = "请修正无效的数字输入后保存。"; return; }
        await RunAsync(async () =>
        {
            var request = Draft.ToRequest(); request.SyncSettings!.Token = string.IsNullOrEmpty(Token.Password) ? null : Token.Password;
            var response = await SendAsync(GameLibraryOperation.UpdateSettings, request);
            var settings = response.Settings!; Apply(settings);
            Result.Text = "全局设置已保存。" + (settings.RestartRequired.Count > 0 ? "监控方式等启动项需重启后台后生效。" : "") + settings.ApplyWarning;
        });
    }
    private async void OnReload(object sender, RoutedEventArgs e) => await LoadAsync();
    private void OnChooseCe(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 Cheat Engine", Filter = "程序|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && Draft is not null)
        { Draft.CheatEnginePath = dialog.FileName; CePath.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); }
    }
    private void OnRecord(object sender, RoutedEventArgs e)
    {
        if (Draft is null || _library is null) return;
        var dialog = new DdcRecordingWindow(_library) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        Draft.MonitorMode.GamePresets = dialog.GamePresets;
        Draft.MonitorMode.ComfortPresets = dialog.ComfortPresets;
        UpdateRecipeSummary(); Result.Text = "已将录制配方放入草稿，点击保存全局设置后生效。";
    }
    private async Task ShowSyncAsync(GameLibraryOperation operation)
    {
        var response = await SendAsync(operation);
        var state = response.SyncStatus;
        SyncState.Text = state is null ? "暂无同步状态。" :
            $"{state.Repo} · {(state.Enabled ? "自动推送已开启" : "自动推送已关闭")}\n" +
            $"上次成功：{(state.LastSuccessUtc == default ? "尚未推送" : state.LastSuccessUtc.ToLocalTime().ToString("g"))}\n" +
            (state.ConfigError ?? state.LastError ?? (state.HasPendingData ? "有待推送数据。" : "当前没有待推送数据。"));
        if (operation != GameLibraryOperation.SyncStatus) Result.Text = response.Message;
    }
    private async void OnSyncStatus(object sender, RoutedEventArgs e) => await RunAsync(() => ShowSyncAsync(GameLibraryOperation.SyncStatus));
    private async void OnSyncValidate(object sender, RoutedEventArgs e) => await RunAsync(() => ShowSyncAsync(GameLibraryOperation.SyncValidate));
    private async void OnSyncPush(object sender, RoutedEventArgs e) => await RunAsync(() => ShowSyncAsync(GameLibraryOperation.SyncPush));
}
