using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHelper.ConsoleHost.Interactive
{
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

        private const string Blocks = "▁▂▃▄▅▆▇█";

        private const int AxisLabelWidth = 5; // "MM-dd" 固定 5 字符宽

        /// <summary>日期轴上的一个标签：起始字符列与它表达的日期下标。</summary>
        public readonly record struct AxisTick(int Column, int DayIndex);

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
