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
            EntryId = "entry-a",
            DataKey = "game-a",
            ExecutableName = "game.exe",
            ExecutablePath = Path.Combine("C:", "Games", "Game", "game.exe")
        };
        var configs = new[]
        {
            existing,
            new GameConfig
            {
                EntryId = "entry-b",
                DataKey = "game-b",
                ExecutableName = "game.exe",
                ExecutablePath = Path.Combine("D:", "Games", "Game", "game.exe")
            }
        };

        var match = ConfigEntryMatcher.FindExistingForAdd(configs, "game.exe", existing.ExecutablePath);

        Assert.Same(existing, match);
    }

    [Fact]
    public void FindExistingForAdd_WhenSingleNameOnlyCandidate_ReturnsNameMatch()
    {
        var existing = new GameConfig
        {
            EntryId = "entry-a",
            DataKey = "game-a",
            ExecutableName = "game.exe"
        };

        var match = ConfigEntryMatcher.FindExistingForAdd(new[] { existing }, "game.exe", null);

        Assert.Same(existing, match);
    }

    [Fact]
    public void FindExistingForAdd_WhenMultipleNameCandidatesWithoutPath_ReturnsNull()
    {
        var configs = new[]
        {
            new GameConfig { EntryId = "entry-a", DataKey = "game-a", ExecutableName = "game.exe" },
            new GameConfig { EntryId = "entry-b", DataKey = "game-b", ExecutableName = "game.exe" }
        };

        var match = ConfigEntryMatcher.FindExistingForAdd(configs, "game.exe", null);

        Assert.Null(match);
    }
}
