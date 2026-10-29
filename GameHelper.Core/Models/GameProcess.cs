namespace GameHelper.Core.Models;

/// <summary>Runtime-observed process identity for foreground/stop actions scoped to one live session.</summary>
public sealed record GameProcess(string DataKey, Guid SessionId, int ProcessId, DateTime? StartedAtUtc);
