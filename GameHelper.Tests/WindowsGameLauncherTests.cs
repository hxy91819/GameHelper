using GameHelper.Core.Models;
using GameHelper.Infrastructure.Processes;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Validators;

namespace GameHelper.Tests;

public sealed class WindowsGameLauncherTests
{
    [Fact]
    public void Companion_AutomaticStartRequestsHiddenWindowWithoutConsole()
    {
        var start = Ds4WindowsProcess.CreateStartInfo(@"C:\Controller\DS4Windows.exe");
        Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Equal(@"C:\Controller", start.WorkingDirectory);
    }
    [Fact]
    public void Start_AllowsForegroundBeforeTheHostCreatesTheGameProcess()
    {
        var calls = new List<string>();
        var launcher = new WindowsGameLauncher(_ => { }, options => calls.Add("game"), () => { }, () => calls.Add("allow"));
        launcher.Start(new() { Target = "steam://rungameid/620" });
        Assert.Equal(new[] { "allow", "game" }, calls);
    }

    [Fact]
    public void DisabledCompanion_PreservesPreUpgradeRevisionTokenForSavedDrafts()
    {
        const string legacy = """{"DataKey":"a","Executable":"a.exe","DisplayName":null,"IsEnabled":true,"HdrEnabled":false,"SpeedMultiplier":null,"SpeedAttach":null,"Launch":{"Target":"steam://rungameid/620","Arguments":null,"WorkingDirectory":null}}""";
        var game = System.Text.Json.JsonSerializer.Deserialize<GameConfig>(legacy)!;
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacy)));
        Assert.Equal(expected, GameHelper.Core.Utilities.GameConfigRevision.For(game));
        game.Launch = game.Launch! with { Ds4WindowsPath = @"C:\Controller\DS4Windows.exe" };
        Assert.NotEqual(expected, GameHelper.Core.Utilities.GameConfigRevision.For(game));
    }

    [Fact]
    public void Companion_AppliesEnabledAndDisabledStateBeforeGame()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-companion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "DS4Windows.exe"); File.WriteAllBytes(path, [1]);
            var calls = new List<string>();
            var launcher = new WindowsGameLauncher(value => { Assert.Equal(path, value); calls.Add("DS4"); }, _ => calls.Add("game"), () => calls.Add("stop DS4"));
            launcher.EnsureCompanionRunning(path);
            Assert.Equal(new[] { "DS4" }, calls);
            calls.Clear();
            launcher.Start(new() { Target = "steam://rungameid/620" }); Assert.Equal(new[] { "stop DS4", "game" }, calls);
            calls.Clear(); launcher.Start(new() { Target = "steam://rungameid/620", Ds4WindowsPath = path });
            Assert.Equal(new[] { "DS4", "game" }, calls);
            calls.Clear();
            var failing = new WindowsGameLauncher(_ => throw new IOException("cannot start DS4"), _ => calls.Add("game"), () => calls.Add("stop DS4"));
            Assert.Throws<IOException>(() => failing.Start(new() { Target = "steam://rungameid/620", Ds4WindowsPath = path })); Assert.Empty(calls);
            File.Delete(path);
            Assert.Throws<ArgumentException>(() => launcher.EnsureCompanionRunning(path));
            Assert.Throws<ArgumentException>(() => launcher.Validate(new() { Target = "steam://rungameid/620", Ds4WindowsPath = path }));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Companion_StopFailurePreventsGameLaunchAndInvalidEntryDoesNotStopCompanion()
    {
        var started = false; var stops = 0;
        var launcher = new WindowsGameLauncher(_ => { }, _ => started = true,
            () => { stops++; throw new IOException("stop failed"); });
        Assert.Throws<IOException>(() => launcher.Start(new() { Target = "steam://rungameid/620" }));
        Assert.False(started); Assert.Equal(1, stops);
        Assert.Throws<ArgumentException>(() => launcher.Start(new() { Target = @"C:\MissingGame.exe" }));
        Assert.Equal(1, stops);
    }

    [Theory]
    [InlineData(@"C:\Sandbox\DS4Windows.exe", true)]
    [InlineData(@"C:\SandboxOther\DS4Windows.exe", false)]
    [InlineData(@"C:\Tools\DS4Windows.exe", false)]
    public void SandboxStopScopeExcludesUserProcesses(string path, bool expected) =>
        Assert.Equal(expected, Ds4WindowsProcess.IsInsideSandbox(path, @"C:\Sandbox"));

    [WindowsOnlyFact]
    public async Task Companion_StopClosesAnOwnedBackgroundProcessAndToleratesAlreadyExited()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe")
        { Arguments = "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60", UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden })!;
        try
        {
            Ds4WindowsProcess.StopProcess(process);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.HasExited);
            Ds4WindowsProcess.StopProcess(process);
        }
        finally { if (!process.HasExited) process.Kill(); }
    }

    [Fact]
    public void Companion_YamlRoundtripsAndRejectsInvalidStoredPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-companion-yaml", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "config.yml"); var config = new YamlConfigProvider(path);
            config.Change(value => value.Games = [new() { DataKey = "a", Executable = "a.exe", Launch = new()
            { Target = "steam://rungameid/620", Ds4WindowsPath = @"C:\Controller\DS4Windows.exe" } }]);
            Assert.Equal(@"C:\Controller\DS4Windows.exe", config.Read().Games.Single().Launch!.Ds4WindowsPath);
            var validation = YamlConfigValidator.Validate(path);
            Assert.True(validation.IsValid, string.Join(";", validation.Errors));
            Assert.DoesNotContain(validation.Warnings, value => value.Contains("ds4WindowsPath", StringComparison.OrdinalIgnoreCase));
            Assert.Throws<ArgumentException>(() => new GameLaunchOptions { Target = "steam://rungameid/620", Ds4WindowsPath = "relative.exe" }.Validate());
            Assert.Throws<ArgumentException>(() => new GameLaunchOptions { Target = "steam://rungameid/620", Ds4WindowsPath = @"C:\controller.cmd" }.Validate());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
