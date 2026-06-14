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

            var monitorRule = new Rule("[yellow]实时监控[/]")
            {
                Style = new Style(Color.Grey),
                Justification = Justify.Left
            };
            _console.Write(monitorRule);

            RenderMonitorHistory(snapshotBefore);
            _console.WriteLine();

            _console.MarkupLine("[bold green]正在启动监控... 按 Q 键可随时返回主菜单。[/]");
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
                monitorCts.Cancel();

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
                _console.MarkupLine($"[red]启动监控失败：{Markup.Escape(startException.Message)}[/]");
                _console.WriteLine();
                return;
            }

            if (runException != null)
            {
                var message = exitSignalled
                    ? $"监控已停止，但处理过程中出现异常：{runException.Message}"
                    : $"监控过程中出现异常：{runException.Message}";
                _console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
                _console.WriteLine();
                return;
            }

            _console.MarkupLine("[grey]监控已停止，正在汇总本次游玩...[/]");
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
                var prompt = new TextPrompt<string>("输入 [bold]Q[/] 并按 Enter 返回主菜单")
                    .AllowEmpty()
                    .DefaultValue(string.Empty);

                var input = _promptUI.Prompt(prompt);
                if (IsQuitCommand(input))
                {
                    break;
                }

                _console.MarkupLine("[yellow]请输入 Q 键以结束监控。[/]");
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
                var placeholder = new Panel(new Markup("[italic grey]最近暂无监控记录。完成一次会话后，会在此展示新增摘要。[/]"))
                {
                    Header = new PanelHeader("历史记录预览"),
                    Border = BoxBorder.Rounded
                };
                _console.Write(placeholder);
                return;
            }

            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("游戏");
            table.AddColumn("结束时间");
            table.AddColumn("时长");

            foreach (var record in snapshot.Records
                .OrderByDescending(r => r.End)
                .Take(5))
            {
                table.AddRow(
                    Markup.Escape(record.DisplayName),
                    FormatTimestamp(record.End),
                    DurationFormatter.Format(record.DurationMinutes));
            }

            table.Caption("最近 5 条记录");

            _console.Write(new Panel(table)
            {
                Header = new PanelHeader("历史记录预览"),
                Border = BoxBorder.Rounded
            });
        }

        private void RenderSessionSummary(SessionActivitySnapshot before, SessionActivitySnapshot after)
        {
            var newSessions = after.Records
                .Where(record => !before.Keys.Contains(record.Key))
                .OrderBy(record => record.Start)
                .ToList();

            if (newSessions.Count == 0)
            {
                _console.MarkupLine("[italic grey]本次会话没有产生新的游玩记录。[/]");
                return;
            }

            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("游戏");
            table.AddColumn("开始");
            table.AddColumn("结束");
            table.AddColumn("本次时长");

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

            var summaryText = string.Join("，", aggregated.Select(g => $"{g.Key} {DurationFormatter.Format(g.Minutes)}（{g.Count} 次）"));
            _console.MarkupLine($"[grey]本次共计 {newSessions.Count} 次游戏结束：{summaryText}[/]");

            if (!string.IsNullOrWhiteSpace(after.Source))
            {
                _console.MarkupLine($"[grey]数据已写入：{Markup.Escape(after.Source)}[/]");
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

    }
}
