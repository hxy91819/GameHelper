using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHelper.ConsoleHost.Interactive
{
    /// <summary>半窗对比得出的游玩趋势方向。</summary>
    public enum TrendDirection
    {
        /// <summary>后半窗日均高于前半窗超过 <see cref="DailyTrendChartRenderer.SteadyThresholdPercent"/>。</summary>
        Increasing,

        /// <summary>后半窗日均相对前半窗的变化不超过阈值。</summary>
        Steady,

        /// <summary>后半窗日均低于前半窗超过阈值。</summary>
        Decreasing
    }

    /// <summary>
    /// 窗口对半分后"近段"（后半窗）与"早段"（前半窗）的日均游玩对比结果。
    /// </summary>
    /// <param name="Direction">近段相对早段的方向。</param>
    /// <param name="PreviousDailyAvg">前半窗日均分钟数。</param>
    /// <param name="RecentDailyAvg">后半窗日均分钟数。</param>
    /// <param name="ChangePercent">后半窗相对前半窗的变化百分比（带符号）；前半窗无游玩时无定义，为 NaN。</param>
    /// <param name="HasSignal">整个窗口至少存在一点游玩时长；否则状态不应展示。</param>
    public readonly record struct HalfWindowTrend(
        TrendDirection Direction,
        double PreviousDailyAvg,
        double RecentDailyAvg,
        double ChangePercent,
        bool HasSignal);

    /// <summary>
    /// 近 N 天每日游玩时长的纵向柱状迷你图核心算法：每天一列，自底向上按当日
    /// 分钟数占窗口峰值的比例填充 <see cref="BarRows"/> 行块字符；底行的零值天
    /// 以 <see cref="ZeroMark"/> 占位。纯文本计算，不依赖控制台，便于单元测试。
    /// </summary>
    internal static class DailyTrendChartRenderer
    {
        /// <summary>柱体总行数（含底行）。</summary>
        public const int BarRows = 8;

        /// <summary>底行中零值天的占位字符（渲染为灰色）。</summary>
        public const char ZeroMark = '·';

        /// <summary>日期轴刻度间隔（天）。窗口拉长后仅标注起止两端无法定位中间日期。</summary>
        public const int AxisTickDays = 14;

        /// <summary>稳态阈值（%）：后半窗日均相对前半窗的绝对变化不超过它时判为基本持平。</summary>
        public const double SteadyThresholdPercent = 10.0;

        private const string Blocks = "▁▂▃▄▅▆▇█";

        private const int AxisLabelWidth = 5; // "MM-dd" 固定 5 字符宽

        /// <summary>日期轴上的一个标签：起始字符列与它表达的日期下标。</summary>
        public readonly record struct AxisTick(int Column, int DayIndex);

        /// <summary>
        /// 把窗口对半分为"近段"（后半窗）与前半窗，比较两者日均分钟数得出
        /// 趋势方向与变化百分比。窗口不足 2 天或两段日均均为 0 时无信号；
        /// 前半窗无游玩而后半窗有时判为增加（变化百分比无定义）。
        /// </summary>
        public static HalfWindowTrend CompareHalves(
            IReadOnlyList<long> minutesPerDay,
            double steadyThresholdPercent = SteadyThresholdPercent)
        {
            var previousDays = minutesPerDay.Count / 2;
            var recentDays = minutesPerDay.Count - previousDays;
            if (previousDays == 0 || recentDays == 0)
            {
                return new HalfWindowTrend(TrendDirection.Steady, 0d, 0d, double.NaN, false);
            }

            var previousSum = minutesPerDay.Take(previousDays).Sum();
            var recentSum = minutesPerDay.Skip(previousDays).Sum();
            if (previousSum == 0L && recentSum == 0L)
            {
                return new HalfWindowTrend(TrendDirection.Steady, 0d, 0d, double.NaN, false);
            }

            var previousAvg = (double)previousSum / previousDays;
            var recentAvg = (double)recentSum / recentDays;
            var changePercent = previousSum > 0L
                ? (recentAvg - previousAvg) / previousAvg * 100d
                : double.NaN;
            TrendDirection direction;
            if (double.IsNaN(changePercent))
            {
                direction = TrendDirection.Increasing;
            }
            else if (changePercent > steadyThresholdPercent)
            {
                direction = TrendDirection.Increasing;
            }
            else if (changePercent < -steadyThresholdPercent)
            {
                direction = TrendDirection.Decreasing;
            }
            else
            {
                direction = TrendDirection.Steady;
            }

            return new HalfWindowTrend(direction, previousAvg, recentAvg, changePercent, true);
        }

        /// <summary>
        /// 生成自顶向下的 <see cref="BarRows"/> 行柱体文本，每行长度等于天数。
        /// 第 r 行（自顶向下）覆盖柱高区间 [BarRows-1-r, BarRows-r)，按区间内
        /// 填充比例选取恰好盖住该比例的最低块字符（ratio→Blocks[ceil(ratio*8)-1]），
        /// 保证峰值日占满全部行、零值天仅在底行留占位点。
        /// </summary>
        public static IReadOnlyList<string> BuildBarRows(IReadOnlyList<long> minutesPerDay)
        {
            var max = minutesPerDay.Count > 0 ? minutesPerDay.Max() : 0;
            var rows = new string[BarRows];
            for (var r = 0; r < BarRows; r++)
            {
                var sliceBase = BarRows - 1 - r;
                var chars = new char[minutesPerDay.Count];
                for (var c = 0; c < minutesPerDay.Count; c++)
                {
                    var fill = max > 0 ? minutesPerDay[c] / (double)max * BarRows : 0d;
                    var ratio = Math.Clamp(fill - sliceBase, 0d, 1d);
                    char glyph;
                    if (ratio <= 0d)
                    {
                        glyph = r == BarRows - 1 ? ZeroMark : ' ';
                    }
                    else
                    {
                        glyph = Blocks[Math.Min(Blocks.Length - 1, (int)Math.Ceiling(ratio * Blocks.Length) - 1)];
                    }

                    chars[c] = glyph;
                }

                rows[r] = new string(chars);
            }

            return rows;
        }

        /// <summary>
        /// 计算日期轴标签的位置：每 <paramref name="tickEvery"/> 天一个刻度；
        /// 末端日期固定右对齐标注，与其重叠的刻度让位；窗口容纳不下两个标签时
        /// 只保留首个刻度。<see cref="AxisTick.Column"/> 是标签起始字符列，
        /// <see cref="AxisTick.DayIndex"/> 是该标签表达的日期下标（末端标签与
        /// 右缘相差 4 列，两者不相等）。
        /// </summary>
        public static IReadOnlyList<AxisTick> GetAxisTicks(int dayCount, int tickEvery = AxisTickDays)
        {
            var ticks = new List<AxisTick>();
            for (var column = 0; column + AxisLabelWidth <= dayCount; column += tickEvery)
            {
                ticks.Add(new AxisTick(column, column));
            }

            if (dayCount < AxisLabelWidth * 2)
            {
                return ticks;
            }

            var endLabelStart = dayCount - AxisLabelWidth;
            ticks.RemoveAll(tick => tick.Column + AxisLabelWidth > endLabelStart);
            ticks.Add(new AxisTick(endLabelStart, dayCount - 1));
            return ticks;
        }
    }
}
