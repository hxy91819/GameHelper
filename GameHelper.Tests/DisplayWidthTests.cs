using GameHelper.ConsoleHost.Utilities;

namespace GameHelper.Tests
{
    public class DisplayWidthTests
    {
        [Theory]
        [InlineData("", 0)]
        [InlineData("abc", 3)]
        [InlineData("Cheat Engine", 12)]
        [InlineData("三国无双", 8)]
        [InlineData("✨", 2)]
        public void Measure_ReturnsExpectedWidth(string text, int expected)
        {
            Assert.Equal(expected, DisplayWidth.Measure(text));
        }

        [Fact]
        public void PadRight_PreservesDisplayWidth()
        {
            const string value = "三国无双";
            int targetWidth = DisplayWidth.Measure(value) + 4;

            string padded = DisplayWidth.PadRight(value, targetWidth);

            Assert.Equal(targetWidth, DisplayWidth.Measure(padded));
            Assert.EndsWith("    ", padded);
        }
    }
}
