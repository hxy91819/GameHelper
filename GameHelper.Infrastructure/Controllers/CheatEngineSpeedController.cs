using System.Diagnostics;
using System.Globalization;
using System.Text;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Controllers;

/// <summary>
/// Applies speed settings by generating a short-lived Cheat Engine table and opening it with the
/// user's Cheat Engine installation. No per-game .CT file is required.
/// </summary>
public sealed class CheatEngineSpeedController : IGameSpeedController
{
    private readonly IGameConfiguration _configuration;
    private readonly ILogger<CheatEngineSpeedController> _logger;
    private readonly string _tableDirectory;
    private readonly Action<ProcessStartInfo> _launch;

    public CheatEngineSpeedController(
        IGameConfiguration configuration,
        ILogger<CheatEngineSpeedController> logger)
        : this(configuration, logger, AppDataPath.GetCheatEngineTableDirectory(), LaunchProcess)
    {
    }

    internal CheatEngineSpeedController(
        IGameConfiguration configuration,
        ILogger<CheatEngineSpeedController> logger,
        string tableDirectory,
        Action<ProcessStartInfo> launch)
    {
        _configuration = configuration;
        _logger = logger;
        _tableDirectory = tableDirectory;
        _launch = launch;
    }

    public bool ApplySpeed(int processId, double speedMultiplier)
    {
        if (processId <= 0)
        {
            _logger.LogWarning("Cannot apply game speed: invalid process ID {ProcessId}", processId);
            return false;
        }

        if (!double.IsFinite(speedMultiplier) || speedMultiplier <= 0)
        {
            _logger.LogWarning("Cannot apply game speed: invalid multiplier {Speed}", speedMultiplier);
            return false;
        }

        var cheatEnginePath = _configuration.Read().CheatEngine?.Executable?.Trim();
        if (string.IsNullOrWhiteSpace(cheatEnginePath))
        {
            _logger.LogWarning(
                "Game speed {Speed} is configured, but cheatEngine.executable is missing",
                speedMultiplier);
            return false;
        }

        if (!Path.IsPathFullyQualified(cheatEnginePath) || !File.Exists(cheatEnginePath))
        {
            _logger.LogWarning("Cheat Engine executable was not found: {Path}", cheatEnginePath);
            return false;
        }

        try
        {
            Directory.CreateDirectory(_tableDirectory);
            DeleteExpiredTables();

            var tablePath = Path.Combine(_tableDirectory, $"speed-{processId}.ct");
            WriteTableAtomically(tablePath, processId, speedMultiplier);

            _launch(new ProcessStartInfo
            {
                FileName = cheatEnginePath,
                Arguments = $"\"{tablePath}\"",
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(cheatEnginePath) ?? string.Empty
            });

            _logger.LogInformation(
                "Requested game speed {Speed} for PID {ProcessId} through Cheat Engine",
                speedMultiplier,
                processId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to request game speed {Speed} for PID {ProcessId} through Cheat Engine",
                speedMultiplier,
                processId);
            return false;
        }
    }

    private static void LaunchProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Cheat Engine did not start.");
    }

    private static void WriteTableAtomically(string path, int processId, double speedMultiplier)
    {
        var speed = speedMultiplier.ToString("R", CultureInfo.InvariantCulture);
        var xml = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <CheatTable CheatEngineTableVersion="45">
              <CheatEntries />
              <UserdefinedSymbols />
              <LuaScript><![CDATA[
            local targetPid = {{processId}}
            local targetSpeed = {{speed}}
            openProcess(targetPid)
            speedhack_setSpeed(targetSpeed)
            -- SpeedhackV3 reuses a game's existing Unity hook without restoring this CE
            -- instance's setter. Its requested speed changes, but the live timeScale can stay 1.
            if getAddressSafe('set_timeScaleEntryHook') ~= nil
                and UnityEngineTimeSetTimeScaleMethod == nil then
              assert(LaunchMonoDataCollector(), 'GameHelper: cannot reconnect to the Unity speed hook')
              local timeClass = mono_findClass('UnityEngine', 'Time')
              assert(timeClass and timeClass ~= 0, 'GameHelper: Unity Time class unavailable')
              local getter = mono_class_findMethod(timeClass, 'get_timeScale')
              local setter = mono_class_findMethod(timeClass, 'set_timeScale')
              assert(getter and getter ~= 0 and setter and setter ~= 0,
                'GameHelper: Unity timeScale methods unavailable')
              local currentScale = mono_invoke_method(nil, getter, nil, {})
              assert(type(currentScale) == 'number', 'GameHelper: Unity timeScale is unreadable')
              if currentScale > 0 then
                local mode = readInteger('speedhack_unityspeedhackmethod')
                assert(mode == 0 or mode == 1, 'GameHelper: unknown Unity speed hook mode')
                -- Multiplicative hooks already multiply by targetSpeed; passing targetSpeed
                -- again would produce 2.25 at 1.5x. Zero remains paused until the game resumes.
                mono_invoke_method(nil, setter, nil, {mode == 1 and 1 or targetSpeed})
              end
            end
            ]]></LuaScript>
            </CheatTable>
            """;

        var tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private void DeleteExpiredTables()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var path in Directory.EnumerateFiles(_tableDirectory, "speed-*.ct", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not remove expired generated Cheat Engine table {Path}", path);
            }
        }
    }
}
