namespace GameHelper.Core.Abstractions;

/// <summary>Read-only active display state for presentation; HDR state is independent of selection.</summary>
public sealed record HdrDisplayInfo(string Name, string DevicePath, bool Supported, bool Enabled,
    bool ForceDisabled, bool Allowed);
