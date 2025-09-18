using System.Runtime.InteropServices;

namespace GameHelper.Tests;

internal static class TestEnvironment
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
