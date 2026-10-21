using System.Text;
using GameHelper.Core.Utilities;

namespace GameHelper.ConsoleHost.Utilities;

/// <summary>Background monitoring rotates its file log on startup and never reads console input.</summary>
internal static class BackgroundConsoleOutput
{
    public static TextWriter Open(string? config)
    {
        var path = (config ?? AppDataPath.GetConfigPath()) + ".runtime.log";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
            File.Move(path, path + ".previous", overwrite: true);
        var writer = TextWriter.Synchronized(new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetOut(writer);
        Console.SetError(writer);
        Console.SetIn(TextReader.Null);
        return writer;
    }
}
