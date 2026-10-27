using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class GlobalSettingsDraftTests
{
    [Fact]
    public void SaveDisplayChoices_PreservesOfflineRules()
    {
        var snapshot = new AppSettingsSnapshot
        {
            Hdr = new() { AllowedDisplays = ["tv-path", "offline-tv"], DisabledDisplays = ["Desk", "offline-desk"] },
            DisplaysAvailable = true,
            Displays = [new("TV", "tv-path", true, false, false, true), new("Desk", "desk-path", true, false, false, false)],
            MonitorMode = new() { Latitude = 20, Longitude = 100 }
        };
        var draft = new GlobalSettingsViewModel(snapshot);
        draft.Displays[0].Selected = false; draft.Displays[1].Selected = true;
        var request = draft.ToRequest();
        Assert.Equal(new[] { "offline-tv", "desk-path" }, request.Hdr!.AllowedDisplays);
        Assert.Equal(new[] { "offline-desk", "tv-path" }, request.Hdr.DisabledDisplays);
        Assert.Equal(20, request.MonitorMode!.Latitude);
        Assert.Equal(100, request.MonitorMode.Longitude);
        Assert.Equal(new[] { "tv-path", "offline-tv" }, snapshot.Hdr.AllowedDisplays);
    }

    [Fact]
    public void DefaultSpeedHotkeys_EditAsTextAndRejectInvalidLines()
    {
        var draft = new GlobalSettingsViewModel(new()
        {
            CheatEngine = new() { Executable = @"C:\ce.exe", SpeedHotkeys = [new() { Hotkey = "Ctrl+F1", Speed = 2 }] }
        });
        Assert.Equal("Ctrl+F1=2", draft.DefaultSpeedHotkeys);
        draft.DefaultSpeedHotkeys = "Ctrl+F1=0.5\nCtrl+F2=toggle";
        var request = draft.ToRequest();
        Assert.Equal(@"C:\ce.exe", request.CheatEngine!.Executable);
        Assert.Equal(new double?[] { 0.5, null }, request.CheatEngine.SpeedHotkeys!.Select(binding => binding.Speed));
        draft.DefaultSpeedHotkeys = "";
        Assert.Null(draft.ToRequest().CheatEngine!.SpeedHotkeys);
        draft.DefaultSpeedHotkeys = "Ctrl+F1=";
        Assert.Throws<ArgumentException>(() => draft.ToRequest());
    }

    [Fact]
    public void UnavailableDisplays_PreservesAllRules_AndDefaultPolicyUsesExclusions()
    {
        var saved = new HdrSettings { AllowedDisplays = ["offline"], DisabledDisplays = ["other"] };
        var request = new GlobalSettingsViewModel(new() { Hdr = saved }).ToRequest();
        Assert.Equal(saved.AllowedDisplays, request.Hdr!.AllowedDisplays);
        Assert.Equal(saved.DisabledDisplays, request.Hdr.DisabledDisplays);
        var draft = new GlobalSettingsViewModel(new() { Displays = [new("Desk", "desk-path", true, false, false, true)] });
        draft.Displays[0].Selected = false;
        Assert.Empty(draft.ToRequest().Hdr!.AllowedDisplays);
        Assert.Equal("desk-path", Assert.Single(draft.ToRequest().Hdr!.DisabledDisplays));
    }
}
