using GameHelper.Desktop.Input;

namespace GameHelper.Tests;

public sealed class TrayControllerWakeTests
{
    private const ControllerAction Chord = ControllerAction.ModeModifier | ControllerAction.Menu;

    [Fact]
    public void TrayAcceptsOnlyChordAndRequiresReleaseBeforeRepeat()
    {
        var source = new Source();
        var wakes = 0;
        using var listener = new TrayControllerWake(() => source, () => wakes++);
        listener.Poll(false);
        source.Buttons = ControllerAction.Confirm;
        listener.Poll(false);
        Assert.Equal(0, wakes);
        source.Buttons = Chord;
        listener.Poll(false); listener.Poll(false);
        Assert.Equal(1, wakes);
        source.Buttons = ControllerAction.Menu;
        listener.Poll(false);
        source.Buttons = Chord;
        listener.Poll(false);
        Assert.Equal(1, wakes);
        source.Buttons = 0; listener.Poll(false);
        source.Buttons = Chord; listener.Poll(false);
        Assert.Equal(2, wakes);
    }

    [Fact]
    public void LibraryPresenceReleasesDevicesAndReopeningDoesNotReplayHeldChord()
    {
        var sources = new List<Source>();
        var wakes = 0;
        using var listener = new TrayControllerWake(() =>
        {
            var source = new Source { Buttons = Chord };
            sources.Add(source); return source;
        }, () => wakes++);
        listener.Poll(true);
        Assert.Empty(sources);
        listener.Poll(false);
        Assert.Equal(0, wakes);
        listener.Poll(true);
        Assert.True(sources[0].Disposed);
        listener.Poll(false);
        Assert.Equal(2, sources.Count);
        Assert.Equal(0, wakes);
        sources[1].Buttons = 0; listener.Poll(false);
        sources[1].Buttons = Chord; listener.Poll(false);
        Assert.Equal(1, wakes);
        listener.Dispose();
        Assert.All(sources, source => Assert.True(source.Disposed));
    }

    private sealed class Source : IControllerSource
    {
        internal ControllerAction Buttons;
        internal bool Disposed;
        public IReadOnlyList<ControllerState> Read() => [new(1, ControllerKind.Xbox, Buttons)];
        public void Dispose() => Disposed = true;
    }
}
