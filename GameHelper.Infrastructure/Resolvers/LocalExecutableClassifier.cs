using System.Diagnostics;

namespace GameHelper.Infrastructure.Resolvers;

/// <summary>Explains filesystem evidence without executing a candidate. Size never establishes game identity.</summary>
internal static class LocalExecutableClassifier
{
    private static readonly string[] AuxiliaryTokens = ["unins", "uninstall", "crashreport", "crashhandler", "crashpadhandler", "vcredist", "dxsetup", "dxwebsetup", "cefsubprocess", "steamwebhelper", "prereqsetup", "easyanticheat", "battleye", "rapidcrc", "quicksfv", "godotworkshop"];
    private static readonly HashSet<string> AuxiliaryNames = new(StringComparer.OrdinalIgnoreCase)
    { "setup", "installer", "reporter", "redist", "updater", "anticheat", "dotnet", "python", "pythonw", "python3", "md5", "checksum", "7z", "unrar", "zsync", "zsyncmake" };
    private static readonly HashSet<string> AuxiliaryDirectories = new(StringComparer.OrdinalIgnoreCase)
    { "Engine", "_CommonRedist", "Redist", "_Redist", "__Installer", "DirectX", "DotNet", "Support", "EasyAntiCheat", "BattlEye", "BepInEx", "MD5" };

    internal sealed record Evidence(string Name, int Strength, string Reason, bool Auxiliary);

    internal static Evidence Inspect(string path, string gameDirectory, string folder)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var directory = Path.GetDirectoryName(path)!;
        var product = ReadProductName(path);
        var name = product ?? folder;
        var relativeParts = Path.GetRelativePath(gameDirectory, path).Split(Path.DirectorySeparatorChar);
        if (IsAuxiliaryName(stem) || (product is not null && IsAuxiliaryName(product))
            || AuxiliaryDirectories.Contains(Path.GetFileName(gameDirectory))
            || relativeParts.SkipLast(1).Any(AuxiliaryDirectories.Contains))
            return new(name, 0, "疑似安装、校验、模组或运行库辅助程序", true);
        if (Directory.Exists(Path.Combine(directory, stem + "_Data"))
            && (File.Exists(Path.Combine(directory, "UnityPlayer.dll")) || File.Exists(Path.Combine(directory, "GameAssembly.dll"))))
            return new(name, 100, "同目录存在同名 Unity _Data 与引擎文件", false);
        if (File.Exists(Path.Combine(directory, stem + ".pck")))
            return new(name, 100, "同目录存在同名 Godot .pck 游戏资源", false);
        if (stem.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase)
            && relativeParts.SkipLast(1).Any(part => part.Equals("Binaries", StringComparison.OrdinalIgnoreCase)))
            return new(name, 90, "Binaries 目录下的 Unreal Shipping 程序", false);
        if (stem.Contains("launcher", StringComparison.OrdinalIgnoreCase))
            return new(name, 10, "疑似启动器，请确认实际游戏程序", false);
        if (Directory.Exists(Path.Combine(directory, "renpy")) && Directory.Exists(Path.Combine(directory, "game")))
            return new(name, 85, "同目录存在 Ren’Py 引擎与 game 游戏资源", false);
        if (MatchesFolder(stem, folder)) return new(name, 70, "程序名称与游戏目录名称相符", false);
        if (product is not null && MatchesFolder(product, folder)) return new(name, 60, "产品名称与游戏目录名称相符", false);
        return new(name, 0, "缺少明确游戏特征，请手动确认程序路径", false);
    }

    internal static bool IsSingleGameRoot(string root)
    {
        try
        {
            var executables = Directory.EnumerateFiles(root, "*.exe").Where(path => !IsAuxiliaryName(Path.GetFileNameWithoutExtension(path))).Take(1000).ToList();
            if (executables.Count == 0) return false;
            return Directory.Exists(Path.Combine(root, "Binaries")) || Directory.Exists(Path.Combine(root, "renpy"))
                || executables.Any(path => File.Exists(Path.ChangeExtension(path, ".pck"))
                    || Directory.Exists(Path.Combine(root, Path.GetFileNameWithoutExtension(path) + "_Data"))
                    || MatchesFolder(Path.GetFileNameWithoutExtension(path), Path.GetFileName(Path.TrimEndingDirectorySeparator(root))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsAuxiliaryName(string value)
    {
        var normalized = Normalize(value);
        return AuxiliaryNames.Contains(normalized) || AuxiliaryTokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static bool MatchesFolder(string value, string folder)
    {
        var normalized = Normalize(value); var group = Normalize(folder);
        if (normalized == group) return normalized.Length > 0;
        return value.Length >= 4 && folder.StartsWith(value, StringComparison.OrdinalIgnoreCase)
            && folder.Length > value.Length && " ._-".Contains(folder[value.Length]);
    }
    private static string? ReadProductName(string path)
    {
        try
        {
            var name = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim();
            if (!string.IsNullOrWhiteSpace(name) && name.Length <= 160 && !name.Contains("Unreal", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Unity", StringComparison.OrdinalIgnoreCase) && !name.Contains("Operating System", StringComparison.OrdinalIgnoreCase)) return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
}
