using System;
using System.Collections.Generic;

namespace GameHelper.Core.Utilities;

/// <summary>
/// 提供时间跨度的本地化格式化。
/// </summary>
internal static class TimeFormatting
{
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration == TimeSpan.Zero)
        {
            return "0秒";
        }

        var parts = new List<string>();
        if (duration.Days > 0)
        {
            parts.Add($"{duration.Days}天");
        }

        if (duration.Hours > 0)
        {
            parts.Add($"{duration.Hours}小时");
        }

        if (duration.Minutes > 0)
        {
            parts.Add($"{duration.Minutes}分钟");
        }

        var seconds = duration.Seconds;
        if (seconds > 0 || parts.Count == 0)
        {
            parts.Add($"{seconds}秒");
        }

        return string.Concat(parts);
    }
}
