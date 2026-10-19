using System;
using GameHelper.ConsoleHost.Utilities;
using Xunit;

namespace GameHelper.Tests
{
    /// <summary>
    /// 交互窗口宿主守卫的策略测试：真实终端无法在测试环境里创建，因此覆盖"是否转移宿主"的
    /// 判定分支和重新拉起时构造的命令行，保证任何一个前置条件都会让程序留在当前宿主。
    /// </summary>
    public class WindowsTerminalHostTests
    {
        [Fact]
        public void Evaluate_TransfersHost_WhenEveryPreconditionHolds()
        {
            var decision = WindowsTerminalHost.Evaluate(new TerminalRelaunchFacts());

            Assert.True(decision.Relocate);
        }

        [Theory]
        [InlineData("IsWindows")]
        [InlineData("IsFileDropRequest")]
        [InlineData("IsSandboxed")]
        [InlineData("DisabledByEnvironment")]
        [InlineData("AlreadyRelaunched")]
        [InlineData("IsDebuggerAttached")]
        [InlineData("HasInteractiveConsole")]
        [InlineData("IsHostedByWindowsTerminal")]
        [InlineData("WindowsTerminalAvailable")]
        [InlineData("AnotherInstanceRunning")]
        public void Evaluate_KeepsHost_WhenAnyPreconditionFails(string blocker)
        {
            var facts = new TerminalRelaunchFacts();
            switch (blocker)
            {
                case "IsWindows": facts = facts with { IsWindows = false }; break;
                case "IsFileDropRequest": facts = facts with { IsFileDropRequest = true }; break;
                case "IsSandboxed": facts = facts with { IsSandboxed = true }; break;
                case "DisabledByEnvironment": facts = facts with { DisabledByEnvironment = true }; break;
                case "AlreadyRelaunched": facts = facts with { AlreadyRelaunched = true }; break;
                case "IsDebuggerAttached": facts = facts with { IsDebuggerAttached = true }; break;
                case "HasInteractiveConsole": facts = facts with { HasInteractiveConsole = false }; break;
                case "IsHostedByWindowsTerminal": facts = facts with { IsHostedByWindowsTerminal = true }; break;
                case "WindowsTerminalAvailable": facts = facts with { WindowsTerminalAvailable = false }; break;
                case "AnotherInstanceRunning": facts = facts with { AnotherInstanceRunning = true }; break;
                default: throw new ArgumentOutOfRangeException(nameof(blocker));
            }

            var decision = WindowsTerminalHost.Evaluate(facts);

            Assert.False(decision.Relocate);
            Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        }

        [Fact]
        public void BuildTerminalCommandLine_QuotesPathsWithSpaces_AndAppendsMarker()
        {
            var commandLine = WindowsTerminalHost.BuildTerminalCommandLine(
                @"D:\My Apps\GameHelper.ConsoleHost.exe",
                new[] { "monitor", "--debug" });

            Assert.Equal(
                @"-w -1 -d ""D:\My Apps"" -- ""D:\My Apps\GameHelper.ConsoleHost.exe"" monitor --debug --in-windows-terminal",
                commandLine);
        }

        [Fact]
        public void BuildTerminalCommandLine_KeepsPlainPathUnquoted_AndEscapesEmbeddedQuotes()
        {
            var commandLine = WindowsTerminalHost.BuildTerminalCommandLine(
                @"C:\Tools\GameHelper.ConsoleHost.exe",
                new[] { "a\"b" });

            Assert.Equal(
                @"-w -1 -d C:\Tools -- C:\Tools\GameHelper.ConsoleHost.exe ""a\""b"" --in-windows-terminal",
                commandLine);
        }

        [Fact]
        public void ArgumentParser_RemovesRelaunchMarker_FromEffectiveArgs()
        {
            var parsed = ArgumentParser.Parse(new[] { "monitor", WindowsTerminalHost.RelaunchedArgument });

            Assert.Equal(new[] { "monitor" }, parsed.EffectiveArgs);
        }

        [Fact]
        public void ArgumentParser_ReadsMinimizedFlag_AndKeepsCommandArgs()
        {
            var parsed = ArgumentParser.Parse(new[] { "monitor", "--minimized" });

            Assert.True(parsed.Minimized);
            Assert.Equal(new[] { "monitor" }, parsed.EffectiveArgs);
        }
    }
}
