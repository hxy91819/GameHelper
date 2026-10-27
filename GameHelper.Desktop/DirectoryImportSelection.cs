using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>
/// User-owned scan choices: initially empty, with at most one executable per game directory.
/// Directories already in the library and directories the user ignored are collapsed by default,
/// so a rescan of the same library surfaces only new games.
/// </summary>
internal sealed class DirectoryImportSelection : ObservableState
{
    private string _search = string.Empty;
    private bool _showAuxiliary;
    private bool _showExisting;
    private bool _showIgnored;
    public DirectoryImportSelection(IEnumerable<LocalGameCandidate> candidates, IEnumerable<string>? ignoredPaths = null)
    {
        var list = candidates.ToList();
        var ignored = new HashSet<string>(ignoredPaths ?? [], StringComparer.OrdinalIgnoreCase);
        var existingFolders = new HashSet<string>(list.Where(candidate => candidate.AlreadyAdded).Select(candidate => candidate.Folder), StringComparer.OrdinalIgnoreCase);
        Choices = list.Select(candidate => new DirectoryGameChoice(candidate, OnSelectionChanged,
            existingFolders.Contains(candidate.Folder), ignored.Contains(candidate.ExecutablePath))).ToList();
    }
    /// <summary>Raised with every executable path whose ignored state changed, for persistence.</summary>
    public event Action<IReadOnlyList<string>, bool>? IgnoredChanged;
    public IReadOnlyList<DirectoryGameChoice> Choices { get; }
    public IReadOnlyList<LocalGameCandidate> SelectedGames => Choices.Where(choice => choice.Selected).Select(choice => choice.Candidate).ToList();
    public int SelectedCount => Choices.Count(choice => choice.Selected);
    public int NewFolderCount => Folders(choice => !choice.FolderInLibrary && !choice.FolderIgnored);
    public int ExistingFolderCount => Folders(choice => choice.FolderInLibrary);
    public int IgnoredFolderCount => Folders(choice => !choice.FolderInLibrary && choice.FolderIgnored);
    public string Search { get => _search; set => Set(ref _search, value); }
    public bool ShowAuxiliary
    {
        get => _showAuxiliary;
        set
        {
            if (!Set(ref _showAuxiliary, value)) return;
            if (!value) foreach (var choice in Choices.Where(choice => choice.Candidate.IsAuxiliary)) choice.Selected = false;
        }
    }
    public bool ShowExisting { get => _showExisting; set => Set(ref _showExisting, value); }
    public bool ShowIgnored { get => _showIgnored; set => Set(ref _showIgnored, value); }
    public bool Matches(DirectoryGameChoice choice) => (!choice.Candidate.IsAuxiliary || ShowAuxiliary)
        && (!choice.FolderInLibrary || ShowExisting)
        && (!choice.FolderIgnored || ShowIgnored)
        && (choice.Candidate.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)
            || choice.Candidate.ExecutablePath.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)
            || choice.Candidate.Folder.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
    public void Clear() { foreach (var choice in Choices) choice.Selected = false; }
    public void SelectRecommended()
    {
        Clear();
        foreach (var choice in Choices.Where(choice => Matches(choice) && choice.Candidate.Recommended && !choice.Candidate.IsAuxiliary))
            choice.Selected = true;
    }
    /// <summary>Ignore or restore a whole game directory; library directories cannot be ignored.</summary>
    public void SetFolderIgnored(string folder, bool ignored)
    {
        var changed = Choices.Where(choice => !choice.FolderInLibrary && choice.FolderIgnored != ignored
            && choice.Candidate.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (changed.Count == 0) return;
        foreach (var choice in Choices.Where(choice => choice.Candidate.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)))
            choice.FolderIgnored = ignored;
        IgnoredChanged?.Invoke(changed.Select(choice => choice.Candidate.ExecutablePath).ToList(), ignored);
        Changed(nameof(NewFolderCount)); Changed(nameof(IgnoredFolderCount)); Changed(nameof(SelectedCount)); Changed(nameof(SelectedGames));
    }
    private int Folders(Func<DirectoryGameChoice, bool> predicate) =>
        Choices.Where(predicate).Select(choice => choice.Candidate.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    private void OnSelectionChanged(DirectoryGameChoice selected)
    {
        if (selected.Selected)
            foreach (var choice in Choices.Where(choice => choice != selected
                && choice.Candidate.Folder.Equals(selected.Candidate.Folder, StringComparison.OrdinalIgnoreCase))) choice.Selected = false;
        Changed(nameof(SelectedCount)); Changed(nameof(SelectedGames));
    }
}

internal sealed class DirectoryGameChoice(LocalGameCandidate candidate, Action<DirectoryGameChoice> changed, bool folderInLibrary, bool folderIgnored) : ObservableState
{
    private bool _selected;
    private bool _folderIgnored = folderIgnored && !folderInLibrary;
    public LocalGameCandidate Candidate => candidate;
    /// <summary>Another program of this game directory is already in the library.</summary>
    public bool FolderInLibrary => folderInLibrary;
    public bool FolderIgnored
    {
        get => _folderIgnored;
        internal set
        {
            if (!Set(ref _folderIgnored, value)) return;
            if (value) Selected = false;
            Changed(nameof(CanSelect)); Changed(nameof(Note)); Changed(nameof(IgnoreLabel));
        }
    }
    public bool CanSelect => !candidate.AlreadyAdded && !FolderIgnored;
    public bool CanIgnore => !folderInLibrary;
    public string IgnoreLabel => FolderIgnored ? "取消忽略" : "忽略此目录";
    public string Note => candidate.AlreadyAdded ? "已在游戏库" : FolderIgnored ? "已忽略" : candidate.IsAuxiliary ? "辅助程序" : candidate.Recommended ? "推荐" : "待确认";
    public bool Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value && CanSelect)) changed(this); }
    }
}
