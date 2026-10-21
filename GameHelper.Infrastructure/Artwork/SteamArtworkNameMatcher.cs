using System.Text;

namespace GameHelper.Infrastructure.Artwork;

/// <summary>Only one exact normalized title may be associated automatically; editions and numbers remain significant.</summary>
public static class SteamArtworkNameMatcher
{
    public static SteamArtworkGame? ExactMatch(string title, IReadOnlyList<SteamArtworkGame> candidates)
    {
        var key = Normalize(title);
        if (key.Length == 0) return null;
        var matches = candidates.Where(item => Normalize(item.Name) == key).DistinctBy(item => item.AppId).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Normalize(string name) => new(name.Replace("™", "", StringComparison.Ordinal).Replace("®", "", StringComparison.Ordinal)
        .Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
