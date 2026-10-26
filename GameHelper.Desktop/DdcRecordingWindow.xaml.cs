using System.Windows;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

public partial class DdcRecordingWindow : Window
{
    private readonly IGameLibrary _library;
    private MonitorModeSnapshot? _before;
    private bool _busy;
    private readonly CancellationTokenSource _lifetime = new();
    public List<MonitorModePresetData> GamePresets { get; private set; } = [];
    public List<MonitorModePresetData> ComfortPresets { get; private set; } = [];
    public DdcRecordingWindow(IGameLibrary library)
    {
        _library = library; InitializeComponent();
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }
    private async void OnCapture(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; Capture.IsEnabled = false; Accept.IsEnabled = false;
        Status.Text = "正在读取候选码 0xDC、0xE2、0xF0、0x14…";
        try
        {
            var response = await _library.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.DdcSnapshot }, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            if (!response.Success || response.DdcSnapshot is null) throw new InvalidOperationException(response.Error ?? "无法读取显示器。");
            if (_before is null)
            {
                _before = response.DdcSnapshot;
                Step.Text = "2 / 2 · 游戏模式";
                Instruction.Text = "现在用同一台显示器的实体菜单切到游戏模式，然后再次读取。请保持连接与 HDR 状态不变。";
                Capture.Content = "读取游戏模式";
                Status.Text = "舒适模式已读取：" + string.Join(" · ", _before.Values);
            }
            else
            {
                var after = response.DdcSnapshot;
                var game = MonitorModeSnapshot.Difference(_before, after);
                var comfort = MonitorModeSnapshot.Difference(after, _before);
                if (game.Count == 0) throw new InvalidOperationException("候选码没有可用变化，请确认已经切换模式后重试。未成功读取的码不会进入配方。");
                GamePresets = ToPresets(game); ComfortPresets = ToPresets(comfort);
                Status.Text = "游戏配方：" + string.Join(" · ", game) + "\n舒适配方：" + string.Join(" · ", comfort) + "\n使用后仍需在全局设置页保存。";
                Accept.IsEnabled = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _busy = false; Capture.IsEnabled = true; }
    }
    private static List<MonitorModePresetData> ToPresets(IReadOnlyList<MonitorModeWrite> writes) =>
        writes.Select(write => new MonitorModePresetData { Code = $"{write.Code:X2}", Value = $"0x{write.Value:X2}" }).ToList();
    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _before = null; Accept.IsEnabled = false; Step.Text = "1 / 2 · 舒适模式";
        Instruction.Text = "关闭所有显示器 HDR，并用显示器实体菜单切到舒适模式后读取。";
        Capture.Content = "读取舒适模式"; Status.Text = "";
    }
    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
}
