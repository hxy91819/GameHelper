using System;
using System.Runtime.InteropServices;
using Xunit;

namespace GameHelper.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Requires Windows for WMI support.";
        }
    }
}
