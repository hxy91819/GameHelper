namespace GameHelper.Desktop.Input;

/// <summary>Listens only while the library is absent; never forwards ordinary input.</summary>
internal sealed class TrayControllerWake(Func<IControllerSource> createSource, Action wake) : IDisposable
{
    private IControllerSource? source;
    private ControllerNavigation? navigation;

    internal void Poll(bool libraryOpen)
    {
        if (libraryOpen) { Dispose(); return; }
        if (source is null)
        {
            source = createSource();
            navigation = new(source, TimeProvider.System);
        }
        if (navigation!.Poll(false, globalModeChord: true).Contains(ControllerAction.ToggleConsoleMode)) wake();
    }

    public void Dispose()
    {
        var old = source;
        source = null; navigation = null;
        old?.Dispose();
    }
}
