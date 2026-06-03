using System;
using GameHelper.ConsoleHost.Utilities;
using Xunit;

namespace GameHelper.Tests
{
    public class ConsoleWindowHelperTests
    {
        [WindowsOnlyFact]
        public void GetHandle_ShouldReturnConsistentValue()
        {
            var first = ConsoleWindowHelper.GetHandle();
            var second = ConsoleWindowHelper.GetHandle();

            Assert.Equal(first, second);
        }

        [WindowsOnlyFact]
        public void HideAndShow_ShouldNotThrowWhenConsoleWindowExists()
        {
            var hwnd = ConsoleWindowHelper.GetHandle();
            if (hwnd == IntPtr.Zero) return;

            ConsoleWindowHelper.Hide();
            ConsoleWindowHelper.Show();
        }

        [WindowsOnlyFact]
        public void Minimize_ShouldNotThrowWhenConsoleWindowExists()
        {
            var hwnd = ConsoleWindowHelper.GetHandle();
            if (hwnd == IntPtr.Zero) return;

            ConsoleWindowHelper.Minimize();
            ConsoleWindowHelper.Show();
        }

        [WindowsOnlyFact]
        public void InstallAndRemoveCloseHandler_ShouldSucceed()
        {
            var result = ConsoleWindowHelper.InstallCloseHandler(() => true);

            Assert.True(result);

            ConsoleWindowHelper.RemoveCloseHandler();
        }

        [WindowsOnlyFact]
        public void RemoveCloseHandler_ShouldNotThrowWhenNoHandlerInstalled()
        {
            ConsoleWindowHelper.RemoveCloseHandler();
        }

        [WindowsOnlyFact]
        public void InstallCloseHandler_ReplacesPreviousHandler()
        {
            var result1 = ConsoleWindowHelper.InstallCloseHandler(() => true);
            Assert.True(result1);

            var result2 = ConsoleWindowHelper.InstallCloseHandler(() => true);
            Assert.True(result2);

            ConsoleWindowHelper.RemoveCloseHandler();
        }
    }
}