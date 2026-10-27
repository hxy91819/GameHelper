using GameHelper.Core.Models;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class GameActionEditorTests
{
    [Fact]
    public void Editor_AddEditReorderDeleteAndSave_PreservesExecutionOrder()
    {
        var edits = 0;
        var editor = new GameActionEditor(null, () => edits++);
        var first = editor.Add(); first.Path = @"C:\Tools\first.exe";
        var second = editor.Add(); second.When = "afterExit"; second.Type = "close"; second.Path = @"C:\Tools\second.exe";
        editor.Move(second, -1);
        Assert.Equal(new[] { second.Path, first.Path }, editor.Save()!.Select(action => action.Path));
        editor.Move(second, -1); // Moving past the edge is a no-op.
        editor.Remove(first);
        Assert.Equal("close", Assert.Single(editor.Save()!).Type);
        Assert.True(edits >= 8);
        editor.Remove(second);
        Assert.Null(editor.Save());
    }

    [Fact]
    public void Editor_DraftRetainsInvalidInputButSaveRejectsIt()
    {
        var editor = new GameActionEditor(null, () => { });
        editor.Add().Path = "unfinished";
        var restored = new GameActionEditor(editor.Snapshot(), () => { });
        Assert.Equal("unfinished", Assert.Single(restored.Items).Path);
        Assert.Throws<ArgumentException>(() => restored.Save());
    }

    [Fact]
    public void Editor_CloseDisablesWaitAndLeavesSourceConfigurationUntouched()
    {
        var original = new GameAction { When = "beforeLaunch", Type = "run", Path = @"C:\Tools\first.exe", Wait = true };
        var editor = new GameActionEditor([original], () => { });
        var item = Assert.Single(editor.Items);
        item.Type = "close";
        Assert.False(item.IsRun);
        Assert.Null(Assert.Single(editor.Save()!).Wait);
        Assert.Equal("run", original.Type);
        Assert.True(original.Wait);
    }
}
