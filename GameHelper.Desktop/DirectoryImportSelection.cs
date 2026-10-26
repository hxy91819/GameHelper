using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>User-owned scan choices: initially empty, with at most one executable per game directory.</summary>
internal sealed class DirectoryImportSelection : ObservableState
{
    private string _search = string.Empty;
    private bool _showAuxiliary;
    public DirectoryImportSelection(IEnumerable<LocalGameCandidate> candidates) =>
        Choices = candidates.Select(candidate => new DirectoryGameChoice(candidate, OnSelectionChanged)).ToList();
    public IReadOnlyList<DirectoryGameChoice> Choices { get; }
    public IReadOnlyList<LocalGameCandidate> SelectedGames => Choices.Where(choice => choice.Selected).Select(choice => choice.Candidate).ToList();
    public int SelectedCount => Choices.Count(choice => choice.Selected);
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
    public bool Matches(DirectoryGameChoice choice) => (!choice.Candidate.IsAuxiliary || ShowAuxiliary)
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
    private void OnSelectionChanged(DirectoryGameChoice selected)
    {
        if (selected.Selected)
            foreach (var choice in Choices.Where(choice => choice != selected
                && choice.Candidate.Folder.Equals(selected.Candidate.Folder, StringComparison.OrdinalIgnoreCase))) choice.Selected = false;
        Changed(nameof(SelectedCount)); Changed(nameof(SelectedGames));
    }
}

internal sealed class DirectoryGameChoice(LocalGameCandidate candidate, Action<DirectoryGameChoice> changed) : ObservableState
{
    private bool _selected;
    public LocalGameCandidate Candidate => candidate;
    public bool CanSelect => !candidate.AlreadyAdded;
    public string Note => candidate.AlreadyAdded ? "已在游戏库" : candidate.IsAuxiliary ? "辅助程序" : candidate.Recommended ? "推荐" : "待确认";
    public bool Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value && CanSelect)) changed(this); }
    }
}
