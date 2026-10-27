using GameHelper.ConsoleHost.Utilities;
using Xunit;

namespace GameHelper.Tests
{
    public class ArgumentParserTests
    {
        [Theory]
        [InlineData("--monitor-dry-run")]
        [InlineData("--monitor-dryrun")]
        public void Parse_WithMonitorDryRunFlag_ShouldEnableDryRunAndStripFlag(string flag)
        {
            var args = new[] { flag, "monitor" };

            var result = ArgumentParser.Parse(args);

            Assert.True(result.MonitorDryRun);
            Assert.Contains("monitor", result.EffectiveArgs);
            Assert.DoesNotContain(result.EffectiveArgs, value => value.Equals(flag, System.StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(new string[0], true)]
        [InlineData(new string[] { "--config", "config.yml" }, true)]
        [InlineData(new string[] { "-c", "config.yml" }, true)]
        [InlineData(new string[] { "--debug" }, false)]
        [InlineData(new string[] { "-v" }, false)]
        [InlineData(new string[] { "--verbose" }, false)]
        [InlineData(new string[] { "--monitor-type", "ETW" }, false)]
        [InlineData(new string[] { "--monitor-dry-run" }, false)]
        [InlineData(new string[] { "--background" }, false)]
        [InlineData(new string[] { "monitor" }, false)]
        [InlineData(new string[] { @"C:\Games\game.exe" }, false)]
        public void Parse_ShouldOpenLibrary_OnlyForBareInvocation(string[] args, bool expected)
        {
            var parsed = ArgumentParser.Parse(args);

            Assert.Equal(expected, StartupModeResolver.ShouldOpenLibrary(parsed));
        }
    }
}
