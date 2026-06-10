using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameHelper.Core.Utilities;

/// <summary>
/// 提供 Windows 可执行文件路径和名称的归一化。
/// 处理驱动器路径、UNC 路径、相对路径、尾部斜杠、大小写等变体，
/// 使 L1 精确路径匹配能正确工作。
/// </summary>
internal static class PathNormalizer
{
    /// <summary>
    /// 把可执行文件名归一化为标准形式（含 .exe 扩展名）。
    /// </summary>
    public static string? NormalizeName(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var name = Path.GetFileName(executableName.Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".exe";
        }

        return name;
    }

    /// <summary>
    /// 把可执行文件路径归一化为标准形式。
    /// 支持驱动器路径、UNC 路径、相对路径。
    /// </summary>
    public static string? NormalizePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        if (TryResolveWindowsPath(executablePath, out var windowsPath, out _))
        {
            if (windowsPath.Length > 3 &&
                windowsPath[1] == ':' &&
                windowsPath.EndsWith("\\", StringComparison.Ordinal))
            {
                windowsPath = windowsPath.TrimEnd('\\');
            }
            else if (windowsPath.StartsWith("\\\\", StringComparison.Ordinal) &&
                     windowsPath.EndsWith("\\", StringComparison.Ordinal))
            {
                windowsPath = windowsPath.TrimEnd('\\');
            }

            return windowsPath;
        }

        try
        {
            return Path.GetFullPath(executablePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return executablePath.Trim();
        }
    }

    /// <summary>
    /// 尝试解析 Windows 风格的驱动器路径或 UNC 路径。
    /// 返回解析后的完整路径和所在目录。
    /// </summary>
    public static bool TryResolveWindowsPath(string path, out string normalizedPath, out string directory)
    {
        normalizedPath = string.Empty;
        directory = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();

        if (IsWindowsDrivePath(trimmed))
        {
            return TryResolveDrivePath(trimmed, out normalizedPath, out directory);
        }

        if (IsWindowsUncPath(trimmed))
        {
            return TryResolveUncPath(trimmed, out normalizedPath, out directory);
        }

        return false;
    }

    /// <summary>
    /// 确保路径以目录分隔符结尾。
    /// </summary>
    public static string EnsureTrailingSeparator(string path, bool preferBackslash = false)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (path.EndsWith(Path.DirectorySeparatorChar) ||
            path.EndsWith(Path.AltDirectorySeparatorChar) ||
            path.EndsWith("\\", StringComparison.Ordinal))
        {
            return path;
        }

        var separator = preferBackslash || path.Contains("\\", StringComparison.Ordinal)
            ? '\\'
            : Path.DirectorySeparatorChar;

        return path + separator;
    }

    private static bool TryResolveDrivePath(string path, out string normalizedPath, out string directory)
    {
        normalizedPath = string.Empty;
        directory = string.Empty;

        var segments = path.Substring(2)
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

        var resolved = new List<string>();
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (resolved.Count == 0)
                {
                    return false;
                }

                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            resolved.Add(segment);
        }

        var hasTrailingSeparator = path.EndsWith("\\", StringComparison.Ordinal) ||
                                   path.EndsWith("/", StringComparison.Ordinal);

        var drive = char.ToUpperInvariant(path[0]);
        normalizedPath = $"{drive}:\\";

        if (resolved.Count > 0)
        {
            normalizedPath += string.Join("\\", resolved);

            if (hasTrailingSeparator)
            {
                normalizedPath += "\\";
            }
        }

        var directorySegments = new List<string>(resolved);
        if (!hasTrailingSeparator && directorySegments.Count > 0)
        {
            directorySegments.RemoveAt(directorySegments.Count - 1);
        }

        directory = $"{drive}:\\";
        if (directorySegments.Count > 0)
        {
            directory += string.Join("\\", directorySegments) + "\\";
        }

        return true;
    }

    private static bool TryResolveUncPath(string path, out string normalizedPath, out string directory)
    {
        normalizedPath = string.Empty;
        directory = string.Empty;

        var segments = path.Substring(2)
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2)
        {
            return false;
        }

        var resolved = new List<string>
        {
            segments[0],
            segments[1]
        };

        for (var i = 2; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (resolved.Count > 2)
                {
                    resolved.RemoveAt(resolved.Count - 1);
                }

                continue;
            }

            resolved.Add(segment);
        }

        var hasTrailingSeparator = path.EndsWith("\\", StringComparison.Ordinal) ||
                                   path.EndsWith("/", StringComparison.Ordinal);

        normalizedPath = "\\\\" + string.Join("\\", resolved);
        if (hasTrailingSeparator)
        {
            normalizedPath += "\\";
        }

        var directorySegments = resolved;
        if (!hasTrailingSeparator && resolved.Count > 2)
        {
            directorySegments = resolved.Take(resolved.Count - 1).ToList();
        }

        directory = "\\\\" + string.Join("\\", directorySegments);
        if (!directory.EndsWith("\\", StringComparison.Ordinal))
        {
            directory += "\\";
        }

        return true;
    }

    private static bool IsWindowsDrivePath(string path)
    {
        return path.Length >= 3 &&
               char.IsLetter(path[0]) &&
               path[1] == ':' &&
               (path[2] == '\\' || path[2] == '/');
    }

    private static bool IsWindowsUncPath(string path)
    {
        return path.StartsWith("\\\\", StringComparison.Ordinal) ||
               path.StartsWith("//", StringComparison.Ordinal);
    }
}
