using GameHelper.Desktop;
using Xunit;
namespace GameHelper.Input.Tests;
public sealed class ConsoleInteractionTests
{
    [Theory]
    [InlineData((int)ConsoleMenuCommand.Hdr,false,true,false,false)]
    [InlineData((int)ConsoleMenuCommand.Hdr,true,false,false,true)]
    [InlineData((int)ConsoleMenuCommand.Pause,false,false,false,false)]
    [InlineData((int)ConsoleMenuCommand.Pause,false,true,false,true)]
    [InlineData((int)ConsoleMenuCommand.Resume,false,true,false,false)]
    [InlineData((int)ConsoleMenuCommand.Resume,false,true,true,true)]
    [InlineData((int)ConsoleMenuCommand.Pause,false,true,true,false)]
    [InlineData((int)ConsoleMenuCommand.Exit,false,false,false,true)]
    public void MenuAvailability(int command,bool hdr,bool reminders,bool paused,bool expected) => Assert.Equal(expected,ConsoleInteractionPolicy.Available((ConsoleMenuCommand)command,hdr,reminders,paused));
    [Fact] public void OnlyPowerActionsRequireSystemConfirmation()
    {
        foreach (var command in Enum.GetValues<ConsoleMenuCommand>())
            Assert.Equal(command is ConsoleMenuCommand.Sleep or ConsoleMenuCommand.Restart or ConsoleMenuCommand.Shutdown,ConsoleInteractionPolicy.NeedsConfirmation(command));
    }
    [Fact] public void OnlyOurEligibleUnratedCompletionInvites()
    {
        var own = Guid.NewGuid();
        Assert.True(ConsoleInteractionPolicy.Invite(own,own,true,false));
        Assert.False(ConsoleInteractionPolicy.Invite(null,own,true,false));
        Assert.False(ConsoleInteractionPolicy.Invite(own,Guid.NewGuid(),true,false));
        Assert.False(ConsoleInteractionPolicy.Invite(own,own,false,false));
        Assert.False(ConsoleInteractionPolicy.Invite(own,own,true,true));
    }
    [Theory]
    [InlineData("ELDEN RING"," ring ",true)] [InlineData("艾尔登法环","法环",true)]
    [InlineData("Game","",true)] [InlineData("Game","other",false)]
    public void SearchName(string name,string query,bool expected) => Assert.Equal(expected,ConsoleInteractionPolicy.Matches(name,query));
    [Fact] public void KeyboardEditsDraftAndNavigatesGrid()
    {
        var keyboard = new ConsoleKeyboard("old",5); keyboard.Press(); Assert.Equal("oldA",keyboard.Text);
        keyboard.Move(0,1); keyboard.Press(); Assert.Equal("oldAK",keyboard.Text);
        keyboard.Press(); Assert.Equal("oldAK",keyboard.Text);
        keyboard.Delete(); Assert.Equal("oldA",keyboard.Text);
        keyboard.Move(500,0); Assert.Equal(ConsoleKeyboard.Keys.Length-1,keyboard.Index);
        keyboard.Move(-500,0); Assert.Equal(0,keyboard.Index);
    }
}
