using GameHelper.Infrastructure.Startup;
using Xunit;

namespace GameHelper.Tests
{
    /// <summary>
    /// 自启动注册值改成"经 cmd 的 start /min 拉起"后，设置界面判断"是否已启用"必须认这个形态，
    /// 否则每次启动都会重复改注册表，用户也会看到状态与实际不一致。
    /// </summary>
    public class WindowsAutoStartManagerTests
    {
        [Theory]
        [InlineData(@"cmd.exe /c start """" /min ""D:\Apps\GameHelper.ConsoleHost.exe"" --minimized", @"D:\Apps\GameHelper.ConsoleHost.exe", true)]
        [InlineData(@"cmd.exe /c start """" /min ""D:\My Apps\GameHelper.ConsoleHost.exe"" --minimized", @"D:\My Apps\GameHelper.ConsoleHost.exe", true)]
        [InlineData(@"""D:\Apps\GameHelper.ConsoleHost.exe"" --minimized", @"D:\Apps\GameHelper.ConsoleHost.exe", false)]
        [InlineData(@"cmd.exe /c start """" /min ""D:\Other\GameHelper.ConsoleHost.exe"" --minimized", @"D:\Apps\GameHelper.ConsoleHost.exe", false)]
        [InlineData("", @"D:\Apps\GameHelper.ConsoleHost.exe", false)]
        public void IsRegistrationFor_RecognizesBackgroundLaunch(string commandLine, string executablePath, bool expected)
        {
            Assert.Equal(expected, WindowsAutoStartManager.IsRegistrationFor(commandLine, executablePath));
        }

        [Theory]
        [InlineData(@"cmd.exe /c start """" /min ""C:\app.exe"" --minimized", true)]
        [InlineData(@"""C:\app.exe""", false)]
        public void HasBackgroundArgument_DetectsMinimizedFlag(string commandLine, bool expected)
        {
            Assert.Equal(expected, WindowsAutoStartManager.HasBackgroundArgument(commandLine));
        }
    }
}
