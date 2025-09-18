using System.Globalization;
using System.Text;

namespace GameHelper.ConsoleHost.Utilities
{
    public static class DisplayWidth
    {
        public static int Measure(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;

            int width = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                width += RuneWidth(rune);
            }

            return width;
        }

        public static string PadRight(string value, int totalWidth)
        {
            int padding = totalWidth - Measure(value);
            if (padding <= 0) return value;
            return value + new string(' ', padding);
        }

        private static int RuneWidth(Rune rune)
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control ||
                category == UnicodeCategory.NonSpacingMark ||
                category == UnicodeCategory.EnclosingMark ||
                category == UnicodeCategory.SpacingCombiningMark ||
                category == UnicodeCategory.Format)
            {
                return 0;
            }

            return IsWide(rune) ? 2 : 1;
        }

        private static bool IsWide(Rune rune)
        {
            int value = rune.Value;

            if (value >= 0x1100 &&
                (value <= 0x115F ||
                 value == 0x2329 || value == 0x232A ||
                 (value >= 0x2600 && value <= 0x27FF) ||
                 (value >= 0x2E80 && value <= 0xA4CF && value != 0x303F) ||
                 (value >= 0xAC00 && value <= 0xD7A3) ||
                 (value >= 0xF900 && value <= 0xFAFF) ||
                 (value >= 0xFE10 && value <= 0xFE19) ||
                 (value >= 0xFE30 && value <= 0xFE6F) ||
                 (value >= 0xFF00 && value <= 0xFF60) ||
                 (value >= 0xFFE0 && value <= 0xFFE6) ||
                 (value >= 0x1F300 && value <= 0x1F64F) ||
                 (value >= 0x1F680 && value <= 0x1F6FF) ||
                 (value >= 0x1F900 && value <= 0x1F9FF) ||
                 (value >= 0x20000 && value <= 0x3FFFD)))
            {
                return true;
            }

            return false;
        }
    }
}
