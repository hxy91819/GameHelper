using GameHelper.Core.Models;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class DirectoryImportSelectionTests
{
    private static LocalGameCandidate Candidate(string name, string folder, bool recommended = false, bool auxiliary = false, bool added = false) =>
        new(name, @"C:\Games\" + folder + "\\" + name + ".exe", folder, recommended, added) { IsAuxiliary = auxiliary };

    [Fact]
    public void DefaultHasNoSelectionsAndHidesAuxiliaryPrograms()
    {
        var selection = new DirectoryImportSelection([Candidate("Game", "A", true), Candidate("CRC", "A", auxiliary: true)]);
        Assert.Empty(selection.SelectedGames);
        Assert.Single(selection.Choices.Where(selection.Matches));
        selection.SelectRecommended(); Assert.Equal("Game", Assert.Single(selection.SelectedGames).Name);
    }

    [Fact]
    public void PickingAnotherExecutableInSameDirectoryReplacesOnlyThatDirectoryChoice()
    {
        var selection = new DirectoryImportSelection([Candidate("Main", "A", true), Candidate("Alternate", "A"), Candidate("Other", "B")]);
        selection.Choices[0].Selected = true; selection.Choices[2].Selected = true;
        selection.Choices[1].Selected = true;
        Assert.False(selection.Choices[0].Selected); Assert.True(selection.Choices[2].Selected);
        Assert.Equal(2, selection.SelectedCount);
    }

    [Fact]
    public void HidingAuxiliaryClearsHiddenChoiceAndRecommendationsRespectSearchAndExistingEntries()
    {
        var selection = new DirectoryImportSelection([Candidate("Main", "A", true), Candidate("CRC", "A", auxiliary: true),
            Candidate("Other", "B", true), Candidate("Existing", "C", true, added: true)]);
        selection.ShowAuxiliary = true; selection.Choices[1].Selected = true;
        selection.ShowAuxiliary = false; Assert.Empty(selection.SelectedGames);
        selection.Search = "Other"; selection.SelectRecommended();
        Assert.Equal("Other", Assert.Single(selection.SelectedGames).Name);
        selection.Choices[3].Selected = true; Assert.False(selection.Choices[3].Selected);
        selection.Clear(); Assert.Empty(selection.SelectedGames);
    }
}
