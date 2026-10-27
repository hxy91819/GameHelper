using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Reports;
using Microsoft.Win32;

namespace GameHelper.Desktop;

/// <summary>Report presentation inside the archive; raw history stays in the runtime.</summary>
public partial class PlaytimeReportView : UserControl
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private IGameLibrary? _library;
    private Func<PlaytimeReport, PlaytimeReport>? _enrichReport;
    private PlaytimeReport? _report;
    private CancellationTokenSource? _pending;
    private string? _commentary;

    public PlaytimeReportView()
    {
        InitializeComponent();
        Year.Text = TimeProvider.System.GetLocalNow().Year.ToString();
        Unloaded += (_, _) => _pending?.Cancel();
    }
    public void Connect(IGameLibrary library, Func<PlaytimeReport, PlaytimeReport>? enrichReport = null)
    { _library = library; _enrichReport = enrichReport; }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_pending is not null || _library is null) return;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        Actions.IsEnabled = false;
        try { await action(pending.Token); }
        catch (OperationCanceledException) { Status.Text = "操作已取消。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _pending = null; Actions.IsEnabled = true; }
    }
    private async void OnGenerate(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (!int.TryParse(Year.Text, out var year)) throw new ArgumentException("请输入有效年份。");
        var period = new ReportPeriod(year, Period.SelectedIndex == 0 ? null : Period.SelectedIndex);
        _ = period.Start;
        Status.Text = "正在读取历史并生成统计…";
        var response = await _library!.ExecuteAsync(new() { Operation = GameLibraryOperation.Report, ReportPeriod = period }, token);
        token.ThrowIfCancellationRequested();
        if (!response.Success || response.Report is null) throw new InvalidOperationException(response.Error ?? "无法生成报告。");
        _report = _enrichReport?.Invoke(response.Report) ?? response.Report; _commentary = null;
        Body.Text = PlaytimeReportMarkdown.Render(_report);
        BodyPanel.Visibility = Visibility.Visible; BodyPanel.IsExpanded = true;
        CommentButton.IsEnabled = ExportButton.IsEnabled = true;
        Status.Text = $"{period}报告已生成。{(_report.IsPartial ? "本期尚未结束。" : "")}";
    });
    private async void OnComment(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (_report is null) return;
        var response = await _library!.ExecuteAsync(new() { Operation = GameLibraryOperation.GetSettings }, token);
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "无法读取点评设置。");
        var settings = response.Settings?.Report?.Llm ?? throw new InvalidOperationException("请先在全局设置配置报告点评接口和模型。");
        Status.Text = "正在生成点评（最多 60 秒）…";
        var commentary = await new ReportCommentaryClient(Http).GenerateAsync(_report, settings, token);
        token.ThrowIfCancellationRequested();
        _commentary = commentary;
        Body.Text = PlaytimeReportMarkdown.Render(_report, _commentary);
        Status.Text = "点评已生成；统计仍以程序计算为准。";
    });
    private async void OnExport(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (_report is null) return;
        var dialog = new SaveFileDialog { Filter = "Markdown|*.md", FileName = $"playtime-{_report.Period.Year}" +
            (_report.Period.Quarter is { } quarter ? $"-Q{quarter}" : "") + ".md" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await File.WriteAllTextAsync(dialog.FileName, PlaytimeReportMarkdown.Render(_report, _commentary), new UTF8Encoding(false), token);
        Status.Text = "Markdown 报告已导出。";
    });
}
