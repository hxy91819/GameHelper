using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests;

public sealed class ConfigFileWatcherTests : IDisposable
{
    private const int DebounceMs = 150;

    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "GameHelperTests",
        Guid.NewGuid().ToString("N"));
    private readonly string _configFilePath;

    public ConfigFileWatcherTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _configFilePath = Path.Combine(_tempDirectory, "config.yml");
    }

    [Fact]
    public void RapidEdits_CoalesceIntoSingleReload_AndLaterEditsReloadAgain()
    {
        var reloadCount = 0;
        using var watcher = new ConfigFileWatcher(
            _configFilePath, () => Interlocked.Increment(ref reloadCount),
            NullLogger.Instance, TimeSpan.FromMilliseconds(DebounceMs));

        watcher.Start();
        File.WriteAllText(_configFilePath, "first");
        File.WriteAllText(_configFilePath, "second");
        File.WriteAllText(_configFilePath, "third");

        Assert.True(WaitFor(() => Volatile.Read(ref reloadCount) == 1));
        // The burst must land exactly once, not once per raw file event.
        var quietPeriod = TimeSpan.FromMilliseconds(DebounceMs * 3);
        Thread.Sleep(quietPeriod);
        Assert.Equal(1, Volatile.Read(ref reloadCount));

        File.WriteAllText(_configFilePath, "fourth");
        Assert.True(WaitFor(() => Volatile.Read(ref reloadCount) == 2));
    }

    [Fact]
    public void FileMissingInsideDebounceWindow_SkipsReload()
    {
        var reloadCount = 0;
        using var watcher = new ConfigFileWatcher(
            _configFilePath, () => Interlocked.Increment(ref reloadCount),
            NullLogger.Instance, TimeSpan.FromMilliseconds(DebounceMs));

        watcher.Start();
        File.WriteAllText(_configFilePath, "about to vanish");
        File.Delete(_configFilePath);

        Thread.Sleep(TimeSpan.FromMilliseconds(DebounceMs * 4));
        // An absent file must never push an empty config into consumers; the next
        // real change once the file exists again is what reloads.
        Assert.Equal(0, Volatile.Read(ref reloadCount));
    }

    [Fact]
    public void ReloadFailure_IsRetriedOnce_AndWatcherKeepsWorking()
    {
        var attempts = 0;
        using var watcher = new ConfigFileWatcher(
            _configFilePath,
            () =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    throw new InvalidOperationException("config still mid-write");
                }
            },
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(DebounceMs));

        watcher.Start();
        File.WriteAllText(_configFilePath, "causes first failing read");

        // First attempt fails, the bounded retry succeeds, and no third attempt fires.
        Assert.True(WaitFor(() => Volatile.Read(ref attempts) >= 2));
        Thread.Sleep(TimeSpan.FromMilliseconds(DebounceMs * 4));
        Assert.Equal(2, Volatile.Read(ref attempts));

        // The watcher is still alive after the failure path.
        File.WriteAllText(_configFilePath, "next change");
        Assert.True(WaitFor(() => Volatile.Read(ref attempts) >= 3));
    }

    [Fact]
    public void Start_IsIdempotent_AndEventsAfterDisposeAreIgnored()
    {
        var reloadCount = 0;
        var watcher = new ConfigFileWatcher(
            _configFilePath, () => Interlocked.Increment(ref reloadCount),
            NullLogger.Instance, TimeSpan.FromMilliseconds(DebounceMs));

        watcher.Start();
        watcher.Start();

        File.WriteAllText(_configFilePath, "one watcher only");
        Assert.True(WaitFor(() => Volatile.Read(ref reloadCount) == 1));

        watcher.Dispose();
        watcher.Dispose();

        File.WriteAllText(_configFilePath, "ignored after dispose");
        Thread.Sleep(TimeSpan.FromMilliseconds(DebounceMs * 3));
        Assert.Equal(1, Volatile.Read(ref reloadCount));
    }

    [Fact]
    public void UnwatchablePath_StartDegrades_AndLaterStartRetries()
    {
        // Occupy the would-be config directory with a regular file so the watcher
        // creation inside Start() fails and the no-op degradation path is exercised.
        var blocker = Path.Combine(_tempDirectory, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var configPath = Path.Combine(blocker, "config.yml");

        var reloadCount = 0;
        using var watcher = new ConfigFileWatcher(
            configPath, () => Interlocked.Increment(ref reloadCount),
            NullLogger.Instance, TimeSpan.FromMilliseconds(DebounceMs));

        watcher.Start();

        // Start() must not throw even though the watch could not be established,
        // and no reload can fire because no events were ever raised.
        Thread.Sleep(TimeSpan.FromMilliseconds(DebounceMs * 2));
        Assert.Equal(0, Volatile.Read(ref reloadCount));

        // Once the obstruction is gone, Start() retries the watch (the failed attempt
        // left _watcher null) and hot reload begins working.
        File.Delete(blocker);
        watcher.Start();
        File.WriteAllText(configPath, "content");
        Assert.True(WaitFor(() => Volatile.Read(ref reloadCount) == 1));
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Temp dirs under %TEMP% are best-effort cleanup.
        }
    }
}