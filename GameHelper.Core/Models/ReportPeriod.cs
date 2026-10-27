using System.Text.Json.Serialization;

namespace GameHelper.Core.Models;

/// <summary>Local calendar year or quarter; null Quarter selects the whole year.</summary>
public sealed record ReportPeriod(int Year, int? Quarter = null)
{
    [JsonIgnore]
    public DateOnly Start
    {
        get
        {
            if (Year is < 2 or > 9998 || Quarter is < 1 or > 4)
                throw new ArgumentException("报告年份应为 2–9998，季度应为 1–4。");
            return new DateOnly(Year, Quarter is { } quarter ? (quarter - 1) * 3 + 1 : 1, 1);
        }
    }
    [JsonIgnore]
    public DateOnly EndExclusive => Start.AddMonths(Quarter is null ? 12 : 3);
    [JsonIgnore]
    public ReportPeriod Previous => Quarter switch
    {
        null => new(Year - 1), 1 => new(Year - 1, 4), _ => new(Year, Quarter - 1)
    };
    public override string ToString() => Quarter is null ? $"{Year} 年度" : $"{Year} 年第 {Quarter} 季度";
}
