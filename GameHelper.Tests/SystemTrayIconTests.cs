using System.Runtime.InteropServices;
using GameHelper.Infrastructure.Notifications;

namespace GameHelper.Tests;

public sealed class SystemTrayIconTests
{
    [Theory]
    [InlineData(16, 16)]
    [InlineData(20, 20)]
    [InlineData(24, 24)]
    [InlineData(28, 32)]
    [InlineData(32, 32)]
    [InlineData(36, 48)]
    [InlineData(40, 48)]
    [InlineData(48, 48)]
    [InlineData(56, 64)]
    [InlineData(64, 64)]
    [InlineData(80, 64)]
    public void RasterCoversTargetOrUsesLargestAvailable(int target, int expectedSource)
    {
        using var stream = typeof(WindowsSystemTrayService).Assembly.GetManifestResourceStream(
            "GameHelper.Infrastructure.Notifications.gamehelper-tray.ico")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var entry = SystemTrayIcon.SelectImageEntry(bytes, target, target);
        Assert.Equal(expectedSource, bytes[entry]);
        Assert.Equal(expectedSource, bytes[entry + 1]);
    }

    [WindowsOnlyFact]
    public void UnawareHostLoadsTaskbarPhysicalSizeWithoutChangingAwareness()
    {
        var original = SetThreadDpiAwarenessContext(new IntPtr(-1)); // DPI_AWARENESS_CONTEXT_UNAWARE
        Assert.NotEqual(IntPtr.Zero, original);
        try
        {
            var context = GetThreadDpiAwarenessContext();
            var dpi = GetDpiForWindow(FindWindow("Shell_TrayWnd", null));
            var expected = (GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi),
                GetSystemMetricsForDpi(50, dpi == 0 ? 96u : dpi));
            using var icon = SystemTrayIcon.Load();
            Assert.Equal(expected, NativeBitmapSize(icon));
            Assert.Equal(context, GetThreadDpiAwarenessContext());
        }
        finally { SetThreadDpiAwarenessContext(original); }
    }

    [WindowsOnlyFact]
    public void EverySupportedDpiCreatesPhysicalSizedNativeBitmap()
    {
        foreach (var dpi in new uint[] { 96, 120, 144, 168, 192, 216, 240, 288, 336, 384, 480 })
        {
            var expected = (GetSystemMetricsForDpi(49, dpi), GetSystemMetricsForDpi(50, dpi));
            using var icon = SystemTrayIcon.Load(SystemTrayIcon.GetIconSizeForDpi(dpi));
            Assert.Equal(expected, NativeBitmapSize(icon));
        }
    }

    private static (int Width, int Height) NativeBitmapSize(SystemTrayIcon icon)
    {
        Assert.True(GetIconInfo(icon.DangerousGetHandle(), out var info));
        try
        {
            Assert.NotEqual(0, GetObject(info.Color, Marshal.SizeOf<Bitmap>(), out var bitmap));
            return (bitmap.Width, bitmap.Height);
        }
        finally
        {
            if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
            if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
        }
    }

    [Fact]
    public void BrandIconIsEmbeddedWithSmallAndHighDpiImages()
    {
        using var stream = typeof(WindowsSystemTrayService).Assembly.GetManifestResourceStream(
            "GameHelper.Infrastructure.Notifications.gamehelper-tray.ico");
        Assert.NotNull(stream);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        var sizes = new List<int>();
        for (var index = 0; index < count; index++)
        {
            var width = reader.ReadByte();
            Assert.Equal(width, reader.ReadByte());
            sizes.Add(width);
            reader.ReadUInt16();
            Assert.Equal(1, reader.ReadUInt16());
            Assert.Equal(32, reader.ReadUInt16());
            var length = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            Assert.True(offset + length <= stream.Length);
        }
        Assert.Equal(new[] { 16, 20, 24, 32, 48, 64 }, sizes);
    }

    [WindowsOnlyFact]
    public void EmbeddedIconLoadsAsOwnedNativeIconAndReleasesHandle()
    {
        var icon = SystemTrayIcon.Load();
        var handle = icon.DangerousGetHandle();
        try
        {
            Assert.False(icon.IsInvalid);
            Assert.True(GetIconInfo(handle, out var info));
            try { Assert.True(info.IsIcon); }
            finally
            {
                if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
                if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
            }
        }
        finally { icon.Dispose(); }
        Assert.True(icon.IsClosed);
        Assert.False(GetIconInfo(handle, out _));
        icon.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint HotspotX, HotspotY;
        public IntPtr Mask, Color;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, Bits;
        public IntPtr Data;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? name);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int metric, uint dpi);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr bitmap, int size, out Bitmap value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
}
