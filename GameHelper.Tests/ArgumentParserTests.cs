using GameHelper.ConsoleHost.Utilities;
using Xunit;

namespace GameHelper.Tests
{
    public class ArgumentParserTests
    {
        [Fact]
        public void Parse_WithInteractiveFlag_ShouldSetInteractiveModeAndRemoveFlag()
        {
            var args = new[] { "--interactive", "monitor" };

            var result = ArgumentParser.Parse(args);

            Assert.True(result.UseInteractiveShell);
            Assert.Contains("monitor", result.EffectiveArgs);
            Assert.DoesNotContain(result.EffectiveArgs, a => a.Equals("--interactive", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_WithMenuAlias_ShouldSetInteractiveMode()
        {
            var args = new[] { "--menu" };

            var result = ArgumentParser.Parse(args);

            Assert.True(result.UseInteractiveShell);
            Assert.Empty(result.EffectiveArgs);
        }

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

        [Fact]
        public void Parse_WithNoTrayFlag_ShouldDisableTrayAndStripFlag()
        {
            var args = new[] { "--no-tray", "interactive" };

            var result = ArgumentParser.Parse(args);

            Assert.True(result.DisableTray);
            Assert.Contains("interactive", result.EffectiveArgs);
            Assert.DoesNotContain(result.EffectiveArgs, a => a.Equals("--no-tray", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_WithoutNoTrayFlag_DisableTrayShouldBeFalse()
        {
            var args = new[] { "interactive" };

            var result = ArgumentParser.Parse(args);

            Assert.False(result.DisableTray);
        }

        [Fact]
        public void Parse_NoTrayFlagIsCaseInsensitive()
        {
            var args = new[] { "--NO-TRAY" };

            var result = ArgumentParser.Parse(args);

            Assert.True(result.DisableTray);
            Assert.Empty(result.EffectiveArgs);
        }

        [Fact]
        public void Parse_WithTraySmokeTestFlag_ShouldEnableSmokeTestAndStripFlag()
        {
            var args = new[] { "--tray-smoke-test" };

            var result = ArgumentParser.Parse(args);

            Assert.True(result.RunTraySmokeTest);
            Assert.Empty(result.EffectiveArgs);
        }

        [Fact]
        public void Parse_WithoutTraySmokeTestFlag_ShouldDefaultToFalse()
        {
            var result = ArgumentParser.Parse(Array.Empty<string>());

            Assert.False(result.RunTraySmokeTest);
        }
    }
}
