using GameHelper.Infrastructure.Startup;
using Xunit;

namespace GameHelper.Tests
{
    /// <summary>
    /// 自启动注册值就是指回发布目录的可执行文件；判断"是否已启用"只比较可执行文件部分，
    /// 允许引号与历史遗留的启动参数，避免状态与实际不一致时反复改写注册表。
    /// </summary>
    public class WindowsAutoStartManagerTests
    {
        [Theory]
        [InlineData(@"D:\Apps\GameHelper.ConsoleHost.exe", @"D:\Apps\GameHelper.ConsoleHost.exe", true)]
        [InlineData(@"""D:\Apps\GameHelper.ConsoleHost.exe""", @"D:\Apps\GameHelper.ConsoleHost.exe", true)]
        [InlineData(@"""D:\My Apps\GameHelper.ConsoleHost.exe"" --minimized", @"D:\My Apps\GameHelper.ConsoleHost.exe", true)]
        [InlineData(@"D:\Apps\GameHelper.ConsoleHost.exe", @"D:\Apps\Other.exe", false)]
        [InlineData(@"cmd.exe /c start """" /min ""D:\Apps\GameHelper.ConsoleHost.exe""", @"D:\Apps\GameHelper.ConsoleHost.exe", false)]
        [InlineData("", @"D:\Apps\GameHelper.ConsoleHost.exe", false)]
        public void IsRegistrationFor_MatchesExecutable(string commandLine, string executablePath, bool expected)
        {
            Assert.Equal(expected, WindowsAutoStartManager.IsRegistrationFor(commandLine, executablePath));
        }
    }
}
