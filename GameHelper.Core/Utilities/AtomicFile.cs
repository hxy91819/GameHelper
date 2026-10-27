namespace GameHelper.Core.Utilities;

/// <summary>Writes a sibling temporary file before moving it into place. The writer must close its handles before returning.</summary>
public static class AtomicFile
{
    /// <summary>Publishes a complete file, cleaning up the temporary file even when the writer fails.</summary>
    public static void Write(string path, Action<string> write, bool overwrite = true,
        Action<string>? cleanup = null, string? temporaryPath = null)
    {
        var temporary = temporaryPath ?? path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            write(temporary);
            File.Move(temporary, path, overwrite);
        }
        finally { (cleanup ?? DeleteTemporary)(temporary); }
    }

    /// <summary>Asynchronously writes a complete file before publishing it.</summary>
    public static async Task WriteAsync(string path, Func<string, Task> write, bool overwrite = true,
        Action<string>? cleanup = null)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await write(temporary).ConfigureAwait(false);
            File.Move(temporary, path, overwrite);
        }
        finally { (cleanup ?? DeleteTemporary)(temporary); }
    }

    private static void DeleteTemporary(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
