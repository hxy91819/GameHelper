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

    public void ApplySpeed(int processId, double speedMultiplier)
    {
        if (processId <= 0)
        {
            _logger.LogWarning("Cannot apply game speed: invalid process ID {ProcessId}", processId);
            return;
        }

        if (!double.IsFinite(speedMultiplier) || speedMultiplier <= 0)
        {
            _logger.LogWarning("Cannot apply game speed: invalid multiplier {Speed}", speedMultiplier);
            return;
        }

        var cheatEnginePath = _configuration.Read().CheatEngine?.Executable?.Trim();
        if (string.IsNullOrWhiteSpace(cheatEnginePath))
        {
            _logger.LogWarning(
                "Game speed {Speed} is configured, but cheatEngine.executable is missing",
                speedMultiplier);
            return;
        }

        if (!Path.IsPathFullyQualified(cheatEnginePath) || !File.Exists(cheatEnginePath))
        {
            _logger.LogWarning("Cheat Engine executable was not found: {Path}", cheatEnginePath);
            return;
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
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to request game speed {Speed} for PID {ProcessId} through Cheat Engine",
                speedMultiplier,
                processId);
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
