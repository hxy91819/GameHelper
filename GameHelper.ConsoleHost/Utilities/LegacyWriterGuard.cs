using GameHelper.Infrastructure.Ipc;

namespace GameHelper.ConsoleHost.Utilities;

/// <summary>Older Desktop writers do not know the SQLite directory lock. Refuse first activation while their UI is open.</summary>
internal static class LegacyWriterGuard
{
    internal static void EnsureDesktopStopped(string configPath)
    {
        if (Mutex.TryOpenExisting(@"Local\" + GameLibraryPipe.NameFor(configPath) + ".Desktop", out var desktop))
        {
            desktop.Dispose();
            throw new InvalidOperationException("请关闭游戏库后重启后台，再自动迁移；旧版游戏库仍可能写入文档。");
        }
    }
}
