using System.Reflection;
using System.Runtime.InteropServices;
using GameHelper.Infrastructure.Hotkeys;
using Xunit;

namespace GameHelper.Tests.Hotkeys;

public sealed class NativeWindowsHotkeyPlatformPInvokeTests
{
    [Theory]
    [InlineData("RegisterHotKeyNative", "RegisterHotKey")]
    [InlineData("UnregisterHotKeyNative", "UnregisterHotKey")]
    [InlineData("GetMessageNative", "GetMessageW")]
    [InlineData("PeekMessage", "PeekMessageW")]
    [InlineData("PostThreadMessageNative", "PostThreadMessageW")]
    [InlineData("GetCurrentThreadIdNative", "GetCurrentThreadId")]
    public void DllImportEntryPoint_ShouldMatchWin32Symbol(string methodName, string expectedEntryPoint)
    {
        var method = typeof(NativeWindowsHotkeyPlatform).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var dllImport = method!.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(dllImport);
        Assert.Equal(expectedEntryPoint, dllImport!.EntryPoint);
    }
}
