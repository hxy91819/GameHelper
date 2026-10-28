using GameHelper.Core.Models;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class LibraryGameItemSpeedValueTests
{
    [Fact]
    public void Getter_ReadsStoredSpeedWithoutWriting()
    {
        var item = NewItem(1.5);
        Assert.Equal(1.5, item.SpeedValue);
        Assert.False(item.IsDirty);
    }

    [Fact]
    public void Getter_ClampsBeyondSliderRangeButKeepsStoredText()
    {
        var fast = NewItem(20);
        Assert.Equal(LibraryGameItem.MaxSpeedValue, fast.SpeedValue);
        Assert.Equal("20", fast.Speed);
        Assert.False(fast.IsDirty);

        var slow = NewItem(0.02);
        Assert.Equal(LibraryGameItem.MinSpeedValue, slow.SpeedValue);
        Assert.Equal("0.02", slow.Speed);
        Assert.False(slow.IsDirty);
    }

    [Fact]
    public void Getter_FallsBackToDefaultWhenUnparsable()
    {
        var item = NewItem();
        item.Speed = "unfinished input";
        Assert.Equal(1.5, item.SpeedValue);
        Assert.Equal("unfinished input", item.Speed);
    }

    [Fact]
    public void Setter_RoundsToTenthStepAndMarksDirty()
    {
        var item = NewItem(1.5);
        item.SpeedValue = 3.444;
        Assert.Equal("3.4", item.Speed);
        Assert.Equal(3.4, item.SpeedValue);
        Assert.True(item.IsDirty);
    }

    [Fact]
    public void Setter_ClampsOutOfRangeValuesIntoSliderRange()
    {
        var item = NewItem();
        item.SpeedValue = 55;
        Assert.Equal("10", item.Speed);

        var slower = NewItem();
        slower.SpeedValue = -3;
        Assert.Equal("0.1", slower.Speed);
    }

    [Fact]
    public void Setter_SameValueLeavesDraftClean()
    {
        var item = NewItem(1.5);
        item.SpeedValue = 1.5;
        Assert.False(item.IsDirty);
        Assert.Equal("1.5", item.Speed);
    }

    [Fact]
    public void Setter_SnapsHandTunedMidDecimalValuesToTenthGrid()
    {
        var item = NewItem(1.55);
        item.SpeedValue = 1.55;
        Assert.Equal("1.6", item.Speed);
        Assert.True(item.IsDirty);
    }

    [Fact]
    public void SpeedValue_LeadsToRoundedConfigOnSave()
    {
        var item = NewItem();
        item.UseSpeed = true;
        item.SpeedValue = 2.7;
        Assert.Equal(2.7, item.ToConfig().SpeedMultiplier);
    }

    private static LibraryGameItem NewItem(double? speed = null) => new(new LibraryGame
    {
        Config = new() { DataKey = "game", Executable = "game.exe", SpeedMultiplier = speed },
        Revision = "r1"
    });
}