using System.Reflection;
using System.Runtime.InteropServices;
using GameHelper.Infrastructure.Controllers;

namespace GameHelper.Tests;

public sealed class WindowsHdrInputLayoutTests
{
    [Fact]
    public void NativeInputLayoutIncludesTheLargestUnionMember()
    {
        var input = typeof(WindowsHdrController).GetNestedType("INPUT", BindingFlags.NonPublic)!;
        // winuser.h INPUT contains a union of MOUSEINPUT, KEYBDINPUT and HARDWAREINPUT.
        // Keyboard-only union sizing makes SendInput reject cbSize (32 vs 40 on x64).
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf(input));
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf(input, "U").ToInt32());
    }
}
