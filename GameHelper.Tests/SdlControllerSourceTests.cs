using GameHelper.Desktop.Input;
using SDL3;

namespace GameHelper.Tests;

[CollectionDefinition("SDL process settings", DisableParallelization = true)]
public sealed class SdlProcessSettingsCollection { }

[Collection("SDL process settings")]
public sealed class SdlControllerSourceTests
{
    [Fact]
    public void InitializationOverridesInheritedEnhancedReportsSetting()
    {
        const string hint = "SDL_JOYSTICK_ENHANCED_REPORTS";
        var inherited = Environment.GetEnvironmentVariable(hint);
        var sdlEnvironment = SDL.GetEnvironment();
        var sdlInherited = SDL.GetEnvironmentVariable(sdlEnvironment, hint);
        var background = SDL.GetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS");
        try
        {
            SDL.ResetHint(hint);
            Environment.SetEnvironmentVariable(hint, "1");
            // SDL maintains its own environment snapshot; keep it aligned with the simulated inheritance.
            Assert.True(SDL.SetEnvironmentVariable(sdlEnvironment, hint, "1", true));
            Assert.Equal("1", SDL.GetHint(hint));
            // Demonstrate that normal priority cannot override this process environment.
            Assert.False(SDL.SetHint(hint, "0"));
            using var source = new SdlControllerSource();
            Assert.Equal("0", SDL.GetHint(hint));
        }
        finally
        {
            Environment.SetEnvironmentVariable(hint, inherited);
            if (sdlInherited is null) SDL.UnsetEnvironmentVariable(sdlEnvironment, hint);
            else SDL.SetEnvironmentVariable(sdlEnvironment, hint, sdlInherited, true);
            SDL.ResetHint(hint);
            if (background is null) SDL.ResetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS");
            else SDL.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", background);
        }
    }
}
