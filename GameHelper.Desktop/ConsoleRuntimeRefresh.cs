namespace GameHelper.Desktop;

/// <summary>Refreshes the console's live library on every tick, independent of the desktop page or launch tracking.</summary>
internal sealed class ConsoleRuntimeRefresh(Func<Task> refresh, Func<Task> render)
{
    private bool busy;
    private bool stopped;
    internal async Task TickAsync()
    {
        if (busy || stopped) return;
        busy = true;
        try
        {
            await refresh();
            if (!stopped) await render();
        }
        finally { busy = false; }
    }
    internal void Stop() => stopped = true;
}
