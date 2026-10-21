using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Resolvers;
using Moq;

namespace GameHelper.Tests;

public sealed class LocalGameDiscoveryTests
{
    [Fact]
    public async Task Scan_ExcludesHelpersRanksShippingAndUsesCatalogDuplicatePolicy()
    {
        using var root = new Folder();
        var game = root.File("Adventure/game.exe", 10);
        var shipping = root.File("Adventure/Binaries/Win64/game-Win64-Shipping.exe", 20);
        root.File("Adventure/unins000.exe"); root.File("Adventure/CrashReportClient.exe"); root.File("Adventure/Engine/tool.exe");
        var second = root.File("Other/other.exe");
        var crashGame = root.File("Crash Bandicoot 4/CrashBandicoot4.exe");
        var existing = new[] { new GameConfig { DataKey = "old", Executable = second, HdrEnabled = true, SpeedMultiplier = 2 } };
        var scan = await Discovery().ScanAsync(root.Path, existing, CancellationToken.None);
        Assert.False(scan.Truncated);
        Assert.Equal(4, scan.Candidates.Count);
        Assert.Contains(scan.Candidates, item => item.ExecutablePath == crashGame);
        Assert.True(scan.Candidates.Single(item => item.ExecutablePath == shipping).Recommended);
        Assert.False(scan.Candidates.Single(item => item.ExecutablePath == game).Recommended);
        Assert.True(scan.Candidates.Single(item => item.ExecutablePath == second).AlreadyAdded);
        Assert.Equal("Adventure", scan.Candidates.Single(item => item.ExecutablePath == shipping).Name);
        Assert.True(existing[0].HdrEnabled); Assert.Equal(2, existing[0].SpeedMultiplier);
    }

    [Fact]
    public async Task Scan_IsCancellableAndReportsDepthLimit()
    {
        using var root = new Folder();
        root.File(string.Join('/', Enumerable.Repeat("nested", 10)) + "/game.exe");
        var scan = await Discovery().ScanAsync(root.Path, [], CancellationToken.None);
        Assert.True(scan.Truncated);
        Assert.Empty(scan.Candidates);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Discovery().ScanAsync(root.Path, [], cancelled.Token));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Discovery().ScanAsync(System.IO.Path.Combine(root.Path, "missing"), [], CancellationToken.None));
    }

    [Fact]
    public void Assess_SeparatesMissingPathsFromUnknownAndDetectsReinstallationWithoutChangingConfig()
    {
        using var root = new Folder();
        var path = root.File("game.exe");
        var discovery = Discovery();
        var known = new GameConfig { DataKey = "known", Executable = path };
        var unknown = new GameConfig { DataKey = "legacy", Executable = "unknown.exe", Launch = new() { Target = "steam://rungameid/999999" } };
        Assert.Equal(GameInstallationState.Installed, discovery.Assess([known, unknown])["known"].State);
        Assert.Equal(GameInstallationState.Unknown, discovery.Assess([known, unknown])["legacy"].State);
        File.Delete(path);
        Assert.Equal(GameInstallationState.Missing, discovery.Assess([known])["known"].State);
        root.File("game.exe");
        Assert.Equal(GameInstallationState.Installed, discovery.Assess([known])["known"].State);
        Assert.Equal(path, known.Executable);
    }

    [Fact]
    public void Assess_ConfirmsNameOnlyGamesUsingRealSteamExecutableAndInvalidatesInventoryByInjectedClock()
    {
        using var root = new Folder();
        var path = root.File("game.exe");
        var steam = new Mock<ISteamGameResolver>();
        steam.Setup(item => item.TryEnumerateInstalledGames()).Returns([new() { AppId = "620", Name = "Game", ExecutablePath = path }]);
        var clock = new Clock();
        var discovery = new LocalGameDiscovery(steam.Object, clock);
        var game = new GameConfig { DataKey = "a", Executable = "game.exe" };
        Assert.Equal(GameInstallationState.Installed, discovery.Assess([game])["a"].State);
        discovery.Assess([game]); steam.Verify(item => item.TryEnumerateInstalledGames(), Times.Once);
        clock.Now += TimeSpan.FromSeconds(31);
        discovery.Assess([game]); steam.Verify(item => item.TryEnumerateInstalledGames(), Times.Exactly(2));
    }

    private static LocalGameDiscovery Discovery()
    {
        var steam = new Mock<ISteamGameResolver>(); steam.Setup(item => item.TryEnumerateInstalledGames()).Returns([]);
        return new LocalGameDiscovery(steam.Object);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gh-discovery", Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public string File(string relative, int size = 1)
        {
            var file = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!); System.IO.File.WriteAllBytes(file, new byte[size]); return file;
        }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
