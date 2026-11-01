using System.Text;
using GameHelper.Core.Models;
using Microsoft.Win32;

namespace GameHelper.Infrastructure.Controllers;

/// <summary>Reads Windows' EDID copy only; no DDC traffic and no hardware writes.</summary>
internal static class MonitorEdidIdentity
{
    internal static MonitorDeviceIdentity Read(string name, string devicePath)
    {
        byte[]? edid = null;
        try
        {
            var parts = devicePath.Split('#');
            if (parts.Length == 4 && parts[0].Equals(@"\\?\DISPLAY", StringComparison.OrdinalIgnoreCase) &&
                !parts[1].Contains('\\') && !parts[2].Contains('\\'))
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + "\\" + parts[2] + @"\Device Parameters");
                edid = key?.GetValue("EDID") as byte[];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return new() { Name = name, DevicePath = devicePath, HardwareKey = HardwareKey(edid) };
    }

    internal static string? HardwareKey(byte[]? edid)
    {
        if (edid is null || edid.Length < 128 || !edid.AsSpan(0, 8).SequenceEqual(new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }) ||
            edid.Take(128).Sum(value => (int)value) % 256 != 0) return null;
        string? serial = null;
        for (var offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] == 0 && edid[offset + 1] == 0 && edid[offset + 2] == 0 && edid[offset + 3] == 0xFF)
            {
                var value = Encoding.ASCII.GetString(edid, offset + 5, 13).Trim('\0', '\r', '\n', ' ');
                if (value.Length > 0 && value.All(c => c is >= ' ' and <= '~') && value.Any(c => c != '0')) serial = value.ToUpperInvariant();
            }
        }
        var numeric = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(edid.AsSpan(12, 4));
        if (serial is null && numeric is not (0 or uint.MaxValue)) serial = numeric.ToString("X8");
        return serial is null ? null : Convert.ToHexString(edid.AsSpan(8, 4)) + ":" + serial;
    }
}
