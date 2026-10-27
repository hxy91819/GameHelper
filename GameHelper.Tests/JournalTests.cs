using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class JournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gh-journal-" + Guid.NewGuid().ToString("N"));
    private string Config => Path.Combine(_root, "config.yml");
    public JournalTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Store_RoundTripsByStableKeyWithoutChangingConfiguration()
    {
        File.WriteAllText(Config, "original");
        var record = new JournalRecord("已通关", new DateOnly(2032, 2, 29), "旅程结束");
        var store = new LibraryJournalStore(Config);
        store.Save("game", record);
        store.Save("another", new("搁置", null, "稍后再玩"));
        var reopened = new LibraryJournalStore(Config);
        Assert.Equal(record, reopened.Read("GAME"));
        Assert.Equal("搁置", reopened.Read("another").Status);
        Assert.Equal(new JournalRecord(), reopened.Read("missing"));
        Assert.Equal("original", File.ReadAllText(Config));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"a\":null}")]
    [InlineData("{\"a\":{\"Status\":\"unknown\"}}")]
    [InlineData("{\"a\":{\"Status\":\"在玩\",\"CompletedOn\":\"bad date\"}}")]
    public void Store_CorruptDocumentFallsBack(string json)
    {
        File.WriteAllText(Config + ".desktop-journal.json", json);
        Assert.Equal(new JournalRecord(), new LibraryJournalStore(Config).Read("a"));
    }

    [Theory]
    [InlineData("已通关")]
    [InlineData("全成就")]
    public void Editor_UsesInjectedLocalDateAndRetainsDraftUntilSave(string status)
    {
        var store = new LibraryJournalStore(Config);
        var clock = new Clock();
        var editor = new JournalEditorViewModel(store, "a", "游戏", clock) { Status = status, Note = "完成" };
        Assert.Equal(new DateTime(2032, 3, 1), editor.CompletedOn);
        Assert.Equal(new JournalRecord(), store.Read("a"));
        Assert.True(editor.Save());
        Assert.Equal(new DateOnly(2032, 3, 1), store.Read("a").CompletedOn);
        var reopened = new JournalEditorViewModel(store, "a", "游戏", clock);
        Assert.Equal(editor.CompletedOn, reopened.CompletedOn);
        reopened.Status = "在玩";
        Assert.True(reopened.Save());
        Assert.Null(store.Read("a").CompletedOn);
        reopened.Status = status;
        reopened.CompletedOn = null;
        Assert.True(reopened.Save());
        Assert.Null(store.Read("a").CompletedOn);
    }

    [Fact]
    public void AllAchievements_RetainsCompletionDateAcrossReopenAndStatusChanges()
    {
        var store = new LibraryJournalStore(Config);
        var completedOn = new DateOnly(2031, 12, 24);
        store.Save("a", new("已通关", completedOn, "完成"));
        var editor = new JournalEditorViewModel(store, "a", "游戏", new Clock()) { Status = "全成就" };
        Assert.True(editor.IsCompleted);
        Assert.True(editor.Save());
        var reopenedStore = new LibraryJournalStore(Config);
        Assert.Equal(new JournalRecord("全成就", completedOn, "完成"), reopenedStore.Read("a"));
        Assert.True(reopenedStore.Matches("a", "全成就"));
        Assert.False(reopenedStore.Matches("a", "已通关"));
        var reopened = new JournalEditorViewModel(reopenedStore, "a", "游戏", new Clock()) { Status = "已通关" };
        Assert.True(reopened.Save());
        Assert.Equal(completedOn, reopenedStore.Read("a").CompletedOn);
        reopened.Status = "搁置";
        Assert.True(reopened.Save());
        Assert.Null(reopenedStore.Read("a").CompletedOn);
    }

    [Theory]
    [InlineData("未开始")]
    [InlineData("在玩")]
    [InlineData("已通关")]
    [InlineData("全成就")]
    [InlineData("搁置")]
    public void Filter_IncludesOnlyRequestedStatusAndSupportsEmptyResults(string status)
    {
        var store = new LibraryJournalStore(Config);
        store.Save("a", new(status));
        Assert.True(store.Matches("A", status));
        Assert.True(store.Matches("a", ""));
        Assert.Equal(status == "未开始", store.Matches("unrecorded", status));
        Assert.False(store.Matches("a", status == "在玩" ? "搁置" : "在玩"));
    }

    [Fact]
    public void Save_RejectsLongOrMultilineNoteAndKeepsPreviousRecord()
    {
        var store = new LibraryJournalStore(Config);
        var editor = new JournalEditorViewModel(store, "a", "游戏", new Clock()) { Note = new string('字', 80) };
        Assert.True(editor.Save());
        var bytes = File.ReadAllText(Config + ".desktop-journal.json");
        foreach (var invalid in new[] { new string('字', 81), "多\n行", "多\r行" })
        {
            editor.Note = invalid;
            Assert.False(editor.Save());
            Assert.NotEmpty(editor.Error);
            Assert.Equal(bytes, File.ReadAllText(Config + ".desktop-journal.json"));
            Assert.Equal(new string('字', 80), store.Read("a").Note);
        }
    }

    [Fact]
    public void FailedWrite_DoesNotPublishDraft()
    {
        var store = new LibraryJournalStore(Config);
        Directory.CreateDirectory(Config + ".desktop-journal.json");
        var editor = new JournalEditorViewModel(store, "a", "游戏", new Clock()) { Status = "搁置" };
        Assert.False(editor.Save());
        Assert.Equal("未开始", store.Read("a").Status);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2032, 2, 29, 18, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("JournalTest", TimeSpan.FromHours(8), "JournalTest", "JournalTest");
    }
    public void Dispose() => Directory.Delete(_root, true);
}
