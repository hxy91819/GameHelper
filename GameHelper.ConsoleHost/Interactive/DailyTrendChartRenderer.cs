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
        public const int BarRows = 4;

        /// <summary>底行中零值天的占位字符（渲染为灰色）。</summary>
        public const char ZeroMark = '·';

        private const string Blocks = "▁▂▃▄▅▆▇█";

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
    }
}
