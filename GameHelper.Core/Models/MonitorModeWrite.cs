using System;
using System.Globalization;

namespace GameHelper.Core.Models;

/// <summary>A single VCP code/value pair of a monitor preset recipe.</summary>
public readonly record struct MonitorModeWrite(byte Code, uint Value)
{
    /// <summary>
    /// Parses a code/value pair. The VCP code is always hexadecimal ("DC" or "0xDC");
    /// the value is hexadecimal when prefixed with "0x" ("0x1D") and decimal otherwise ("29").
    /// </summary>
    public static bool TryParse(string? codeText, string? valueText, out MonitorModeWrite write)
    {
        write = default;
        if (string.IsNullOrWhiteSpace(codeText) || string.IsNullOrWhiteSpace(valueText))
        {
            return false;
        }

        var codeTextTrimmed = codeText.Trim();
        if (codeTextTrimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            codeTextTrimmed = codeTextTrimmed[2..];
        }

        if (!int.TryParse(codeTextTrimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
            || code is < 0 or > 0xFF)
        {
            return false;
        }

        if (!TryParseValue(valueText, out var value) || value is < 0 or > 0xFF)
        {
            return false;
        }

        write = new MonitorModeWrite((byte)code, (uint)value);
        return true;
    }

    private static bool TryParseValue(string text, out int value)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Canonical "0xDC" rendering used for logs.</summary>
    public override string ToString() => $"0x{Code:X2}=0x{Value:X2}";
}