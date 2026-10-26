using System.Globalization;

namespace GameHelper.Core.Models;

/// <summary>A CE shortcut to a preset speed, or (when Speed is null) a primary-speed/original-speed toggle.</summary>
public sealed record SpeedHotkey
{
    public string Hotkey { get; init; } = string.Empty;
    public double? Speed { get; init; }

    /// <summary>Translate the supported shortcut syntax to CE virtual key codes.</summary>
    public int[] GetVirtualKeys()
    {
        if (string.IsNullOrWhiteSpace(Hotkey)) throw new ArgumentException("倍速热键不能为空。");
        var parts = Hotkey.Split('+', StringSplitOptions.TrimEntries);
        var keys = parts.Select(ParseKey).ToArray();
        if (keys.Length is < 1 or > 4 || keys.Distinct().Count() != keys.Length
            || keys.Count(key => key is not (16 or 17 or 18)) != 1)
            throw new ArgumentException("热键须为可选 Ctrl/Alt/Shift 加一个 F1–F12、A–Z 或 0–9 键，例如 Ctrl+F1。");
        return keys;
    }

    private static int ParseKey(string text)
    {
        var key = text.ToUpperInvariant();
        if (key == "CTRL") return 17;
        if (key == "ALT") return 18;
        if (key == "SHIFT") return 16;
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) return key[0];
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 12) return 111 + f;
        throw new ArgumentException($"不支持的热键：{text}。请使用 Ctrl/Alt/Shift、F1–F12、A–Z 或 0–9。");
    }

    /// <summary>Reject invalid speeds and duplicate key combinations before persisting or generating Lua.</summary>
    public static void Validate(IEnumerable<SpeedHotkey>? hotkeys)
    {
        var combinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in hotkeys ?? [])
        {
            if (binding is null) throw new ArgumentException("倍速热键不能为空。");
            if (binding.Speed is { } speed && (!double.IsFinite(speed) || speed <= 0))
                throw new ArgumentException("热键倍速必须是大于 0 的有限数字。");
            var identity = string.Join(',', binding.GetVirtualKeys().Order());
            if (!combinations.Add(identity)) throw new ArgumentException($"重复的倍速热键：{binding.Hotkey}。");
        }
    }

    /// <summary>Parse the one-binding-per-line editor text (<c>Ctrl+F1=2</c>, <c>Ctrl+F3=toggle</c>); blank text means no bindings.</summary>
    public static List<SpeedHotkey>? ParseLines(string? text)
    {
        var bindings = new List<SpeedHotkey>();
        foreach (var line in (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', StringSplitOptions.TrimEntries);
            if (parts.Length != 2) throw new ArgumentException("倍速热键每行填写 热键=倍率 或 热键=toggle，例如 Ctrl+F1=2。");
            double? speed = null;
            if (!parts[1].Equals("toggle", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    throw new ArgumentException("热键倍率请使用数字和小数点，例如 1.5；切换原速填写 toggle。");
                speed = parsed;
            }
            bindings.Add(new() { Hotkey = parts[0], Speed = speed });
        }
        Validate(bindings);
        return bindings.Count == 0 ? null : bindings;
    }

    /// <summary>Format bindings as editor text accepted by <see cref="ParseLines"/>.</summary>
    public static string FormatLines(IEnumerable<SpeedHotkey>? hotkeys) =>
        string.Join(Environment.NewLine, (hotkeys ?? []).Select(binding =>
            $"{binding.Hotkey}={binding.Speed?.ToString("R", CultureInfo.InvariantCulture) ?? "toggle"}"));
}
