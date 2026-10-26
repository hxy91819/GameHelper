using System.Xml.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using MoonSharp.Interpreter;
using Moq;

namespace GameHelper.Tests;

public sealed class CheatEngineSpeedHotkeyLuaTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void GeneratedLua_SwitchesPresetsAndToggleRepeatedlyWithoutSquaringOrResumingPause(int mode, bool paused)
    {
        var lua = Generate();
        var script = Sandbox();
        script.DoString($"mode = {mode}; scale = {(paused ? 0 : 1)}");
        script.DoString(lua);
        script.DoString($$"""
            assert(wanted == 2)
            assert(scale == {{(paused ? 0 : 2)}})
            assert(#registered == 3)
            assert(registered[1].keys[1] == 17 and registered[1].keys[2] == 112)
            registered[1].callback(); assert(wanted == 3); assert(scale == {{(paused ? 0 : 3)}})
            registered[2].callback(); assert(wanted == 1); assert(scale == {{(paused ? 0 : 1)}})
            registered[3].callback(); assert(wanted == 2); assert(scale == {{(paused ? 0 : 2)}})
            registered[3].callback(); assert(wanted == 1)
            registered[1].callback(); registered[3].callback(); assert(wanted == 1)
            """);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("retarget")]
    [InlineData("handle")]
    [InlineData("wait-failure")]
    public void GeneratedLua_StopsAndDestroysEveryHotkeyEvenWhenOneDestroyFails(string reason)
    {
        var script = Sandbox();
        script.DoString(Generate());
        script.DoString("registered[1].destroy = function() destroyed = destroyed + 1; error('destroy failed') end");
        script.DoString(reason switch
        {
            "exit" => "waitState = 0",
            "retarget" => "pid = 999",
            "handle" => "handle = 222",
            _ => "waitState = 123"
        });
        script.DoString("timer.OnTimer(); assert(destroyed == 3); assert(timerDestroyed == 1); assert(handlesClosed == 1)");
        Assert.Equal(reason == "exit", script.Globals.Get("closed").Boolean);
        // A queued callback after teardown cannot change any target's speed or close handles twice.
        script.DoString("registered[1].callback(); assert(wanted == 2); assert(destroyed == 3); assert(handlesClosed == 1)");
    }

    [Fact]
    public void GeneratedLua_RegistrationFailureCleansAlreadyRegisteredKeysAndWatcher()
    {
        var script = Sandbox();
        script.DoString("failRegistration = 2");
        Assert.Throws<ScriptRuntimeException>(() => script.DoString(Generate()));
        script.DoString("assert(destroyed == 1); assert(timerDestroyed == 1); assert(handlesClosed == 1)");
    }

    [Fact]
    public void GeneratedLua_NoWatchHandleAppliesLegacySpeedWithoutRegisteringUnownedKeys()
    {
        var script = Sandbox();
        script.DoString("duplicateHandle = function() return 0 end");
        script.DoString(Generate());
        script.DoString("assert(wanted == 2); assert(#registered == 0)");
    }

    [Fact]
    public void GeneratedLua_CallbackAfterRetargetDoesNotWaitForTimerToStopKeys()
    {
        var script = Sandbox();
        script.DoString(Generate());
        script.DoString("pid = 999; registered[1].callback(); assert(wanted == 2); assert(destroyed == 3)");
    }

    [Fact]
    public void GeneratedLua_OrdinaryClockHookSwitchesWithoutRequestingUnityMetadata()
    {
        var script = Sandbox();
        script.DoString("getAddressSafe = function() return nil end; LaunchMonoDataCollector = function() error('not Unity') end");
        script.DoString(Generate());
        script.DoString("registered[1].callback(); assert(wanted == 3); registered[2].callback(); assert(wanted == 1)");
    }

    [Fact]
    public void GeneratedLua_HotkeyRacingGameExitStillClosesOwnedCe()
    {
        var script = Sandbox();
        script.DoString(Generate());
        script.DoString("waitState = 0; registered[1].callback(); assert(closed); assert(destroyed == 3)");
    }

    private static Script Sandbox()
    {
        var script = new Script(CoreModules.Preset_SoftSandbox);
        script.DoString("""
            pid = 4321; handle = 100; mode = 1; scale = 1; wanted = 1
            waitState = 258; registered = {}; destroyed = 0; timerDestroyed = 0; handlesClosed = 0; closed = false
            function openProcess(value) assert(value == pid) end
            function getOpenedProcessID() return pid end
            function getOpenedProcessHandle() return handle end
            function getCheatEngineProcessID() return 123 end
            function duplicateHandle() return 101 end
            function getMainForm() return {} end
            function createTimer()
              timer = {destroy = function() timerDestroyed = timerDestroyed + 1 end}
              return timer
            end
            function executeCodeLocalEx(name)
              if name == 'kernel32.CloseHandle' then handlesClosed = handlesClosed + 1; return end
              assert(name == 'kernel32.WaitForSingleObject'); return waitState
            end
            function closeCE() closed = true end
            function speedhack_setSpeed(value) wanted = value end
            function getAddressSafe() return 1 end
            function LaunchMonoDataCollector() return true end
            function mono_findClass() return 1 end
            function mono_class_findMethod(class, name) return name end
            function readInteger() return mode end
            function mono_invoke_method(domain, name, object, values)
              if name == 'get_timeScale' then return scale end
              scale = mode == 1 and values[1] * wanted or values[1]
            end
            function createHotkey(callback, ...)
              if failRegistration == #registered + 1 then error('registration failed') end
              local key = {callback = callback, keys = {...}, destroy = function() destroyed = destroyed + 1 end}
              registered[#registered + 1] = key
              return key
            end
            """);
        return script;
    }

    private static string Generate()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-ce-lua-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "ce.exe");
            File.WriteAllText(executable, string.Empty);
            var configuration = new Mock<IGameConfiguration>();
            configuration.Setup(c => c.Read()).Returns(new AppConfig { CheatEngine = new() { Executable = executable } });
            string? table = null;
            var controller = new CheatEngineSpeedController(configuration.Object,
                NullLogger<CheatEngineSpeedController>.Instance, root, start => table = start.Arguments.Trim('"'));
            Assert.True(controller.ApplySpeed(4321, 2, [new() { Hotkey = "Ctrl+F1", Speed = 3 },
                new() { Hotkey = "F2", Speed = 1 }, new() { Hotkey = "F3" }]));
            return XDocument.Load(table!).Root!.Element("LuaScript")!.Value;
        }
        finally { Directory.Delete(root, true); }
    }
}
