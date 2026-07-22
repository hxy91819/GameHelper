using GameHelper.Core.Models;

namespace GameHelper.Tests;

public sealed class ExecutableIdentityTests
{
    [Fact]
    public void Parse_NameOnly_ExposesOnlyDerivedName()
    {
        var identity = ExecutableIdentity.Parse("game.exe");

        Assert.Equal("game.exe", identity.Value);
        Assert.Equal("game.exe", identity.Name);
        Assert.Null(identity.Path);
        Assert.False(identity.IsPath);
    }

    [Fact]
    public void Parse_Path_ExposesDerivedNameAndPath()
    {
        var identity = ExecutableIdentity.Parse(@"C:\Games\Game\game.exe");

        Assert.Equal(@"C:\Games\Game\game.exe", identity.Value);
        Assert.Equal("game.exe", identity.Name);
        Assert.Equal(identity.Value, identity.Path);
        Assert.True(identity.IsPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\Games\\")]
    public void Parse_InvalidIdentity_Throws(string value)
    {
        Assert.Throws<ArgumentException>(() => ExecutableIdentity.Parse(value));
    }
}
