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
            // 峰值日(420)占满 4 行，5 档日到 ▇，3 档日到 ▆，2 档日底行 █ 上方 ▂，
            // 1 档日底行 ▅，零值天仅底行灰点。
            long[] minutes = { 180, 0, 300, 60, 60, 120, 420, 120, 60, 120, 120, 0, 0, 60 };

            var rows = DailyTrendChartRenderer.BuildBarRows(minutes);

            Assert.Equal(DailyTrendChartRenderer.BarRows, rows.Count);
            Assert.All(rows, row => Assert.Equal(minutes.Length, row.Length));
            Assert.Equal("      █       ", rows[0]);
            Assert.Equal("  ▇   █       ", rows[1]);
            Assert.Equal("▆ █  ▂█▂ ▂▂   ", rows[2]);
            Assert.Equal("█·█▅▅███▅██··▅", rows[3]);
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
    }
}
