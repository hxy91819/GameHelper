using System.Runtime.InteropServices;
using GameHelper.Infrastructure.Notifications;

namespace GameHelper.Tests;

public sealed class SystemTrayIconTests
{
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
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
}
