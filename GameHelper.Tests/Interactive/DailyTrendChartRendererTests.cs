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

        // ---------- 半窗对比趋势（CompareHalves） ----------

        private static IReadOnlyList<long> TwoHalves(int previousDays, long previousMinutes, int recentDays, long recentMinutes)
        {
            return Enumerable.Range(0, previousDays).Select(_ => previousMinutes)
                .Concat(Enumerable.Range(0, recentDays).Select(_ => recentMinutes))
                .ToArray();
        }

        [Fact]
        public void CompareHalves_IncreaseAboveThreshold_ReturnsDirectionAndPercent()
        {
            var trend = DailyTrendChartRenderer.CompareHalves(TwoHalves(30, 100, 30, 150));

            Assert.True(trend.HasSignal);
            Assert.Equal(TrendDirection.Increasing, trend.Direction);
            Assert.Equal(100d, trend.PreviousDailyAvg);
            Assert.Equal(150d, trend.RecentDailyAvg);
            Assert.Equal(50d, trend.ChangePercent, 6);
        }

        [Fact]
        public void CompareHalves_DecreaseAboveThreshold_ReturnsDirectionAndNegativePercent()
        {
            var trend = DailyTrendChartRenderer.CompareHalves(TwoHalves(30, 90, 30, 60));

            Assert.True(trend.HasSignal);
            Assert.Equal(TrendDirection.Decreasing, trend.Direction);
            Assert.Equal(-33.3333d, trend.ChangePercent, 4);
        }

        [Fact]
        public void CompareHalves_ChangeWithinThreshold_IsSteady()
        {
            var trend = DailyTrendChartRenderer.CompareHalves(TwoHalves(30, 100, 30, 108));

            Assert.True(trend.HasSignal);
            Assert.Equal(TrendDirection.Steady, trend.Direction);
            Assert.Equal(8d, trend.ChangePercent, 6);
        }

        [Fact]
        public void CompareHalves_ChangeExactlyAtDefaultThreshold_IsSteady()
        {
            // (110-100)/100*100 恰为 10.0；阈值按“大于”判定，恰好落在阈值上不算增加。
            var trend = DailyTrendChartRenderer.CompareHalves(TwoHalves(30, 100, 30, 110));

            Assert.Equal(TrendDirection.Steady, trend.Direction);
        }

        [Fact]
        public void CompareHalves_CustomThreshold_DecidesDirection()
        {
            // (150-100)/100*100 = 50.0，二进制精确可整除，可用于边界断言。
            var steady = DailyTrendChartRenderer.CompareHalves(TwoHalves(30, 100, 30, 150), 50.0);
            var increase = DailyTrendChartRenderer.CompareHalves(TwoHalves(30, 100, 30, 151), 50.0);

            Assert.Equal(TrendDirection.Steady, steady.Direction);
            Assert.Equal(TrendDirection.Increasing, increase.Direction);
        }

        [Fact]
        public void CompareHalves_PreviousHalfEmpty_IsIncreaseWithoutPercent()
        {
            var trend = DailyTrendChartRenderer.CompareHalves(TwoHalves(30, 0, 30, 120));

            Assert.True(trend.HasSignal);
            Assert.Equal(TrendDirection.Increasing, trend.Direction);
            Assert.Equal(0d, trend.PreviousDailyAvg);
            Assert.Equal(120d, trend.RecentDailyAvg);
            Assert.True(double.IsNaN(trend.ChangePercent));
        }

        [Fact]
        public void CompareHalves_RecentHalfEmpty_IsFullDecrease()
        {
            var trend = DailyTrendChartRenderer.CompareHalves(TwoHalves(30, 120, 30, 0));

            Assert.True(trend.HasSignal);
            Assert.Equal(TrendDirection.Decreasing, trend.Direction);
            Assert.Equal(-100d, trend.ChangePercent, 6);
        }

        [Fact]
        public void CompareHalves_BothHalvesEmpty_NoSignal()
        {
            var trend = DailyTrendChartRenderer.CompareHalves(TwoHalves(30, 0, 30, 0));

            Assert.False(trend.HasSignal);
            Assert.Equal(TrendDirection.Steady, trend.Direction);
            Assert.True(double.IsNaN(trend.ChangePercent));
        }

        [Fact]
        public void CompareHalves_WindowShorterThanTwoDays_NoSignal()
        {
            Assert.False(DailyTrendChartRenderer.CompareHalves(Array.Empty<long>()).HasSignal);
            Assert.False(DailyTrendChartRenderer.CompareHalves(new long[] { 5 }).HasSignal);
        }
    }
}
