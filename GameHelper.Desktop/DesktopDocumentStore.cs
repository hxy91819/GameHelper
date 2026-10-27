using System.IO;
using System.Text.Json;
using GameHelper.Core.Utilities;

namespace GameHelper.Desktop;

/// <summary>JSON side documents only; runtime configuration is owned by its configuration provider.</summary>
internal static class DesktopDocumentStore
{
    internal static T Read<T>(string? path, Func<T> empty, bool recoverReadFailure = true) where T : class
    {
        try
        {
            return path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? empty() : empty();
        }
        catch (Exception ex) when (recoverReadFailure && ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return empty();
        }
    }

    internal static void Write<T>(string path, T document) =>
        WriteJson(path, () => JsonSerializer.Serialize(document));

    // Drafts already serialized their payload for write deduplication and historically use the supplied directory.
    internal static void WriteJson(string path, Func<string> serialize, bool resolveDirectory = true) =>
        AtomicFile.Write(path, temporary =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolveDirectory ? Path.GetFullPath(path) : path)!);
            File.WriteAllText(temporary, serialize());
        });
}
