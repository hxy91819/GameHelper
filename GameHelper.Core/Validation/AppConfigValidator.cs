using GameHelper.Core.Models;

namespace GameHelper.Core.Validation;

public sealed class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public int GameCount { get; set; }
    public int DuplicateCount { get; set; }
}

/// <summary>Shared domain validation for configuration transactions and file validation.</summary>
public static class AppConfigValidator
{
    /// <summary>Validates detached application settings before the configuration transaction commits.</summary>
    public static ValidationResult ValidateGlobals(AppConfig config)
    {
        var result = new ValidationResult();
        void Check(Action action) { try { action(); } catch (ArgumentException ex) { result.Errors.Add(ex.Message); } }
        if (!Enum.IsDefined(config.ProcessMonitorType)) result.Errors.Add("monitor must be ETW or WMI.");
        Check(() => config.Report?.Validate());
        Check(() => config.Hdr?.Validate());
        Check(() => config.BreakReminder?.Validate());
        Check(() => config.MonitorMode?.Validate());
        if (config.SyncSettings is { } sync)
        {
            var error = sync.Validate(allowUnconfigured: !sync.Enabled);
            if (error is not null) result.Errors.Add(error);
        }
        if (config.CheatEngine?.Executable is { Length: > 0 } path &&
            (!(path.Length > 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] is '\\' or '/')) && !path.StartsWith(@"\\") ||
             !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            result.Errors.Add("cheatEngine.executable must be an absolute Windows .exe path.");
        try { GameHelper.Core.Models.SpeedHotkey.Validate(config.CheatEngine?.SpeedHotkeys); }
        catch (ArgumentException ex) { result.Errors.Add("cheatEngine.speedHotkeys: " + ex.Message); }
        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    /// <summary>Validates the complete file, including game entries.</summary>
    public static ValidationResult Validate(AppConfig config)
    {
        var result = ValidateGlobals(config);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in config.Games ?? [])
        {
            result.GameCount++;
            result.Errors.AddRange(ValidateGame(game));
            if (!keys.Add(game.DataKey?.Trim() ?? string.Empty))
            {
                result.DuplicateCount++;
                result.Errors.Add($"Duplicate dataKey: '{game.DataKey}'.");
            }
        }
        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    /// <summary>Checks a game without repairing or discarding stored values.</summary>
    public static IReadOnlyList<string> ValidateGame(GameConfig game)
    {
        var errors = new List<string>(ValidateGameIdentity(game));
        if (!IsValidSpeed(game.SpeedMultiplier)) errors.Add("Speed must be a finite number greater than zero.");
        if (!IsValidSpeedAttach(game.SpeedAttach)) errors.Add("speedAttach must be auto or immediate.");
        try { SpeedHotkey.Validate(game.SpeedHotkeys); }
        catch (ArgumentException ex) { errors.Add("speedHotkeys: " + ex.Message); }
        foreach (var (action, index) in (game.Actions ?? []).Select((action, index) => (action, index)))
        {
            try
            {
                if (action is null) throw new ArgumentException("action is required.");
                action.Validate();
            }
            catch (ArgumentException ex) { errors.Add($"actions[{index + 1}]: {ex.Message}"); }
        }
        // Launch targets may refer to an unmounted Windows drive; validate stored shape without probing installation.
        if (game.Launch is { } launch && string.IsNullOrWhiteSpace(launch.Target)) errors.Add("launch.target is required.");
        return errors.Select(error => $"Game '{game.DataKey}': {error}").ToList();
    }

    /// <summary>Identity checks shared with the legacy load-time skip policy.</summary>
    public static IReadOnlyList<string> ValidateGameIdentity(GameConfig game)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(game.DataKey)) errors.Add("DataKey is required.");
        if (!ExecutableIdentity.TryCreate(game.Executable, out _)) errors.Add("Executable identity is required and must be valid.");
        return errors;
    }

    public static bool IsValidSpeed(double? speed) => speed is null || double.IsFinite(speed.Value) && speed > 0;

    public static bool IsValidSpeedAttach(string? policy) => string.IsNullOrWhiteSpace(policy) ||
        policy.Trim().ToLowerInvariant() is GameConfig.SpeedAttachPolicyAuto or GameConfig.SpeedAttachPolicyImmediate;
}
