using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameHelper.Infrastructure.Notifications;

/// <summary>Owns the embedded, high-contrast G icon independently of installed executable paths.</summary>
internal sealed class SystemTrayIcon : SafeHandleZeroOrMinusOneIsInvalid
{
    internal (int Width, int Height) Size { get; }

    private SystemTrayIcon(IntPtr icon, int width, int height) : base(true)
    {
        Size = (width, height);
        SetHandle(icon);
    }

    internal static SystemTrayIcon Load() => Load(GetTaskbarIconSize());

    // Query Explorer's DPI, not this DPI-unaware host's virtualized system metrics.
    // Explorer may temporarily be absent during restart; subsequent tray refreshes retry.
    internal static (int Width, int Height) GetTaskbarIconSize()
    {
        var dpi = GetDpiForWindow(FindWindow("Shell_TrayWnd", null));
        return GetIconSizeForDpi(dpi == 0 ? 96u : dpi);
    }

    internal static (int Width, int Height) GetIconSizeForDpi(uint dpi) =>
        (GetSystemMetricsForDpi(49, dpi), GetSystemMetricsForDpi(50, dpi));

    internal static SystemTrayIcon Load((int Width, int Height) dimensions)
    {
        var (width, height) = dimensions;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        using var resource = typeof(SystemTrayIcon).Assembly.GetManifestResourceStream(
            "GameHelper.Infrastructure.Notifications.gamehelper-tray.ico")
            ?? throw new InvalidOperationException("Embedded GameHelper tray icon is unavailable.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var bytes = memory.ToArray();
        var entry = SelectImageEntry(bytes, width, height);
        var size = BitConverter.ToUInt32(bytes, entry + 8);
        var offset = BitConverter.ToUInt32(bytes, entry + 12);
        if (offset > bytes.Length || size > bytes.Length - offset)
            throw new InvalidOperationException("Embedded tray icon image is invalid.");
        var image = bytes.AsSpan((int)offset, (int)size).ToArray();
        var icon = CreateIconFromResourceEx(image, size, true, 0x00030000, width, height, 0);
        if (icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new SystemTrayIcon(icon, width, height);
    }

    internal static int SelectImageEntry(byte[] bytes, int width, int height)
    {
        if (bytes.Length < 6 || BitConverter.ToUInt16(bytes, 2) != 1)
            throw new InvalidOperationException("Embedded tray icon directory is invalid.");
        var count = BitConverter.ToUInt16(bytes, 4);
        if (count == 0 || 6 + count * 16 > bytes.Length)
            throw new InvalidOperationException("Embedded tray icon has no matching image.");
        // ICO file entries contain byte offsets, unlike RT_GROUP_ICON resource identifiers.
        // Prefer downsampling an image that covers the target. If all are too small,
        // use the largest available image rather than magnifying a smaller one.
        static int Dimension(byte value) => value == 0 ? 256 : value;
        return Enumerable.Range(0, count).Select(index => 6 + index * 16)
            .OrderBy(index => Dimension(bytes[index]) >= width && Dimension(bytes[index + 1]) >= height ? 0 : 1)
            .ThenBy(index => Dimension(bytes[index]) >= width && Dimension(bytes[index + 1]) >= height
                ? Dimension(bytes[index]) * Dimension(bytes[index + 1])
                : -Dimension(bytes[index]) * Dimension(bytes[index + 1]))
            .First();
    }

    protected override bool ReleaseHandle() => DestroyIcon(handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] image, uint size,
        [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
