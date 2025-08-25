using System.Collections.Generic;

namespace GameHelper.Core.Abstractions
{
    /// <summary>
    /// Resolve Steam shortcuts/URIs (e.g., steam://rungameid/12345) to local game executable paths.
    /// </summary>
    public interface ISteamGameResolver
    {
        /// <summary>
        /// Try parse a .url (Internet Shortcut) file to a URL string.
        /// Returns null if file invalid or URL missing.
        /// </summary>
        string? TryParseInternetShortcutUrl(string urlFilePath);

        /// <summary>
        /// Extract appId from a steam://rungameid/{appid} URL.
        /// Returns null if not a supported steam URL.
        /// </summary>
        string? TryParseRunGameId(string steamUrl);

        /// <summary>
        /// Resolve a Steam appId to an installed game executable path on this machine.
        /// Returns null if Steam not installed or app not found.
        /// </summary>
        string? TryResolveExeFromAppId(string appId);

        /// <summary>
        /// For diagnostics: enumerate candidate executables for a given appId, if available.
        /// </summary>
        IReadOnlyList<string> TryEnumerateExeCandidates(string appId);
    }
}
