using System.Globalization;
using System.Text.RegularExpressions;

namespace GameHelper.Infrastructure.Artwork;

/// <summary>Finds an explicit Steam identity without searching or guessing from a game's name.</summary>
public static class SteamArtworkIdentity
{
    public static uint? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric > 0) return numeric;
        var match = Regex.Match(value, @"^(?:steam://rungameid/|https://store\.steampowered\.com/app/)([0-9]+)(?:/[^\s]*)?$", RegexOptions.IgnoreCase);
        return match.Success && uint.TryParse(match.Groups[1].Value, out numeric) && numeric > 0 ? numeric : null;
    }

    public static uint? Resolve(string executable, string target)
    {
        if (Parse(target) is { } direct) return direct;
        try
        {
            if (Path.IsPathFullyQualified(target) && target.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                && File.Exists(target) && new FileInfo(target).Length < 16384)
                foreach (var line in File.ReadLines(target))
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase) && Parse(line[4..]) is { } shortcut) return shortcut;
            var path = Path.IsPathFullyQualified(executable) ? executable : target;
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var parent = new DirectoryInfo(Path.GetDirectoryName(path)!);
            uint? besideId = null;
            for (var level = 0; parent is not null && level < 6; level++, parent = parent.Parent)
            {
                if (parent.Parent?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) == true
                    && parent.Parent.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true)
                {
                    foreach (var manifest in parent.Parent.Parent.EnumerateFiles("appmanifest_*.acf"))
                    {
                        if (manifest.Length > 1024 * 1024) continue;
                        var text = File.ReadAllText(manifest.FullName);
                        var directory = Regex.Match(text, "\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                        if (directory.Success && directory.Groups[1].Value.Equals(parent.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            var id = Regex.Match(text, "\"appid\"\\s*\"([0-9]+)\"", RegexOptions.IgnoreCase);
                            return id.Success ? Parse(id.Groups[1].Value) : null;
                        }
                    }
                    break;
                }
                var idFile = Path.Combine(parent.FullName, "steam_appid.txt");
                if (besideId is null && File.Exists(idFile) && new FileInfo(idFile).Length < 128
                    && Parse(File.ReadAllText(idFile)) is { } beside) besideId = beside;
            }
            return besideId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }
}
