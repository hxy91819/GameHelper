namespace GameHelper.Desktop;

/// <summary>Pure console library selection and launch lifecycle decisions.</summary>
internal static class ConsoleModePolicy
{
    internal sealed record Entry(string Key, DateTimeOffset? LastPlayed, bool Completed, string Support, bool? Manual);
    internal static IEnumerable<Entry> Select(IEnumerable<Entry> entries, int tab, TimeProvider clock) => entries
        .Where(e => tab switch { 1 => e.LastPlayed >= clock.GetUtcNow().AddDays(-30), 2 => !e.Completed,
            3 => e.Manual == true || (e.Manual is null && e.Support == "full"), _ => true })
        .OrderByDescending(e => e.LastPlayed).ThenBy(e => e.Key, StringComparer.Ordinal);
    internal static bool Confirm(string support, bool? manual) => manual == false ||
        (manual is null && support is "partial" or "unlisted");
    internal static bool Restore(bool launchedHere, bool observedRunning, bool running) => launchedHere && observedRunning && !running;
}
