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
        Assert.Equal(4, scan.Candidates.Count(item => !item.IsAuxiliary));
        Assert.All(scan.Candidates.Where(item => item.IsAuxiliary), item => Assert.False(item.Recommended));
        Assert.Contains(scan.Candidates, item => item.ExecutablePath == crashGame);
        Assert.True(scan.Candidates.Single(item => item.ExecutablePath == shipping).Recommended);
        Assert.False(scan.Candidates.Single(item => item.ExecutablePath == game).Recommended);
        Assert.True(scan.Candidates.Single(item => item.ExecutablePath == second).AlreadyAdded);
        Assert.Equal("Adventure", scan.Candidates.Single(item => item.ExecutablePath == shipping).Name);
        Assert.True(existing[0].HdrEnabled); Assert.Equal(2, existing[0].SpeedMultiplier);
    }

    [Fact]
    public async Task Scan_PrefersGameEvidenceOverLargerUtilitiesAndRetainsAuxiliaryChoices()
    {
        using var root = new Folder();
        var main = root.File("Brotato/Brotato.exe", 10);
        root.File("Brotato/Brotato.pck");
        var workshop = root.File("Brotato/GodotWorkshopUtility.exe", 1000);
        var crc = root.File("Silksong/RapidCRC.exe", 1000);
        var crashpad = root.File("Silksong/crashpad_handler.exe", 1000);
        var silk = root.File("Silksong/Silksong.exe", 10);
        root.File("Silksong/UnityPlayer.dll"); Directory.CreateDirectory(Path.Combine(root.Path, "Silksong/Silksong_Data"));
        var installer = root.File("Other/_Redist/dxwebsetup.exe", 1000);
        var scan = await Discovery().ScanAsync(root.Path, [], CancellationToken.None);
        Assert.True(scan.Candidates.Single(item => item.ExecutablePath == main).Recommended);
        Assert.Contains(".pck", scan.Candidates.Single(item => item.ExecutablePath == main).Evidence);
        Assert.True(scan.Candidates.Single(item => item.ExecutablePath == silk).Recommended);
        Assert.Contains("Unity", scan.Candidates.Single(item => item.ExecutablePath == silk).Evidence);
        foreach (var path in new[] { workshop, crc, installer, crashpad })
        {
            var item = scan.Candidates.Single(item => item.ExecutablePath == path);
            Assert.True(item.IsAuxiliary); Assert.False(item.Recommended);
        }
    }

    [Fact]
    public async Task Scan_SingleGameRootKeepsLauncherAndBinariesInOneGroup()
    {
        using var root = new Folder();
        root.File("Launcher.exe", 1000);
        var main = root.File("Binaries/Win64/Game-Win64-Shipping.exe", 10);
        var scan = await Discovery().ScanAsync(root.Path, [], CancellationToken.None);
        Assert.Single(scan.Candidates.Select(item => item.Folder).Distinct());
        Assert.Equal(main, Assert.Single(scan.Candidates.Where(item => item.Recommended)).ExecutablePath);
    }

    [Fact]
    public async Task Scan_GodotRootWithDifferentDirectoryNameStillKeepsSubprogramsTogether()
    {
        using var root = new Folder();
        var main = root.File("Brotato.exe"); root.File("Brotato.pck"); root.File("tools/extra.exe", 1000);
        var scan = await Discovery().ScanAsync(root.Path, [], CancellationToken.None);
        Assert.Single(scan.Candidates.Select(item => item.Folder).Distinct());
        Assert.Equal(main, Assert.Single(scan.Candidates.Where(item => item.Recommended)).ExecutablePath);
    }

    [Fact]
    public async Task Scan_SizeAloneAndTiedGameEvidenceDoNotEstablishRecommendation()
    {
        using var root = new Folder();
        root.File("Unknown/Small.exe", 10); root.File("Unknown/Large.exe", 1000);
        root.File("Bundle/First.exe"); root.File("Bundle/First.pck");
        root.File("Bundle/Second.exe", 1000); root.File("Bundle/Second.pck");
        var scan = await Discovery().ScanAsync(root.Path, [], CancellationToken.None);
        Assert.DoesNotContain(scan.Candidates, item => item.Recommended);
        Assert.All(scan.Candidates.Where(item => item.Folder == "Bundle"), item => Assert.Contains("多个", item.Evidence));
    }

    [Fact]
    public async Task Scan_RenpyLauncherIsPreferredAndKnownUnityAssetsDoNotConsumeDepthBudget()
    {
        using var root = new Folder();
        var renpy = root.File("Story/Story.exe");
        Directory.CreateDirectory(Path.Combine(root.Path, "Story/renpy")); Directory.CreateDirectory(Path.Combine(root.Path, "Story/game"));
        root.File("Story/lib/windows-x86_64/pythonw.exe", 1000);
        root.File("Unity/Unity.exe"); root.File("Unity/UnityPlayer.dll");
        root.File("Unity/Unity_Data/" + string.Join('/', Enumerable.Repeat("assets", 10)) + "/helper.exe");
        var scan = await Discovery().ScanAsync(root.Path, [], CancellationToken.None);
        Assert.False(scan.Truncated);
        Assert.True(scan.Candidates.Single(item => item.ExecutablePath == renpy).Recommended);
        Assert.DoesNotContain(scan.Candidates, item => item.ExecutablePath.EndsWith("helper.exe"));
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
