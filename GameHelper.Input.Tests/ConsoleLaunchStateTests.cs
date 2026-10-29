using GameHelper.Desktop;
using Xunit;
namespace GameHelper.Input.Tests;
public sealed class ConsoleLaunchStateTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance() => now = now.AddMinutes(2);
    }
    [Fact] public void ExternalOrPreviousLaunchNeverMinimizesOrRestores()
    {
        var state = new ConsoleLaunchState(new Clock()); state.Begin("game", "Game");
        Assert.Equal(ConsoleLaunchState.Effect.None,state.Observe(true,null,42));
        Assert.Equal(ConsoleLaunchState.Effect.None,state.Observe(true,Guid.NewGuid(),42));
        Assert.Equal(ConsoleLaunchState.Effect.None,state.Observe(false,null,null));
        Assert.True(state.Waiting);
    }
    [Fact] public void DismissingOverlayCannotReplacePendingRequest()
    {
        var state = new ConsoleLaunchState(new Clock()); state.Begin("one", "One"); var request = state.RequestId;
        state.Back(); Assert.False(state.ShowOverlay);
        Assert.False(state.Begin("two", "Two")); Assert.Equal("one",state.Key); Assert.Equal(request,state.RequestId);
        Assert.Equal("正在等待 One 启动",state.Message);
    }
    [Fact] public void RunningImmediatelyHidesOverlayAndRestoresOnlyOnceWhenProcessChanges()
    {
        var state = new ConsoleLaunchState(new Clock()); state.Begin("game", "Game"); var request = state.RequestId;
        Assert.Equal(ConsoleLaunchState.Effect.Minimize,state.Observe(true,request,42)); Assert.False(state.ShowOverlay);
        Assert.Equal(ConsoleLaunchState.Effect.None,state.Observe(true,request,42));
        Assert.Equal(ConsoleLaunchState.Effect.Restore,state.Observe(true,Guid.NewGuid(),43));
        Assert.Equal(ConsoleLaunchState.Effect.None,state.Observe(false,null,null));
    }
    [Fact] public void TimeoutPersistsThroughRendersAndRejectsLateProcessUntilRetryOrBack()
    {
        var clock = new Clock(); var state = new ConsoleLaunchState(clock); state.Begin("game", "Game"); var request = state.RequestId;
        clock.Advance(); Assert.Equal(ConsoleLaunchState.Effect.None,state.Observe(true,request,42));
        Assert.False(state.ShowOverlay); var message = state.Message; Assert.Contains("超时",message);
        Assert.Equal(ConsoleLaunchState.Effect.None,state.Observe(true,request,42)); Assert.Equal(message,state.Message);
        Assert.Equal(ConsoleLaunchState.Effect.None,state.Observe(false,null,null)); Assert.Equal(message,state.Message);
        state.Back(); Assert.Null(state.Message);
        Assert.True(state.Begin("game", "Game")); Assert.NotEqual(request,state.RequestId);
    }
    [Fact] public void FailedRequestCannotClaimLaterExternalSession()
    {
        var state = new ConsoleLaunchState(new Clock()); state.Begin("game", "Game"); var request = state.RequestId; state.Fail();
        Assert.Equal(ConsoleLaunchState.Effect.None,state.Observe(true,request,42)); Assert.False(state.ShowOverlay);
    }
}
