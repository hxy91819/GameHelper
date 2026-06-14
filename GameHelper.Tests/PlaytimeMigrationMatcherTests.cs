using GameHelper.Core.Models;
using GameHelper.Core.Services;

namespace GameHelper.Tests;

public sealed class PlaytimeMigrationMatcherTests
{
    private readonly PlaytimeMigrationMatcher _matcher = new();

    [Fact]
    public void Match_ExistingDataKey_ReturnsAlreadyDataKey()
    {
        var match = _matcher.Match("witcher3", new[]
        {
            new GameConfig { DataKey = "witcher3", ExecutableName = "witcher3.exe" }
        });

        Assert.Equal(PlaytimeMigrationMatchKind.AlreadyDataKey, match.Kind);
        Assert.False(match.ShouldRewrite);
        Assert.Equal("witcher3", match.DataKey);
    }

    [Fact]
    public void Match_ExactExecutableName_ReturnsDataKey()
    {
        var match = _matcher.Match("WITCHER3.EXE", new[]
        {
            new GameConfig { DataKey = "witcher3", ExecutableName = "witcher3.exe" }
        });

        Assert.Equal(PlaytimeMigrationMatchKind.ExactExecutableName, match.Kind);
        Assert.True(match.ShouldRewrite);
        Assert.Equal("witcher3", match.DataKey);
    }

    [Fact]
    public void Match_ExecutableNameWithoutExtension_NormalizesBeforeExactMatch()
    {
        var match = _matcher.Match("Game.exe", new[]
        {
            new GameConfig { DataKey = "game", ExecutableName = "Game" }
        });

        Assert.Equal(PlaytimeMigrationMatchKind.ExactExecutableName, match.Kind);
        Assert.True(match.ShouldRewrite);
        Assert.Equal("game", match.DataKey);
    }

    [Fact]
    public void Match_ExecutableNamePath_NormalizesToFileNameBeforeExactMatch()
    {
        var match = _matcher.Match("Game.exe", new[]
        {
            new GameConfig { DataKey = "game", ExecutableName = @"C:\Games\Game\Game.exe" }
        });

        Assert.Equal(PlaytimeMigrationMatchKind.ExactExecutableName, match.Kind);
        Assert.True(match.ShouldRewrite);
        Assert.Equal("game", match.DataKey);
    }

    [Fact]
    public void Match_FuzzyExecutableName_UsesCoreThreshold()
    {
        var match = _matcher.Match("ProjectPlague.exe", new[]
        {
            new GameConfig { DataKey = "project_plague", ExecutableName = "Project_Plague.exe" }
        });

        Assert.Equal(PlaytimeMigrationMatchKind.FuzzyExecutableName, match.Kind);
        Assert.True(match.ShouldRewrite);
        Assert.Equal("project_plague", match.DataKey);
        Assert.True(match.Score >= GameMatcher.CalculateFuzzyThreshold("Project_Plague.exe"));
    }

    [Fact]
    public void Match_DuplicateExecutableName_ReturnsAmbiguous()
    {
        var match = _matcher.Match("launcher.exe", new[]
        {
            new GameConfig { DataKey = "game_one", ExecutableName = "launcher.exe" },
            new GameConfig { DataKey = "game_two", ExecutableName = "launcher.exe" }
        });

        Assert.Equal(PlaytimeMigrationMatchKind.Ambiguous, match.Kind);
        Assert.False(match.ShouldRewrite);
        Assert.Null(match.DataKey);
    }

    [Fact]
    public void Match_UnknownGame_ReturnsNotFound()
    {
        var match = _matcher.Match("unknown.exe", new[]
        {
            new GameConfig { DataKey = "witcher3", ExecutableName = "witcher3.exe" }
        });

        Assert.Equal(PlaytimeMigrationMatchKind.NotFound, match.Kind);
        Assert.False(match.ShouldRewrite);
        Assert.Null(match.DataKey);
    }
}
