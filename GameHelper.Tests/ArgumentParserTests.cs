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
    }
}
