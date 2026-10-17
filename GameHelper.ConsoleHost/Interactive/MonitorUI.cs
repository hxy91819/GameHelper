using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GameHelper.ConsoleHost.Interactive
{
    /// <summary>
    /// Handles real-time monitor launch, user exit interaction, and session summary rendering.
    /// </summary>
    public sealed class MonitorUI
    {
        private readonly IHost _host;
        private readonly IAnsiConsole _console;
        private readonly PromptUI _promptUI;
        private readonly IStatisticsService _statisticsService;
        private readonly IMonitorControlService _monitorControlService;
        private readonly InteractiveScript? _script;
        private readonly Func<IHost, CancellationToken, Task> _monitorLoop;
        private readonly bool _dryRun;

        public MonitorUI(
            IHost host,
            IAnsiConsole console,
            PromptUI promptUI,
            IStatisticsService statisticsService,
            IMonitorControlService monitorControlService,
            InteractiveScript? script,
            Func<IHost, CancellationToken, Task> monitorLoop,
            bool dryRun)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _console = console ?? throw new ArgumentNullException(nameof(console));
            _promptUI = promptUI ?? throw new ArgumentNullException(nameof(promptUI));
            _statisticsService = statisticsService ?? throw new ArgumentNullException(nameof(statisticsService));
            _monitorControlService = monitorControlService ?? throw new ArgumentNullException(nameof(monitorControlService));
            _script = script;
            _monitorLoop = monitorLoop ?? throw new ArgumentNullException(nameof(monitorLoop));
            _dryRun = dryRun;
        }

        public async Task LaunchMonitorAsync()
        {
            var snapshotBefore = CaptureSessionSnapshot();

            var monitorRule = new Rule("[yellow]Live monitor[/]")
            {
                Style = new Style(Color.Grey),
                Justification = Justify.Left
            };
            _console.Write(monitorRule);

            RenderMonitorHistory(snapshotBefore);
            _console.WriteLine();

            _console.MarkupLine("[bold green]Starting monitor... Press Q at any time to return to the main menu.[/]");
            _console.WriteLine();

            using var monitorCts = new CancellationTokenSource();
            Task monitorLoopTask = Task.CompletedTask;
            var monitorStarted = false;
            var started = false;
            var exitSignalled = false;
            Exception? startException = null;
            Exception? runException = null;

            try
            {
                if (!_dryRun)
                {
                    _monitorControlService.Start();
                    monitorStarted = true;
                }
                started = true;

                monitorLoopTask = _monitorLoop(_host, monitorCts.Token);

                if (!_dryRun)
                {
                    await WaitForMonitorExitAsync(monitorCts.Token).ConfigureAwait(false);
                }

                exitSignalled = true;
            }
            catch (OperationCanceledException)
            {
                exitSignalled = true;
            }
            catch (Exception ex)
            {
                if (!started)
                {
                    startException = ex;
                }
                else
                {
                    runException = ex;
                }
            }
            finally
            {
                await monitorCts.CancelAsync().ConfigureAwait(false);

                try
                {
                    await monitorLoopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    runException ??= ex;
                }

                if (monitorStarted)
                {
                    try
                    {
                        _monitorControlService.Stop();
                    }
                    catch (Exception ex)
                    {
                        runException ??= ex;
                    }
                }
            }

            if (startException != null)
            {
                _console.MarkupLine($"[red]Failed to start monitor: {Markup.Escape(startException.Message)}[/]");
                _console.WriteLine();
                return;
            }

            if (runException != null)
            {
                var message = exitSignalled
                    ? $"Monitor stopped, but an exception occurred while handling it: {runException.Message}"
                    : $"An exception occurred during monitoring: {runException.Message}";
                _console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
                _console.WriteLine();
                return;
            }

            _console.MarkupLine("[grey]Monitor stopped; summarizing this session...[/]");
            _console.WriteLine();

            var snapshotAfter = CaptureSessionSnapshot();
            RenderSessionSummary(snapshotBefore, snapshotAfter);
            _console.WriteLine();
        }

        private async Task WaitForMonitorExitAsync(CancellationToken cancellationToken)
        {
            if (_script != null && _script.TryPeek<string>(out var scriptedCommand) && IsQuitCommand(scriptedCommand))
            {
                _script.TryDequeue(out string _);
                return;
            }

            if (Console.IsInputRedirected)
            {
                await WaitForExitByPromptAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var cancelSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ConsoleCancelEventHandler handler = (_, args) =>
            {
                args.Cancel = true;
                cancelSignal.TrySetResult(true);
            };

            Console.CancelKeyPress += handler;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (cancelSignal.Task.IsCompleted)
                    {
                        return;
                    }

                    try
                    {
                        if (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(intercept: true);
                            if (key.Key == ConsoleKey.Q)
                            {
                                return;
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        await WaitForExitByPromptAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    catch (IOException)
                    {
                        await WaitForExitByPromptAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    catch (PlatformNotSupportedException)
                    {
                        await WaitForExitByPromptAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }

        private Task WaitForExitByPromptAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var prompt = new TextPrompt<string>("Type [bold]Q[/] and press Enter to return to the main menu")
                    .AllowEmpty()
                    .DefaultValue(string.Empty);

                var input = _promptUI.Prompt(prompt);
                if (IsQuitCommand(input))
                {
                    break;
                }

                _console.MarkupLine("[yellow]Please enter Q to end monitoring.[/]");
            }

            return Task.CompletedTask;
        }

        private static bool IsQuitCommand(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && string.Equals(value.Trim(), "q", StringComparison.OrdinalIgnoreCase);
        }

        private void RenderMonitorHistory(SessionActivitySnapshot snapshot)
        {
            if (snapshot.Records.Count == 0)
            {
                var placeholder = new Panel(new Markup("[italic grey]No recent monitor sessions. A summary of new sessions will appear here after one completes.[/]"))
                {
                    Header = new PanelHeader("History preview"),
                    Border = BoxBorder.Rounded
                };
                _console.Write(placeholder);
                return;
            }

            // 复用监控启动前捕获的快照构建预览，避免同一份数据再次全量读取解析。
            var preview = _statisticsService.GetSessionActivityPreview(snapshot);

            if (preview.Games.Count == 0)
            {
                _console.Write(new Panel(
                    new Markup($"[italic grey]No playtime records in the last {preview.WindowDays} days. Older sessions are excluded from the preview.[/]"))
                {
                    Header = new PanelHeader("History preview"),
                    Border = BoxBorder.Rounded
                });
                return;
            }

            // 注意：表格必须单独放进 Panel。Spectre 0.47 的 Rows 在 Panel 内测量
            // 会把表格塌缩成省略号（Rows 里是 Markup/空 Text 时甚至测量死循环），
            // 因此趋势迷你图在 Panel 外逐行输出。
            _console.Write(new Panel(BuildGameSummaryTable(preview))
            {
                Header = new PanelHeader("History preview"),
                Border = BoxBorder.Rounded
            });

            _console.Write(BuildDailyTrendChart(preview));
        }

        private static Table BuildGameSummaryTable(SessionActivityPreview preview)
        {
            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("Game");
            table.AddColumn("Sessions");
            table.AddColumn("Total time");
            table.AddColumn("Last played");

            foreach (var game in preview.Games)
            {
                table.AddRow(
                    Markup.Escape(game.DisplayName),
                    game.SessionCount.ToString(CultureInfo.InvariantCulture),
                    DurationFormatter.Format(game.TotalMinutes),
                    FormatRecentTimestamp(game.LastEnd));
            }

            table.Caption($"Last {preview.WindowDays} days · {preview.SessionCount} sessions · {preview.Games.Count} games");
            return table;
        }

        private static IRenderable BuildDailyTrendChart(SessionActivityPreview preview)
        {
            // 几十天的趋势一行放不下同数量的横向条形；改用 8 行纵向块字符柱状迷你图：
            // 每天一列自底向上填充，高度正比当日分钟数，底行零值天标灰点，
            // 日期轴每 14 天一个刻度并固定标注末端日期。
            // 整体缩进 2 空格与上方 Panel 内容左对齐（边框 1 列 + 内边距 1 列）。
            var trend = preview.DailyTrend;
            if (trend.Count == 0)
            {
                return new Markup(string.Empty);
            }

            var trendMinutes = trend.Select(day => day.Minutes).ToArray();
            var rows = DailyTrendChartRenderer.BuildBarRows(trendMinutes);
            var totalMinutes = trend.Sum(day => day.Minutes);
            var trendStatus = FormatTrendStatus(
                DailyTrendChartRenderer.CompareHalves(trendMinutes),
                trend.Count / 2)
                + FormatGameCountStatus(preview.PreviousHalfGames, preview.RecentHalfGames);

            // 多行 Markup 直接写控制台是安全的；此前挂死只发生在它被 Rows/Panel 嵌套时。
            var lines = new List<string>
            {
                ChartIndent + $"[grey]Daily playtime, last {preview.WindowDays} days ({DurationFormatter.Format(totalMinutes)})[/]" + trendStatus,
            };
            for (var i = 0; i < rows.Count; i++)
            {
                var row = i == rows.Count - 1
                    ? ColorizeBaselineRow(rows[i])
                    : $"[green]{Markup.Escape(rows[i])}[/]";
                lines.Add(ChartIndent + row);
            }

            var axisChars = new char[trend.Count];
            Array.Fill(axisChars, ' ');
            foreach (var tick in DailyTrendChartRenderer.GetAxisTicks(trend.Count))
            {
                var label = trend[tick.DayIndex].Date.ToString("MM-dd", CultureInfo.InvariantCulture);
                label.CopyTo(0, axisChars, tick.Column, label.Length);
            }

            lines.Add(ChartIndent + $"[grey]{Markup.Escape(new string(axisChars).TrimEnd())}[/]");
            return new Markup(string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }

        /// <summary>
        /// 把半窗对比结果格式化为标题行尾部的状态文案：箭头 + "时长增加/减少" + 变化幅度，
        /// 增加/减少/基本持平分别着红/绿/灰（本面板服务于游玩管理，玩得更多越醒目）。
        /// 窗口无任何游玩时返回空串，不显示状态。
        /// </summary>
        private static string FormatTrendStatus(HalfWindowTrend comparison, int previousHalfDays)
        {
            if (!comparison.HasSignal)
            {
                return string.Empty;
            }

            var (arrow, color, word) = comparison.Direction switch
            {
                TrendDirection.Increasing => ("↑", "red", "Playtime up"),
                TrendDirection.Decreasing => ("↓", "green", "Playtime down"),
                _ => ("→", "grey", "Playtime steady")
            };

            // 持平不再标注幅度（阈值内的百分比与“基本持平”表述互相打架）；
            // 前半窗无任何游玩时百分比无定义，改用文字说明基线为零。
            var detail = comparison.Direction switch
            {
                TrendDirection.Steady => string.Empty,
                TrendDirection.Increasing when double.IsNaN(comparison.ChangePercent) => "(no prior playtime)",
                _ => $"({comparison.ChangePercent.ToString("+0;-0", CultureInfo.InvariantCulture)}% vs previous {previousHalfDays} days)"
            };

            return $" [{color}]{arrow} {word}[/][grey]{detail}[/]";
        }

        /// <summary>
        /// 把前后半窗的游玩游戏数量格式化为状态文案：箭头 + "游戏数量增加/减少" + 款数变化，
        /// 配色与时长状态一致；数量按整数直接比较（无持平阈值）。两半窗均无游戏时返回空串。
        /// </summary>
        private static string FormatGameCountStatus(int previousGames, int recentGames)
        {
            if (previousGames == 0 && recentGames == 0)
            {
                return string.Empty;
            }

            var (arrow, color, word) = recentGames > previousGames
                ? ("↑", "red", "Game count up")
                : recentGames < previousGames
                    ? ("↓", "green", "Game count down")
                    : ("→", "grey", "Game count steady");

            // 持平时两半窗数量相同，只给单个数量，避免 "5→5 款" 的冗余。
            var detail = recentGames == previousGames
                ? $"({recentGames} {(recentGames == 1 ? "game" : "games")})"
                : $"({previousGames}→{recentGames} games)";

            return $" [{color}]{arrow} {word}[/][grey]{detail}[/]";
        }

        private const string ChartIndent = "  ";

        private static string ColorizeBaselineRow(string row)
        {
            // 底行混合绿色柱体与灰色零值占位点，按连续同色段分色输出。
            var segments = new List<string>();
            var i = 0;
            while (i < row.Length)
            {
                var isZero = row[i] == DailyTrendChartRenderer.ZeroMark;
                var j = i + 1;
                while (j < row.Length && (row[j] == DailyTrendChartRenderer.ZeroMark) == isZero)
                {
                    j++;
                }

                var text = Markup.Escape(row[i..j]);
                segments.Add(isZero ? $"[grey]{text}[/]" : $"[green]{text}[/]");
                i = j;
            }

            return string.Concat(segments);
        }

        private void RenderSessionSummary(SessionActivitySnapshot before, SessionActivitySnapshot after)
        {
            var newSessions = after.Records
                .Where(record => !before.Keys.Contains(record.Key))
                .OrderBy(record => record.Start)
                .ToList();

            if (newSessions.Count == 0)
            {
                _console.MarkupLine("[italic grey]No new playtime records in this session.[/]");
                return;
            }

            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("Game");
            table.AddColumn("Start");
            table.AddColumn("End");
            table.AddColumn("Duration");

            foreach (var record in newSessions)
            {
                table.AddRow(
                    Markup.Escape(record.DisplayName),
                    FormatTimestamp(record.Start),
                    FormatTimestamp(record.End),
                    DurationFormatter.Format(record.DurationMinutes));
            }

            var totalMinutes = newSessions.Sum(r => r.DurationMinutes);
            table.AddEmptyRow();
            table.AddRow("[bold]TOTAL[/]", string.Empty, string.Empty, $"[bold]{DurationFormatter.Format(totalMinutes)}[/]");

            _console.Write(table);

            var aggregated = newSessions
                .GroupBy(r => r.DisplayName)
                .Select(g => new { g.Key, Minutes = g.Sum(r => r.DurationMinutes), Count = g.Count() })
                .OrderByDescending(x => x.Minutes)
                .ToList();

            var summaryText = string.Join(", ", aggregated.Select(g => $"{g.Key} {DurationFormatter.Format(g.Minutes)} ({g.Count} sessions)"));
            _console.MarkupLine($"[grey]Total of {newSessions.Count} session(s) ended: {summaryText}[/]");

            if (!string.IsNullOrWhiteSpace(after.Source))
            {
                _console.MarkupLine($"[grey]Data written to: {Markup.Escape(after.Source)}[/]");
            }
        }

        private SessionActivitySnapshot CaptureSessionSnapshot()
        {
            return _statisticsService.GetSessionActivitySnapshot();
        }

        private static string FormatTimestamp(DateTime timestamp)
        {
            if (timestamp.Kind == DateTimeKind.Utc)
            {
                timestamp = timestamp.ToLocalTime();
            }

            return timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private static string FormatRecentTimestamp(DateTime timestamp)
        {
            if (timestamp.Kind == DateTimeKind.Utc)
            {
                timestamp = timestamp.ToLocalTime();
            }

            return timestamp.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

    }
}
