namespace GameHelper.Core.Models;

public enum GameInstallationState { Unknown, Installed, Missing }

/// <summary>Read-only discovery evidence. Missing is used only when a known local executable is absent.</summary>
public sealed record GameInstallation(GameInstallationState State, string Description);

/// <summary>A read-only candidate; recommendation evidence never authorizes an import.</summary>
public sealed record LocalGameCandidate(string Name, string ExecutablePath, string Folder, bool Recommended, bool AlreadyAdded)
{
    public string Evidence { get; init; } = string.Empty;
    public bool IsAuxiliary { get; init; }
}

public sealed record LocalGameScan(string Root, IReadOnlyList<LocalGameCandidate> Candidates, int SkippedDirectories, bool Truncated);
