using GameHelper.Core.Utilities;

namespace GameHelper.Tests;

public sealed class HotkeyBindingParserTests
{
    [Fact]
    public void TryParse_ValidBinding_NormalizesDisplayText()
    {
        var success = HotkeyBindingParser.TryParse("control+alt+f10", out var binding, out var error);

        Assert.True(success, error);
        Assert.NotNull(binding);
        Assert.Equal("Ctrl+Alt+F10", binding!.DisplayText);
    }

    [Fact]
    public void TryParse_InvalidBinding_ReturnsError()
    {
        var success = HotkeyBindingParser.TryParse("f10", out var binding, out var error);

        Assert.False(success);
        Assert.Null(binding);
        Assert.Contains("修饰键", error);
    }
}
