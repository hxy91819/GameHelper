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

    [Fact]
    public void LibraryAndIgnoredDirectoriesAreCollapsedSoRescansShowOnlyNewGames()
    {
        var selection = new DirectoryImportSelection([Candidate("Owned", "A", true, added: true), Candidate("Launcher", "A"),
            Candidate("Tool", "B", true), Candidate("New", "C", true)], [@"C:\Games\B\Tool.exe"]);
        Assert.Equal(new[] { "New" }, selection.Choices.Where(selection.Matches).Select(choice => choice.Candidate.Name));
        Assert.Equal((1, 1, 1), (selection.NewFolderCount, selection.ExistingFolderCount, selection.IgnoredFolderCount));
        selection.SelectRecommended();
        Assert.Equal("New", Assert.Single(selection.SelectedGames).Name);

        selection.ShowExisting = true; selection.ShowIgnored = true;
        Assert.Equal(4, selection.Choices.Count(selection.Matches));
        selection.Choices[2].Selected = true;
        Assert.False(selection.Choices[2].Selected);
        Assert.False(selection.Choices[1].CanIgnore);
    }

    [Fact]
    public void IgnoringADirectoryClearsItsSelectionAndReportsEveryProgramForPersistence()
    {
        var selection = new DirectoryImportSelection([Candidate("Main", "A", true), Candidate("Alt", "A"), Candidate("Other", "B")]);
        var reported = new List<(IReadOnlyList<string> Paths, bool Ignored)>();
        selection.IgnoredChanged += (paths, ignored) => reported.Add((paths, ignored));
        selection.Choices[0].Selected = true;

        selection.SetFolderIgnored("A", true);
        Assert.Empty(selection.SelectedGames);
        Assert.Equal(new[] { "Other" }, selection.Choices.Where(selection.Matches).Select(choice => choice.Candidate.Name));
        Assert.Equal(new[] { @"C:\Games\A\Main.exe", @"C:\Games\A\Alt.exe" }, Assert.Single(reported).Paths);

        selection.SetFolderIgnored("A", false);
        Assert.False(reported[1].Ignored);
        Assert.Equal(3, selection.Choices.Count(selection.Matches));
        selection.Choices[1].Selected = true;
        Assert.Equal("Alt", Assert.Single(selection.SelectedGames).Name);
    }

    [Fact]
    public void ScanStoreRemembersRecentRootsAndIgnoredProgramsAcrossRestarts()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-scan-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = Path.Combine(root, "config.yml");
            var store = new LibraryScanStore(config);
            for (var i = 0; i < LibraryScanStore.MaxDirectories + 1; i++) store.Remember($@"D:\Lib{i}\");
            store.Remember(@"D:\Lib3");
            store.SetIgnored([@"D:\Lib3\Tool\Tool.exe", @"D:\Lib3\Crc\Crc.exe"], true);
            store.SetIgnored([@"D:\Lib3\Crc\Crc.exe"], false);

            var restored = new LibraryScanStore(config);
            Assert.Equal(LibraryScanStore.MaxDirectories, restored.Directories.Count);
            Assert.Equal(new[] { @"D:\Lib3", @"D:\Lib6", @"D:\Lib5" }, restored.Directories.Take(3));
            Assert.Equal(@"D:\Lib3\Tool\Tool.exe", Assert.Single(restored.Ignored));
            restored.Forget(@"D:\Lib3\");
            Assert.DoesNotContain(@"D:\Lib3", new LibraryScanStore(config).Directories);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
