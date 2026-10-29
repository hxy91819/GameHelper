using GameHelper.Desktop;
using Xunit;
namespace GameHelper.Input.Tests;

public sealed class ConsoleRuntimeRefreshTests
{
    [Fact] public async Task SlowRefreshDoesNotOverlapAndStoppedWindowDoesNotRenderLateResponse()
    {
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0, renders = 0;
        var refresh = new ConsoleRuntimeRefresh(async () => { requests++; await response.Task; },
            () => { renders++; return Task.CompletedTask; });
        var pending = refresh.TickAsync(); await refresh.TickAsync(); Assert.Equal(1,requests);
        refresh.Stop(); response.SetResult(); await pending;
        await refresh.TickAsync(); Assert.Equal(1,requests); Assert.Equal(0,renders);
    }
}
