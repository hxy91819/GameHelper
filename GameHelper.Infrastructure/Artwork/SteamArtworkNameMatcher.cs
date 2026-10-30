using System.Text;
using System.Text.RegularExpressions;

namespace GameHelper.Infrastructure.Artwork;

/// <summary>Ranks multilingual game titles conservatively; editions, sequels and ambiguous results require a user choice.</summary>
public static class SteamArtworkNameMatcher
{
    private static readonly Dictionary<string, string> Roman = new[] { "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX" }
        .Select((name, index) => (name, value: (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToDictionary(item => item.name, item => item.value, StringComparer.OrdinalIgnoreCase);

    /// <summary>One-word release aliases that may appear on one side only; real sequels (SS, II, 2) never enter the list.</summary>
    private static readonly string[] ReleaseAliases = ["HD", "PLUS", "REMIX"];

    /// <summary>Chooses one confident best base-game result; ambiguity, sequels and unknown fuzzy matches require a user choice.</summary>
    public static SteamArtworkGame? BestMatch(string title, IReadOnlyList<SteamArtworkGame> candidates)
    {
        if (title.Length > 300) return null;
        var ranked = candidates.DistinctBy(item => item.AppId).Select(item => (Game: item, Score: Score(title, item)))
            .Where(item => item.Score > 0).OrderByDescending(item => item.Score).ToList();
        if (ranked.Count == 0) return null;
        var best = ranked[0]; var runnerUp = ranked.Count > 1 ? ranked[1].Score : 0;
        return best.Score >= .90 && best.Score - runnerUp >= .10 && (best.Score == 1 || best.Game.IsGame == true) ? best.Game : null;
    }

    public static double Score(string title, SteamArtworkGame candidate)
    {
        if (candidate.IsGame == false || title.Length > 300) return 0;
        return new[] { candidate.Name, candidate.EnglishName }.Where(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 300)
            .Select(name => Similarity(title, name!, candidate.IsGame)).DefaultIfEmpty().Max();
    }

    private static double Similarity(string query, string title, bool? isGame)
    {
        query = Clean(query); title = Clean(title);
        if (isGame is null && Regex.IsMatch(title, @"\b(dlc|artbook|soundtrack|demo|playtest|dedicated server)\b|原声|设定集|艺术集", RegexOptions.IgnoreCase)) return 0;
        var left = Normalize(query); var right = Normalize(title);
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 1;
        // A trailing known release alias on one side only ("NTRaholic" vs "NTRaholic Remix") stays a confident match;
        // anything else the alias leaves behind (SS, Deluxe, a sequel number) keeps requiring a user choice.
        if (MatchesWithTrailingAlias(left, right) || MatchesWithTrailingAlias(right, left)) return 1;
        if (!Regex.Matches(query, @"\d+").Select(match => match.Value).SequenceEqual(Regex.Matches(title, @"\d+").Select(match => match.Value))) return 0;
        const string editions = @"\b(deluxe|ultimate|definitive|enhanced|remaster\w*|anniversary|complete|goty)\b";
        if (!Regex.Matches(query, editions, RegexOptions.IgnoreCase).Select(match => match.Value.ToUpperInvariant()).Order().SequenceEqual(
            Regex.Matches(title, editions, RegexOptions.IgnoreCase).Select(match => match.Value.ToUpperInvariant()).Order())) return 0;
        if (Math.Min(left.Length, right.Length) < 8 || isGame != true) return 0;
        var distances = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++) distances[i, 0] = i;
        for (var j = 0; j <= right.Length; j++) distances[0, j] = j;
        for (var i = 1; i <= left.Length; i++) for (var j = 1; j <= right.Length; j++)
        {
            distances[i, j] = Math.Min(Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1), distances[i - 1, j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1]) distances[i, j] = Math.Min(distances[i, j], distances[i - 2, j - 2] + 1);
        }
        return 1 - distances[left.Length, right.Length] / (double)Math.Max(left.Length, right.Length);
    }
    public static SteamArtworkGame? ExactMatch(string title, IReadOnlyList<SteamArtworkGame> candidates)
    {
        var key = Normalize(title);
        if (key.Length == 0) return null;
        var matches = candidates.Where(item => item.IsGame != false && (Normalize(item.Name) == key || (item.EnglishName is not null && Normalize(item.EnglishName) == key))).DistinctBy(item => item.AppId).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool MatchesWithTrailingAlias(string left, string right)
    {
        foreach (var alias in ReleaseAliases)
            if (right.Length > alias.Length && right.EndsWith(alias, StringComparison.Ordinal) && left == right[..^alias.Length]) return true;
        return false;
    }

    /// <summary>Strips launcher-style release tails ("v1.0.9", "0.26.10-pc", "Build 14020192") from the end.
    /// A bare trailing number without dots ("Portal 2") is a sequel name, not a build tag, and stays.</summary>
    private static string StripReleaseTails(string name)
    {
        while (true)
        {
            var next = Regex.Replace(name, @"[\s._-]+(build|v)\s*\d+(\.\d+)*$", "", RegexOptions.IgnoreCase);
            next = Regex.Replace(next, @"[\s._-]*\d+(\.\d+){1,4}$", "");
            next = Regex.Replace(next, @"[\s._-]+(pc|x64|win64)$", "", RegexOptions.IgnoreCase);
            next = next.TrimEnd(' ', '.', '_', '-');
            if (next.Length == 0 || next == name) return name;
            name = next;
        }
    }
    private static string Clean(string name)
    {
        name = Regex.Replace(name.Trim(), @"\s*-\s*(快捷方式|Shortcut)$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\.(exe|lnk|url)$", "", RegexOptions.IgnoreCase);
        name = StripReleaseTails(name);
        name = name.Replace("™", "", StringComparison.Ordinal).Replace("®", "", StringComparison.Ordinal).Normalize(NormalizationForm.FormKC);
        return Regex.Replace(name, @"\b[IVX]{1,5}\b", match => Roman.GetValueOrDefault(match.Value, match.Value), RegexOptions.IgnoreCase);
    }
    private static string Normalize(string name) => new(Clean(name).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
