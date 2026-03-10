using System;
using System.Collections.Generic;
using System.Globalization;
using GameHelper.Core.Models;

namespace GameHelper.Core.Utilities;

public static class HotkeyBindingParser
{
    public static bool TryParse(string? text, out HotkeyBinding? binding, out string error)
    {
        binding = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键不能为空。";
            return false;
        }

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
        {
            error = "热键至少需要一个修饰键和一个主键，例如 Ctrl+Alt+F10。";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? keyToken = null;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (TryParseModifier(token, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (keyToken is not null || i != tokens.Length - 1)
            {
                error = $"无法识别热键片段 '{token}'。";
                return false;
            }

            keyToken = token;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            error = "热键必须包含至少一个修饰键。";
            return false;
        }

        if (keyToken is null || !TryParseVirtualKey(keyToken, out var virtualKey, out var normalizedKey))
        {
            error = $"不支持的主键 '{keyToken ?? string.Empty}'。";
            return false;
        }

        binding = new HotkeyBinding(NormalizeDisplayText(modifiers, normalizedKey), modifiers, virtualKey);
        return true;
    }

    private static bool TryParseModifier(string token, out HotkeyModifiers modifier)
    {
        modifier = token.ToUpperInvariant() switch
        {
            "ALT" => HotkeyModifiers.Alt,
            "CTRL" => HotkeyModifiers.Control,
            "CONTROL" => HotkeyModifiers.Control,
            "SHIFT" => HotkeyModifiers.Shift,
            "WIN" => HotkeyModifiers.Win,
            "WINDOWS" => HotkeyModifiers.Win,
            _ => HotkeyModifiers.None
        };

        return modifier != HotkeyModifiers.None;
    }

    private static bool TryParseVirtualKey(string token, out uint virtualKey, out string normalizedKey)
    {
        virtualKey = 0;
        normalizedKey = token.Trim().ToUpperInvariant();

        if (normalizedKey.Length == 1)
        {
            var ch = normalizedKey[0];
            if (ch is >= 'A' and <= 'Z')
            {
                virtualKey = ch;
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }
        }

        if (normalizedKey.StartsWith('F') &&
            int.TryParse(normalizedKey[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            normalizedKey = $"F{functionKey}";
            return true;
        }

        return false;
    }

    private static string NormalizeDisplayText(HotkeyModifiers modifiers, string key)
    {
        var parts = new List<string>();

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(key);
        return string.Join('+', parts);
    }
}
