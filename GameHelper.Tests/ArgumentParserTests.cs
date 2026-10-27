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

    }
}
