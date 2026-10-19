namespace GameHelper.Core.Utilities;

/// <summary>Detection result for the mod loader deployed beside a game executable.</summary>
/// <param name="Kind">Detected loader kind, <see cref="ModLoaderKind.None"/> when no known loader is present.</param>
/// <param name="LogPath">Loader log file rewritten on every game launch, used as the boot-completion signal source.</param>
public sealed record ModLoaderInfo(ModLoaderKind Kind, string? LogPath)
{
    public static ModLoaderInfo None { get; } = new(ModLoaderKind.None, null);
}
