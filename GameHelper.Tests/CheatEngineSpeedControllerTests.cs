using System.Diagnostics;
using System.Xml.Linq;
using System.Globalization;
using System.Text;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class CheatEngineSpeedControllerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "GameHelperTests",
        Guid.NewGuid().ToString("N"));

    private ProcessStartInfo? launchInfo;

    [Theory]
    [InlineData(1.0, 1, true, 1.5)]
    [InlineData(1.0, 0, true, 1.5)]
    [InlineData(0.0, 1, true, 0.0)]
    [InlineData(1.0, 1, false, 1.0)]
    public async Task GeneratedLua_ReusedUnityHook_RefreshesActiveScaleAndPreservesPauseAsync(
        double initialScale, int method, bool unityHook, double expectedScale)
    {
        // Opt-in Lua execution against CE itself, with all target-process APIs stubbed: no game is opened.
        var executable = Environment.GetEnvironmentVariable("GAMEHELPER_TEST_CHEAT_ENGINE_EXE");
        if (string.IsNullOrWhiteSpace(executable)) return;
        Assert.True(File.Exists(executable));
        var configuration = new Mock<IGameConfiguration>();
        configuration.Setup(c => c.Read()).Returns(new AppConfig
        {
            CheatEngine = new CheatEngineSettings { Executable = executable }
        });
        var controller = new CheatEngineSpeedController(configuration.Object,
            NullLogger<CheatEngineSpeedController>.Instance, _tempDirectory, _ => { });
        Assert.True(controller.ApplySpeed(4321, 1.5));
        var generated = XDocument.Load(Path.Combine(_tempDirectory, "speed-4321.ct")).Root!.Element("LuaScript")!.Value;
        var resultPath = Path.Combine(_tempDirectory, "lua-result.txt");
        var script = $$"""
            local scale = {{initialScale.ToString("R", CultureInfo.InvariantCulture)}}
            local wanted = 1
            local mode = {{method}}
            local unity = {{unityHook.ToString().ToLowerInvariant()}}
            local sandbox = {
              openProcess = function(pid) assert(pid == 4321) end,
              getOpenedProcessHandle = function() return 100 end,
              getCheatEngineProcessID = function() return 123 end,
              duplicateHandle = function() return 101 end,
              createTimer = function() return {} end,
              speedhack_setSpeed = function(speed) wanted = speed end,
              getAddressSafe = function(name) if unity then return 1 else return nil end end,
              readInteger = function(address) return mode end,
              LaunchMonoDataCollector = function() return true end,
              mono_findClass = function(ns, name) return 1 end,
              mono_class_findMethod = function(class, name) return name end,
              mono_invoke_method = function(domain, name, object, values)
                if name == 'get_timeScale' then return scale end
                assert(name == 'set_timeScale')
                scale = mode == 1 and values[1] * wanted or values[1]
              end
            }
            setmetatable(sandbox, {__index = _G})
            local ok, err = pcall(function()
              assert(load([====[{{generated}}]====], 'generated speed table', 't', sandbox))()
              assert(math.abs(wanted - 1.5) < 0.0001, 'speed request lost')
              assert(math.abs(scale - {{expectedScale.ToString("R", CultureInfo.InvariantCulture)}}) < 0.0001,
                'Unity scale is ' .. tostring(scale))
            end)
            local f = assert(io.open([====[{{resultPath}}]====], 'w'))
            f:write(ok and 'PASS' or tostring(err))
            f:close()
            """;
        var fixture = Path.Combine(_tempDirectory, "lua-test.ct");
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("CheatTable", new XAttribute("CheatEngineTableVersion", 45),
                new XElement("CheatEntries"), new XElement("LuaScript", new XCData(script))));
        using (var writer = new StreamWriter(fixture, false, new UTF8Encoding(false))) document.Save(writer);
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            Arguments = $"\"{fixture}\"", UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)
        })!;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(resultPath) && !process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(50);
            Assert.True(File.Exists(resultPath), "Cheat Engine did not execute the isolated Lua fixture.");
            Assert.Equal("PASS", File.ReadAllText(resultPath));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedLua_GameExits_ClosesOnlyItsCheatEngineUnlessRetargetedAsync(bool retarget)
    {
        var executable = Environment.GetEnvironmentVariable("GAMEHELPER_TEST_CHEAT_ENGINE_EXE");
        if (string.IsNullOrWhiteSpace(executable)) return;
        Directory.CreateDirectory(_tempDirectory);
        using var target = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 120\"",
            UseShellExecute = false, CreateNoWindow = true
        })!;
        Process? ownedCe = null;
        Process? unrelatedCe = null;
        try
        {
            var configuration = new Mock<IGameConfiguration>();
            configuration.Setup(c => c.Read()).Returns(new AppConfig
            {
                CheatEngine = new CheatEngineSettings { Executable = executable }
            });
            var controller = new CheatEngineSpeedController(configuration.Object,
                NullLogger<CheatEngineSpeedController>.Instance, _tempDirectory, _ => { });
            Assert.True(controller.ApplySpeed(target.Id, 1.5));
            var generated = XDocument.Load(Path.Combine(_tempDirectory, $"speed-{target.Id}.ct"))
                .Root!.Element("LuaScript")!.Value;
            var readyPath = Path.Combine(_tempDirectory, "watcher-ready.txt");
            var retargetPath = Path.Combine(_tempDirectory, "retarget.txt");
            var retargetedPath = Path.Combine(_tempDirectory, "retargeted.txt");
            var fixture = Path.Combine(_tempDirectory, "exit-test.ct");
            // Only speed injection is stubbed; real CE opens the isolated target, duplicates its
            // handle, runs the real timer/Win32 wait, and closes itself. No real game is touched.
            var script = $$"""
                local sandbox = setmetatable({
                  speedhack_setSpeed = function() end,
                  getAddressSafe = function() return nil end
                }, {__index = _G})
                assert(load([====[{{generated}}]====], 'generated speed table', 't', sandbox))()
                local switchTimer = createTimer(getMainForm(), false)
                switchTimer.Interval = 50
                switchTimer.OnTimer = function(timer)
                  local request = io.open([====[{{retargetPath}}]====], 'r')
                  if not request then return end
                  request:close()
                  openProcess(getCheatEngineProcessID())
                  local ack = assert(io.open([====[{{retargetedPath}}]====], 'w'))
                  ack:write('RETARGETED'); ack:close()
                  timer.destroy()
                end
                switchTimer.Enabled = true
                local f = assert(io.open([====[{{readyPath}}]====], 'w'))
                f:write('READY'); f:close()
                """;
            var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XElement("CheatTable", new XAttribute("CheatEngineTableVersion", 45),
                    new XElement("CheatEntries"), new XElement("LuaScript", new XCData(script))));
            using (var writer = new StreamWriter(fixture, false, new UTF8Encoding(false))) document.Save(writer);
            unrelatedCe = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden
            })!;
            ownedCe = Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = $"\"{fixture}\"", UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden
            })!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (!File.Exists(readyPath) && !ownedCe.HasExited)
                await Task.Delay(50, timeout.Token);
            Assert.True(File.Exists(readyPath), "CE did not initialize the generated exit watcher.");
            await Task.Delay(750, timeout.Token);
            Assert.False(ownedCe.HasExited, "CE closed while its target was alive.");
            if (retarget)
            {
                File.WriteAllText(retargetPath, "switch");
                while (!File.Exists(retargetedPath) && !ownedCe.HasExited)
                    await Task.Delay(50, timeout.Token);
                Assert.True(File.Exists(retargetedPath), "CE did not switch to the manual target.");
            }
            target.Kill();
            await target.WaitForExitAsync(timeout.Token);
            if (retarget)
            {
                await Task.Delay(1000, timeout.Token);
                Assert.False(ownedCe.HasExited, "A manually retargeted CE instance must remain running.");
            }
            else await ownedCe.WaitForExitAsync(timeout.Token);
            Assert.False(unrelatedCe.HasExited, "An unrelated CE instance must remain running.");
        }
        finally
        {
            foreach (var process in new[] { target, ownedCe, unrelatedCe })
            {
                if (process is null) continue;
                try { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
                catch { /* Best-effort cleanup must not skip the other isolated processes. */ }
                finally { if (process != target) process.Dispose(); }
            }
        }
    }

    [Fact]
    public void ApplySpeed_GeneratesTableForPidAndLaunchesConfiguredCheatEngine()
    {
        Directory.CreateDirectory(_tempDirectory);
        var cheatEnginePath = Path.Combine(_tempDirectory, "cheatengine-x86_64.exe");
        File.WriteAllBytes(cheatEnginePath, Array.Empty<byte>());
        var tableDirectory = Path.Combine(_tempDirectory, "tables");
        var configuration = new Mock<IGameConfiguration>();
        configuration.Setup(provider => provider.Read()).Returns(new AppConfig
        {
            CheatEngine = new CheatEngineSettings { Executable = cheatEnginePath }
        });
        var controller = new CheatEngineSpeedController(
            configuration.Object,
            NullLogger<CheatEngineSpeedController>.Instance,
            tableDirectory,
            startInfo => launchInfo = startInfo);

        var launched = controller.ApplySpeed(4321, 2.5);

        Assert.True(launched);
        Assert.NotNull(launchInfo);
        Assert.Equal(cheatEnginePath, launchInfo!.FileName);
        var tablePath = launchInfo.Arguments.Trim('"');
        // CE checks the first five bytes for <?XML; a UTF-8 BOM is misread as a compressed trainer.
        Assert.Equal("<?xml", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(tablePath), 0, 5));
        Assert.Equal(Path.Combine(tableDirectory, "speed-4321.ct"), tablePath);
        var document = XDocument.Load(tablePath);
        var lua = document.Root?.Element("LuaScript")?.Value;
        Assert.Contains("local targetPid = 4321", lua);
        Assert.Contains("local targetSpeed = 2.5", lua);
        Assert.Contains("openProcess(targetPid)", lua);
        Assert.Contains("speedhack_setSpeed(targetSpeed)", lua);
    }

    [Fact]
    public void ApplySpeed_WithMissingExecutable_DoesNotLaunch()
    {
        var configuration = new Mock<IGameConfiguration>();
        configuration.Setup(provider => provider.Read()).Returns(new AppConfig());
        var launches = 0;
        var controller = new CheatEngineSpeedController(
            configuration.Object,
            NullLogger<CheatEngineSpeedController>.Instance,
            _tempDirectory,
            startInfo =>
            {
                launchInfo = startInfo;
                launches++;
            });

        var launched = controller.ApplySpeed(4321, 2);

        Assert.False(launched);
        Assert.Equal(0, launches);
        Assert.False(Directory.Exists(_tempDirectory));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
