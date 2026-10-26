namespace GameHelper.Core.Models;

/// <summary>Read-only DDC recording sample. Target identity prevents mixing different display topologies.</summary>
public sealed record MonitorModeSnapshot(string TargetId, IReadOnlyList<MonitorModeWrite> Values)
{
    public static IReadOnlyList<byte> DefaultCodes { get; } = Array.AsReadOnly(new byte[] { 0xDC, 0xE2, 0xF0, 0x14 });

    /// <summary>Produces only changed codes successfully read in both samples, preserving candidate order.</summary>
    public static IReadOnlyList<MonitorModeWrite> Difference(MonitorModeSnapshot before, MonitorModeSnapshot after)
    {
        if (string.IsNullOrEmpty(before.TargetId) || before.TargetId != after.TargetId)
            throw new ArgumentException("显示器连接已变化，请重新录制。");
        var previous = before.Values.ToDictionary(value => value.Code, value => value.Value);
        return after.Values.Where(value => previous.TryGetValue(value.Code, out var old) && old != value.Value).ToArray();
    }
}
