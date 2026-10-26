using GameHelper.Core.Models;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class LibrarySpeedHotkeyDraftTests
{
    [Fact]
    public void Store_RoundtripsIncompleteHotkeysWithoutApplyingThemToBaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-draft-hotkeys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "config.yml");
            var baseline = new LibraryGame { Config = new() { DataKey = "game", Executable = "game.exe", SpeedMultiplier = 2 }, Revision = "previous" };
            var draft = new LibraryDraft(baseline, "game", "game.exe", "", "", "", "2.", false, true, true,
                SpeedHotkeys: "Ctrl+F1=\nCtrl+F2=toggle");
            new LibraryDraftStore(path).Write([draft]);
            var restored = Assert.Single(new LibraryDraftStore(path).Read());
            Assert.Equal(draft.SpeedHotkeys, restored.SpeedHotkeys);
            Assert.Equal("2.", restored.Speed);
            Assert.Null(restored.Baseline.Config.SpeedHotkeys);
            Assert.Equal("previous", restored.Baseline.Revision);
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacyDraft_MissingNewFieldDefaultsToEmptyText()
    {
        const string json = """{"Baseline":{"Config":{"DataKey":"game","Executable":"game.exe"}},"Name":"game","Executable":"game.exe","Target":"","Arguments":"","Directory":"","Speed":"2","Hdr":false,"Enabled":true,"UseSpeed":true}""";
        var draft = System.Text.Json.JsonSerializer.Deserialize<LibraryDraft>(json)!;
        Assert.Equal(string.Empty, draft.SpeedHotkeys);
    }
}
