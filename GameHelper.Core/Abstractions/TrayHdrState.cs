namespace GameHelper.Core.Abstractions;

/// <summary>Unknown/unsupported states cannot be toggled; a lease supplies a disabled reason.</summary>
public sealed record TrayHdrState(bool? Enabled, bool CanToggle, string? Reason = null);
