using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GameHelper.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;
using Xunit.Abstractions;

namespace GameHelper.Tests.Interactive
{
    public class SparklineRenderTests
    {
        private readonly ITestOutputHelper _output;

        public SparklineRenderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static SessionActivityPreview CreatePreview()
        {
            var today = DateTime.Now.Date;
            var trend = new List<DailyPlaytimeSummary>();
            for (int i = 0; i < 14; i++)
            {
                var date = today.AddDays(-(13 - i));
                long minutes = i switch
                {
                    3 => 495,
                    4 => 118,
                    7 => 68,
                    9 => 104,
                    12 => 100,
                    _ => 0
                };
                trend.Add(new DailyPlaytimeSummary(date, minutes));
            }

            return new SessionActivityPreview(
                new List<SessionGameSummary> { new("gulong", "古龙风云录", 2, 90, today) },
                trend, 2, 14, "test.csv", 1, 1);
        }

        private static string RenderWithTimeout(IRenderable renderable, string stepName)
        {
            var console = new TestConsole();
            console.Profile.Capabilities.Ansi = false;
            // 宽度对齐 InteractiveShellTests 的 120 列：英文状态行更长，
            // 100 列会把 "(2→4 games)" 这类断言目标折行拆开。
            console.Profile.Width = 120;

            var renderTask = Task.Run(() => console.Write(renderable));
            if (!renderTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException($"Rendering hung at step: {stepName}");
            }

            return console.Output;
        }

        [Fact]
        public void Render_14DayPreview_StepByStep()
        {
            var preview = CreatePreview();

            var uiType = typeof(GameHelper.ConsoleHost.Interactive.MonitorUI);
            var chartMethod = uiType.GetMethod(
                "BuildDailyTrendChart",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var tableMethod = uiType.GetMethod(
                "BuildGameSummaryTable",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            var table = (IRenderable)tableMethod!.Invoke(null, new object[] { preview })!;
            var chart = (IRenderable)chartMethod!.Invoke(null, new object[] { preview })!;

            // 1. sparkline 多行 Markup 单独渲染
            var out1 = RenderWithTimeout(chart, "chart markup alone");
            _output.WriteLine("[1 chart]\n" + out1);

            // 内容断言：sparkline 图形、总时长标注、起止日期轴都要出现。
            // 趋势状态：前半窗 0..6 共 613min、后半窗 7..13 共 272min → 近 7 天较前 7 天 -56%
            //（(272-613)/613*100 = -55.63，四舍五入 -56）；半窗游戏数 1→1 款持平。
            Assert.Contains("█", out1);
            Assert.Contains("Daily playtime", out1);
            Assert.Contains(preview.DailyTrend[0].Date.ToString("MM-dd", CultureInfo.InvariantCulture), out1);
            Assert.Contains(preview.DailyTrend[^1].Date.ToString("MM-dd", CultureInfo.InvariantCulture), out1);
            Assert.Contains("↓ Playtime down", out1);
            Assert.Contains("-56% vs previous 7 days", out1);
            Assert.Contains("→ Game count steady", out1);
            Assert.Contains("(1 game)", out1);

            // 2. 表格单独入 Panel
            var out2 = RenderWithTimeout(new Panel(table) { Header = new PanelHeader("History preview"), Border = BoxBorder.Rounded }, "panel(table)");
            _output.WriteLine("[2 panel]\n" + out2);

            // 3. 最终组合：Panel(table) + 直接输出多行 Markup（MonitorUI 实际路径）
            var console3 = new TestConsole();
            console3.Profile.Capabilities.Ansi = false;
            console3.Profile.Width = 120;
            var renderTask = Task.Run(() =>
            {
                console3.Write(new Panel(table) { Header = new PanelHeader("History preview"), Border = BoxBorder.Rounded });
                console3.Write(chart);
            });
            if (!renderTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Rendering hung at step: final composition");
            }
            _output.WriteLine("[3 final]\n" + console3.Output);
            Assert.Contains("History preview", console3.Output);
            Assert.Contains("last 14 days", console3.Output);
        }

        [Fact]
        public void Render_AllZeroTrend_NoDivideByZero()
        {
            // 全 0 天的窗口不能除零（maxMinutes=0），每天应渲染为最低块
            var today = DateTime.Now.Date;
            var trend = Enumerable.Range(0, 14)
                .Select(i => new DailyPlaytimeSummary(today.AddDays(-(13 - i)), 0))
                .ToList();
            var preview = new SessionActivityPreview(
                new List<SessionGameSummary>(), trend, 0, 14, "test.csv", 0, 0);

            var uiType = typeof(GameHelper.ConsoleHost.Interactive.MonitorUI);
            var chartMethod = uiType.GetMethod(
                "BuildDailyTrendChart",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var chart = (IRenderable)chartMethod!.Invoke(null, new object[] { preview })!;

            var output = RenderWithTimeout(chart, "all-zero trend");
            Assert.Contains("0 min", output);
            // 全零窗口无趋势信号，两个状态都不显示。
            Assert.DoesNotContain("vs previous", output);
            Assert.DoesNotContain("Game count", output);
        }

        [Fact]
        public void Render_IncreasingTrend_ShowsIncreaseStatus()
        {
            // 前半窗全 10min、后半窗全 60min → 近 7 天较前 7 天 +500%；
            // 半窗游戏数量 2→4 款（模拟 StatisticsService 分桶结果）。
            var today = DateTime.Now.Date;
            var trend = Enumerable.Range(0, 14)
                .Select(i => new DailyPlaytimeSummary(today.AddDays(-(13 - i)), i < 7 ? 10L : 60L))
                .ToList();
            var preview = new SessionActivityPreview(
                new List<SessionGameSummary>(), trend, 0, 14, "test.csv", 2, 4);

            var uiType = typeof(GameHelper.ConsoleHost.Interactive.MonitorUI);
            var chartMethod = uiType.GetMethod(
                "BuildDailyTrendChart",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var chart = (IRenderable)chartMethod!.Invoke(null, new object[] { preview })!;

            var output = RenderWithTimeout(chart, "increasing trend");
            Assert.Contains("↑ Playtime up", output);
            Assert.Contains("+500% vs previous 7 days", output);
            Assert.Contains("↑ Game count up", output);
            Assert.Contains("(2→4 games)", output);
        }

        [Fact]
        public void Render_GameCountDecreasingWhileDurationFlat_ShowsBothStatuses()
        {
            // 每天等量游玩（时长持平，不标注幅度）+ 前 3 → 近 1 款（数量减少），
            // 验证两个状态并列且数量减少的方向断言。
            var today = DateTime.Now.Date;
            var trend = Enumerable.Range(0, 14)
                .Select(i => new DailyPlaytimeSummary(today.AddDays(-(13 - i)), 60L))
                .ToList();
            var preview = new SessionActivityPreview(
                new List<SessionGameSummary>(), trend, 0, 14, "test.csv", 3, 1);

            var uiType = typeof(GameHelper.ConsoleHost.Interactive.MonitorUI);
            var chartMethod = uiType.GetMethod(
                "BuildDailyTrendChart",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var chart = (IRenderable)chartMethod!.Invoke(null, new object[] { preview })!;

            var output = RenderWithTimeout(chart, "flat duration, decreasing games");
            Assert.Contains("→ Playtime steady", output);
            Assert.DoesNotContain("%", output);
            Assert.Contains("↓ Game count down", output);
            Assert.Contains("(3→1 games)", output);
        }
    }
}