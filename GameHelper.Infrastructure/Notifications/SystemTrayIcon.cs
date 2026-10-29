using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameHelper.Infrastructure.Notifications;

/// <summary>Owns the embedded, high-contrast G icon independently of installed executable paths.</summary>
internal sealed class SystemTrayIcon : SafeHandleZeroOrMinusOneIsInvalid
{
    private SystemTrayIcon(IntPtr icon) : base(true) => SetHandle(icon);

    internal static SystemTrayIcon Load()
    {
        using var resource = typeof(SystemTrayIcon).Assembly.GetManifestResourceStream(
            "GameHelper.Infrastructure.Notifications.gamehelper-tray.ico")
            ?? throw new InvalidOperationException("Embedded GameHelper tray icon is unavailable.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var bytes = memory.ToArray();
        var width = GetSystemMetrics(49); // SM_CXSMICON
        var height = GetSystemMetrics(50); // SM_CYSMICON
        if (bytes.Length < 6 || BitConverter.ToUInt16(bytes, 2) != 1)
            throw new InvalidOperationException("Embedded tray icon directory is invalid.");
        var count = BitConverter.ToUInt16(bytes, 4);
        if (count == 0 || 6 + count * 16 > bytes.Length)
            throw new InvalidOperationException("Embedded tray icon has no matching image.");
        // ICO file entries contain byte offsets, unlike RT_GROUP_ICON resource identifiers.
        var entry = Enumerable.Range(0, count).Select(index => 6 + index * 16)
            .OrderBy(index => Math.Abs((bytes[index] == 0 ? 256 : bytes[index]) - width)
                + Math.Abs((bytes[index + 1] == 0 ? 256 : bytes[index + 1]) - height))
            .First();
        var size = BitConverter.ToUInt32(bytes, entry + 8);
        var offset = BitConverter.ToUInt32(bytes, entry + 12);
        if (offset > bytes.Length || size > bytes.Length - offset)
            throw new InvalidOperationException("Embedded tray icon image is invalid.");
        var image = bytes.AsSpan((int)offset, (int)size).ToArray();
        var icon = CreateIconFromResourceEx(image, size, true, 0x00030000, width, height, 0);
        if (icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new SystemTrayIcon(icon);
    }

    protected override bool ReleaseHandle() => DestroyIcon(handle);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] image, uint size,
        [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
