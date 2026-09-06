using System;
using System.Collections.Generic;
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
                trend, 2, 14, "test.csv");
        }

        private static string RenderWithTimeout(IRenderable renderable, string stepName)
        {
            var console = new TestConsole();
            console.Profile.Capabilities.Ansi = false;
            console.Profile.Width = 100;

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

            // 2. 表格单独入 Panel
            var out2 = RenderWithTimeout(new Panel(table) { Header = new PanelHeader("历史记录预览"), Border = BoxBorder.Rounded }, "panel(table)");
            _output.WriteLine("[2 panel]\n" + out2);

            // 3. 最终组合：Panel(table) + 直接输出多行 Markup（MonitorUI 实际路径）
            var console3 = new TestConsole();
            console3.Profile.Capabilities.Ansi = false;
            console3.Profile.Width = 100;
            var renderTask = Task.Run(() =>
            {
                console3.Write(new Panel(table) { Header = new PanelHeader("历史记录预览"), Border = BoxBorder.Rounded });
                console3.Write(chart);
            });
            if (!renderTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Rendering hung at step: final composition");
            }
            _output.WriteLine("[3 final]\n" + console3.Output);
        }
    }
}