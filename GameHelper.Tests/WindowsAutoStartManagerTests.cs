using GameHelper.Infrastructure.Startup;
using Xunit;

namespace GameHelper.Tests
{
    /// <summary>
    /// 自启动注册值现在带 --minimized：启动时收进任务栏而不是抢焦点。
    /// 这里覆盖注册值的解析，保证设置界面判断"是否已启用"时不会被启动参数干扰。
    /// </summary>
    public class WindowsAutoStartManagerTests
    {
        [Theory]
        [InlineData("\"D:\\Apps\\GameHelper.ConsoleHost.exe\" --minimized", "D:\\Apps\\GameHelper.ConsoleHost.exe")]
        [InlineData("D:\\Apps\\GameHelper.ConsoleHost.exe --minimized", "D:\\Apps\\GameHelper.ConsoleHost.exe")]
        [InlineData("\"D:\\Apps\\GameHelper.ConsoleHost.exe\"", "D:\\Apps\\GameHelper.ConsoleHost.exe")]
        [InlineData("  \"D:\\My Apps\\GameHelper.ConsoleHost.exe\" --minimized  ", "D:\\My Apps\\GameHelper.ConsoleHost.exe")]
        public void ExtractExecutablePath_ReturnsExecutableWithoutArguments(string commandLine, string expected)
        {
            Assert.Equal(expected, WindowsAutoStartManager.ExtractExecutablePath(commandLine));
        }

        [Theory]
        [InlineData("\"C:\\app.exe\" --minimized", true)]
        [InlineData("\"C:\\app.exe\"", false)]
        public void HasBackgroundArgument_DetectsMinimizedFlag(string commandLine, bool expected)
        {
            Assert.Equal(expected, WindowsAutoStartManager.HasBackgroundArgument(commandLine));
        }
    }
}
