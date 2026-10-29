using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class ConsoleGameSettingsTests
{
    [Fact]
    public async Task SaveOnlyWritesSettings_PreservesUnrelatedFieldsAndRevision_WithoutLaunchOrConsoleAction()
    {
        var backend = new FakeLibrary();
        var baseline = Row();
        var draft = new ConsoleGameSettings(backend, baseline) { Hdr = true, UseDs4Windows = true, UseSpeed = true };
        draft.AdjustSpeed(1);
        await draft.SaveAsync();
        var request = Assert.Single(backend.Requests);
        Assert.Equal(GameLibraryOperation.Save, request.Operation);
        Assert.Equal("revision-1", request.ExpectedRevision);
        Assert.Equal("game", request.DataKey);
        var saved = Assert.IsType<GameConfig>(request.Game);
        Assert.True(saved.HdrEnabled); Assert.Equal(1.6, saved.SpeedMultiplier);
        Assert.Equal(LibraryGameItem.DefaultDs4WindowsPath, saved.Launch!.Ds4WindowsPath);
        Assert.Equal(baseline.Config.Launch!.Target, saved.Launch.Target);
        Assert.Equal("--safe", saved.Launch.Arguments);
        Assert.Equal(@"C:\Games", saved.Launch.WorkingDirectory);
        Assert.Equal("immediate", saved.SpeedAttach);
        Assert.Equal(DateTimeOffset.UnixEpoch, saved.AddedAt);
        Assert.Same(baseline.Config.Actions, saved.Actions);
        Assert.Same(baseline.Config.SpeedHotkeys, saved.SpeedHotkeys);
        Assert.False(baseline.Config.HdrEnabled);
        Assert.Null(baseline.Config.SpeedMultiplier);
        Assert.Null(baseline.Config.Launch.Ds4WindowsPath);
    }

    [Fact]
    public async Task ConflictRetainsDraftAndOriginalExpectation()
    {
        var backend = new FakeLibrary { Success = false };
        var draft = new ConsoleGameSettings(backend, Row()) { Hdr = true, UseSpeed = true };
        draft.AdjustSpeed(-1);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(draft.SaveAsync);
        Assert.Contains("另一窗口", error.Message);
        Assert.True(draft.Hdr); Assert.True(draft.UseSpeed); Assert.Equal(1.4, draft.Speed);
        await Assert.ThrowsAsync<InvalidOperationException>(draft.SaveAsync);
        Assert.All(backend.Requests, request => Assert.Equal("revision-1", request.ExpectedRevision));
    }

    [Theory]
    [InlineData(-1, 0.1)]
    [InlineData(1, 10)]
    public void RepeatedAdjustmentClampsToSliderRange(int direction, double expected)
    {
        var draft = new ConsoleGameSettings(new FakeLibrary(), Row());
        for (var i = 0; i < 200; i++) draft.AdjustSpeed(direction);
        Assert.Equal(expected, draft.Speed);
    }

    [Fact]
    public async Task ReadingHistoricalMultiplierDoesNotNormalizeUntilAdjusted()
    {
        var baseline = Row(); baseline.Config.SpeedMultiplier = 12.25;
        var backend = new FakeLibrary();
        var draft = new ConsoleGameSettings(backend, baseline) { Hdr = true };
        await draft.SaveAsync();
        Assert.Equal(12.25, Assert.Single(backend.Requests).Game!.SpeedMultiplier);
        draft.AdjustSpeed(-1); Assert.Equal(9.9, draft.Speed);
    }

    [Fact]
    public async Task DisableDs4AndSpeedClearsOnlyTheirValues()
    {
        var baseline = Row();
        baseline.Config.Launch = baseline.Config.Launch! with { Ds4WindowsPath = @"C:\Custom\DS4Windows.exe" };
        baseline.Config.SpeedMultiplier = 2;
        var backend = new FakeLibrary();
        var draft = new ConsoleGameSettings(backend, baseline) { UseDs4Windows = false, UseSpeed = false };
        Assert.Equal(@"C:\Custom\DS4Windows.exe", draft.Ds4WindowsPath);
        await draft.SaveAsync();
        var saved = Assert.Single(backend.Requests).Game!;
        Assert.Null(saved.SpeedMultiplier); Assert.Null(saved.Launch!.Ds4WindowsPath);
        Assert.Equal("--safe", saved.Launch.Arguments);
    }

    [Fact]
    public async Task EnableDs4WithoutLaunchEntryReportsActionableErrorWithoutRequest()
    {
        var baseline = Row(); baseline.Config.Launch = null; baseline.Config.Executable = "game.exe";
        var backend = new FakeLibrary();
        var draft = new ConsoleGameSettings(backend, baseline) { UseDs4Windows = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(draft.SaveAsync);
        Assert.Contains("启动入口", error.Message); Assert.Empty(backend.Requests);
    }

    private static LibraryGame Row() => new()
    {
        Revision = "revision-1", State = "运行中", Config = new()
        {
            DataKey = "game", DisplayName = "Game", Executable = @"C:\Games\game.exe", SpeedAttach = "immediate", AddedAt = DateTimeOffset.UnixEpoch,
            Actions = [], SpeedHotkeys = [], Launch = new() { Target = @"C:\Games\game.exe", Arguments = "--safe", WorkingDirectory = @"C:\Games" }
        }
    };

    private sealed class FakeLibrary : IGameLibrary
    {
        internal bool Success = true;
        internal List<GameLibraryRequest> Requests = [];
        public Task<GameLibraryResponse> ExecuteAsync(GameLibraryRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new GameLibraryResponse { Success = Success, Error = Success ? null : "配置已被另一窗口修改，请重新载入。" });
        }
    }
}
