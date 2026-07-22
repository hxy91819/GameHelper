using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

namespace GameHelper.Tests;

public sealed class ConfigEntryMatcherTests
{
    [Fact]
    public void FindExistingForAdd_WhenPathMatches_ReturnsPathMatch()
    {
        var existing = new GameConfig
        {
            DataKey = "game-a",
            Executable = Path.Combine("C:", "Games", "Game", "game.exe")
        };
        var configs = new[]
        {
            existing,
            new GameConfig
            {
                DataKey = "game-b",
                Executable = Path.Combine("D:", "Games", "Game", "game.exe")
            }
        };

        var match = ConfigEntryMatcher.FindExistingForIntake(configs, ExecutableIdentity.Parse(existing.Executable!));

        Assert.Same(existing, match);
    }

    [Fact]
    public void FindExistingForAdd_WhenSingleNameOnlyCandidate_ReturnsNameMatch()
    {
        var existing = new GameConfig
        {
            DataKey = "game-a",
            Executable = "game.exe"
        };

        var match = ConfigEntryMatcher.FindExistingForIntake(new[] { existing }, ExecutableIdentity.Parse("game.exe"));

        Assert.Same(existing, match);
    }

    [Fact]
    public void FindExistingForAdd_WhenMultipleNameCandidatesWithoutPath_ReturnsNull()
    {
        var configs = new[]
        {
            new GameConfig { DataKey = "game-a", Executable = "game.exe" },
            new GameConfig { DataKey = "game-b", Executable = "game.exe" }
        };

        var match = ConfigEntryMatcher.FindExistingForIntake(configs, ExecutableIdentity.Parse("game.exe"));

        Assert.Null(match);
    }
}
