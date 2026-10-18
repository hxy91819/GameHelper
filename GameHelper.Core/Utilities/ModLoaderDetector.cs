using System;
using System.IO;

namespace GameHelper.Core.Utilities;

/// <summary>Mod loaders GameHelper knows how to detect and wait for.</summary>
public enum ModLoaderKind
{
    None,
    BepInEx,
    MelonLoader
}

/// <summary>Detection result for the mod loader deployed beside a game executable.</summary>
/// <param name="Kind">Detected loader kind, <see cref="ModLoaderKind.None"/> when no known loader is present.</param>
/// <param name="LogPath">Loader log file rewritten on every game launch, used as the boot-completion signal source.</param>
public sealed record ModLoaderInfo(ModLoaderKind Kind, string? LogPath)
{
    public static ModLoaderInfo None { get; } = new(ModLoaderKind.None, null);
}

/// <summary>
/// Detects mod loaders installed beside a game executable. Speed attach defers until the detected
/// loader reports its chainloader complete, because loader boot phases (BepInEx interop generation in
/// particular) race with Cheat Engine's process injection and crash the game.
/// </summary>
public static class ModLoaderDetector
{
    /// <summary>
    /// Probes the directory of <paramref name="gameExecutablePath"/> for known loader layouts.
    /// Pass any executable path or directory; null or missing directories yield <see cref="ModLoaderKind.None"/>.
    /// </summary>
    public static ModLoaderInfo Detect(string? gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath))
        {
            return ModLoaderInfo.None;
        }

        string directory;
        try
        {
            directory = Directory.Exists(gameExecutablePath)
                ? gameExecutablePath
                : Path.GetDirectoryName(gameExecutablePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return ModLoaderInfo.None;
            }
        }
        catch (Exception)
        {
            return ModLoaderInfo.None;
        }

        var bepInExDirectory = Path.Combine(directory, "BepInEx");
        if (Directory.Exists(bepInExDirectory))
        {
            return new ModLoaderInfo(ModLoaderKind.BepInEx, Path.Combine(bepInExDirectory, "LogOutput.log"));
        }

        var melonLoaderDirectory = Path.Combine(directory, "MelonLoader");
        if (Directory.Exists(melonLoaderDirectory))
        {
            return new ModLoaderInfo(ModLoaderKind.MelonLoader, Path.Combine(melonLoaderDirectory, "Latest.log"));
        }

        return ModLoaderInfo.None;
    }
}