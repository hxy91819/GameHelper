namespace GameHelper.Desktop;

internal enum ConsoleMenuCommand { Exit, Hdr, Pause, Resume, Sleep, Restart, Shutdown }
/// <summary>Controller menu, confirmation, search and invitation decisions independent of WPF.</summary>
internal static class ConsoleInteractionPolicy
{
    internal static bool Available(ConsoleMenuCommand command, bool hdrAvailable, bool remindersEnabled, bool paused) => command switch
    { ConsoleMenuCommand.Hdr => hdrAvailable, ConsoleMenuCommand.Pause => remindersEnabled && !paused,
        ConsoleMenuCommand.Resume => remindersEnabled && paused, _ => true };
    internal static bool NeedsConfirmation(ConsoleMenuCommand command) => command is ConsoleMenuCommand.Sleep or ConsoleMenuCommand.Restart or ConsoleMenuCommand.Shutdown;
    internal static bool Invite(Guid? ownSession, Guid? invitation, bool eligible, bool alreadyRated) =>
        ownSession is not null && ownSession == invitation && eligible && !alreadyRated;
    internal static bool Matches(string name, string query) => name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static int ShelfPage(int selected, int count, double viewportWidth, double cardWidth, bool next)
    {
        if (count <= 0) return -1;
        var pageSize = double.IsFinite(viewportWidth) && double.IsFinite(cardWidth) && cardWidth > 0
            ? (int)Math.Clamp(Math.Floor(viewportWidth / cardWidth), 1, count) : 1;
        return Math.Clamp(Math.Max(0, selected) + (next ? pageSize : -pageSize), 0, count - 1);
    }
}

/// <summary>Small character grid shared by search and optional rating thought entry.</summary>
internal sealed class ConsoleKeyboard(string original, int limit = 100)
{
    internal static readonly string[] Keys = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".Select(c => c.ToString()).Concat(["空格", "清空", "完成", "取消"]).ToArray();
    internal const int Columns = 10;
    internal string Text { get; private set; } = original;
    internal int Index { get; private set; }
    internal void Move(int dx, int dy) => Index = Math.Clamp(Index + dx + dy * Columns, 0, Keys.Length - 1);
    internal void Delete() { if (Text.Length > 0) Text = Text[..^1]; }
    internal void Press()
    {
        var key = Keys[Index];
        if (key == "清空") Text = "";
        else if (key is not ("完成" or "取消") && Text.Length < limit) Text += key == "空格" ? " " : key;
    }
}
