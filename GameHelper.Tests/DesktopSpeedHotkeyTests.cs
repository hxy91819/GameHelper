using GameHelper.Core.Models;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class DesktopSpeedHotkeyTests
{
    [Fact]
    public void Settings_EditPresetsAndToggle_RoundtripsAndClears()
    {
        var item = new LibraryGameItem(new() { Config = new()
        {
            DataKey = "game", Executable = "game.exe", SpeedMultiplier = 2,
            SpeedHotkeys = [new() { Hotkey = "Ctrl+F1", Speed = 1.5 }]
        } });
        Assert.Equal("Ctrl+F1=1.5", item.SpeedHotkeys);
        item.SpeedHotkeys = "Ctrl+F1=3\nCtrl+F2=1\nCtrl+F3=toggle";
        Assert.True(item.IsDirty);
        var saved = item.ToConfig();
        Assert.Equal(2, saved.SpeedMultiplier);
        Assert.Equal(new double?[] { 3, 1, null }, saved.SpeedHotkeys!.Select(key => key.Speed));
        item.SpeedHotkeys = "";
        Assert.Null(item.ToConfig().SpeedHotkeys);
    }

    [Theory]
    [InlineData("Ctrl+F1=")]
    [InlineData("Ctrl+F1=0")]
    [InlineData("Ctrl+F1=NaN")]
    [InlineData("Ctrl+F1=2\nF1+Ctrl=3")]
    public void Settings_RejectInvalidHotkeyText(string text)
    {
        var item = new LibraryGameItem(new() { Config = new() { DataKey = "game", Executable = "game.exe", SpeedMultiplier = 2 } });
        item.SpeedHotkeys = text;
        Assert.Throws<ArgumentException>(() => item.ToConfig());
    }

    [Fact]
    public void DraftStore_PreservesIncompleteTextAndBaselineAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-hotkey-draft-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configPath = Path.Combine(root, "config.yml");
            var row = new LibraryGame { Config = new() { DataKey = "game", Executable = "game.exe", SpeedMultiplier = 2 }, Revision = "baseline" };
            var item = new LibraryGameItem(row) { SpeedHotkeys = "Ctrl+F1=", Speed = "2." };
            new LibraryDraftStore(configPath).Write([item.ExportDraft()]);
            var restored = LibraryGameItem.RestoreDraft(Assert.Single(new LibraryDraftStore(configPath).Read()));
            Assert.Equal("Ctrl+F1=", restored.SpeedHotkeys);
            Assert.Equal("2.", restored.Speed);
            Assert.Equal("baseline", restored.Revision);
            Assert.True(restored.IsDirty);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void DraftStore_LegacyDraftWithoutHotkeyField_RemainsReadable()
    {
        const string legacy = """{"Baseline":{"Config":{"DataKey":"game","Executable":"game.exe"}},"Name":"game","Executable":"game.exe","Target":"","Arguments":"","Directory":"","Speed":"2","Hdr":false,"Enabled":true,"UseSpeed":true}""";
        var draft = System.Text.Json.JsonSerializer.Deserialize<LibraryDraft>(legacy)!;
        var item = LibraryGameItem.RestoreDraft(draft);
        Assert.Equal(string.Empty, item.SpeedHotkeys);
        Assert.Equal(2, item.ToConfig().SpeedMultiplier);
        Assert.Null(item.ToConfig().SpeedHotkeys);
    }
}
