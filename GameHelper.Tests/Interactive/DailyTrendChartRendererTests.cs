using System;
using System.Linq;
using GameHelper.ConsoleHost.Interactive;
using Xunit;

namespace GameHelper.Tests.Interactive
{
    public class DailyTrendChartRendererTests
    {
        [Fact]
        public void BuildBarRows_MockDistribution_RendersExactRows()
        {
            // 与方案 mockup 相同的分布（档位×60 分钟），自顶向下逐行精确断言：
            // 峰值日(420)占满全部 8 行，300 档日到第 2 行 ▆，120 档日到第 5 行 ▃，
            // 60 档日底行 █ 上方 ▂，零值天仅底行灰点。
            long[] minutes = { 180, 0, 300, 60, 60, 120, 420, 120, 60, 120, 120, 0, 0, 60 };

            var rows = DailyTrendChartRenderer.BuildBarRows(minutes);

            Assert.Equal(DailyTrendChartRenderer.BarRows, rows.Count);
            Assert.All(rows, row => Assert.Equal(minutes.Length, row.Length));
            Assert.Equal("      █       ", rows[0]);
            Assert.Equal("      █       ", rows[1]);
            Assert.Equal("  ▆   █       ", rows[2]);
            Assert.Equal("  █   █       ", rows[3]);
            Assert.Equal("▄ █   █       ", rows[4]);
            Assert.Equal("█ █  ▃█▃ ▃▃   ", rows[5]);
            Assert.Equal("█ █▂▂███▂██  ▂", rows[6]);
            Assert.Equal("█·█████████··█", rows[7]);
        }

        [Fact]
        public void BuildBarRows_TinyNonZero_RendersLowestBlockNotZeroMark()
        {
            // 极小非零值应显示最低块 ▁ 而不是零值占位点，保证"·只代表没玩"。
            var rows = DailyTrendChartRenderer.BuildBarRows(new long[] { 1, 600 });

            Assert.Equal('▁', rows[^1][0]);
            Assert.Equal('█', rows[^1][1]);
            Assert.Equal('█', rows[0][1]);
        }

        [Fact]
        public void BuildBarRows_AllZero_BaselineDotsAndBlankUpperRows()
        {
            // 全 0 窗口不能除零：上方行全空格，底行全占位点。
            var rows = DailyTrendChartRenderer.BuildBarRows(new long[] { 0, 0, 0, 0, 0 });

            Assert.All(rows.Take(rows.Count - 1), row => Assert.Equal("     ", row));
            Assert.Equal("·····", rows[^1]);
        }

        [Fact]
        public void BuildBarRows_PeakDay_FillsAllBarRows()
        {
            var rows = DailyTrendChartRenderer.BuildBarRows(new long[] { 0, 500 });

            for (var r = 0; r < DailyTrendChartRenderer.BarRows; r++)
            {
                Assert.Equal('█', rows[r][1]);
            }

            Assert.Equal(DailyTrendChartRenderer.ZeroMark, rows[^1][0]);
        }

        [Fact]
        public void GetAxisTicks_60DayWindow_LabelsEvery14DaysPlusEnd()
        {
            // 每 14 天一个刻度；末端右对齐（列 55，日期下标 59）；56 处的刻度因标签越界不生成
            var ticks = DailyTrendChartRenderer.GetAxisTicks(60);

            Assert.Equal(
                new[] { (0, 0), (14, 14), (28, 28), (42, 42), (55, 59) },
                ticks.Select(t => (t.Column, t.DayIndex)).ToArray());
        }

        [Fact]
        public void GetAxisTicks_30DayWindow_ConflictingTickYieldsToEnd()
        {
            // 28 处刻度放不下（28+5 越界），末端标签固定在 25-29 列、表达第 29 天
            var ticks = DailyTrendChartRenderer.GetAxisTicks(30);

            Assert.Equal(
                new[] { (0, 0), (14, 14), (25, 29) },
                ticks.Select(t => (t.Column, t.DayIndex)).ToArray());
        }

        [Fact]
        public void GetAxisTicks_14DayWindow_MatchesLegacyStartEndLayout()
        {
            // 与旧版"起止两端标签 + 中间空格"逐列等价（列 0 表达第 0 天，列 9 表达第 13 天）
            var ticks = DailyTrendChartRenderer.GetAxisTicks(14);

            Assert.Equal(
                new[] { (0, 0), (9, 13) },
                ticks.Select(t => (t.Column, t.DayIndex)).ToArray());
        }

        [Fact]
        public void GetAxisTicks_TinyWindow_SingleLabel()
        {
            var ticks = DailyTrendChartRenderer.GetAxisTicks(7);

            Assert.Equal(
                new[] { (0, 0) },
                ticks.Select(t => (t.Column, t.DayIndex)).ToArray());
        }
    }
}
