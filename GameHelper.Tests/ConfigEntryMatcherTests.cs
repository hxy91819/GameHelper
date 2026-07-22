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

    [Fact]
    public void FindExistingForAdd_WhenSameSteamInstallDir_ReturnsSoftMatch()
    {
        // Existing entry points at the game binary nested under the Steam install dir;
        // the incoming identity is the launcher exe at the install dir root.
        var existing = new GameConfig
        {
            DataKey = "sb-win64-shipping",
            Executable = Path.Combine(
                "D:", "Program Files (x86)", "Steam", "steamapps", "common", "StellarBlade",
                "SB", "Binaries", "Win64", "SB-Win64-Shipping.exe")
        };
        var incoming = ExecutableIdentity.Parse(Path.Combine(
            "D:", "Program Files (x86)", "Steam", "steamapps", "common", "StellarBlade",
            "crs-handler.exe"));

        var match = ConfigEntryMatcher.FindExistingForIntake(new[] { existing }, incoming);

        Assert.Same(existing, match);
    }

    [Fact]
    public void FindExistingForAdd_WhenSameSteamInstallDir_MultipleCandidates_ReturnsNull()
    {
        var installDir = Path.Combine("D:", "Steam", "steamapps", "common", "DupGame");
        var configs = new[]
        {
            new GameConfig { DataKey = "dup-a", Executable = Path.Combine(installDir, "a.exe") },
            new GameConfig { DataKey = "dup-b", Executable = Path.Combine(installDir, "sub", "b.exe") }
        };
        var incoming = ExecutableIdentity.Parse(Path.Combine(installDir, "launcher.exe"));

        var match = ConfigEntryMatcher.FindExistingForIntake(configs, incoming);

        Assert.Null(match);
    }

    [Fact]
    public void FindExistingForAdd_WhenNonSteamCommonDir_DoesNotSoftMatch()
    {
        // Same parent directory but no steamapps/common segment -> must not soft match.
        var dir = Path.Combine("C:", "Games", "X");
        var configs = new[]
        {
            new GameConfig { DataKey = "x-a", Executable = Path.Combine(dir, "a.exe") }
        };
        var incoming = ExecutableIdentity.Parse(Path.Combine(dir, "b.exe"));

        var match = ConfigEntryMatcher.FindExistingForIntake(configs, incoming);

        Assert.Null(match);
    }

    [Fact]
    public void FindExistingForAdd_WhenExistingHasNoPath_DoesNotSoftMatch()
    {
        // Existing entry has no path, so even if the incoming identity is under
        // steamapps/common, soft match must not fire (no Steam install dir to compare).
        // Use a different file name so the single-name-only rule does not apply.
        var configs = new[]
        {
            new GameConfig { DataKey = "nameonly", Executable = "other.exe" }
        };
        var incoming = ExecutableIdentity.Parse(Path.Combine(
            "D:", "Steam", "steamapps", "common", "SomeGame", "launcher.exe"));

        var match = ConfigEntryMatcher.FindExistingForIntake(configs, incoming);

        Assert.Null(match);
    }
}
