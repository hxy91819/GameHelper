using System;
using System.Linq;
using Microsoft.Diagnostics.Tracing.Session;
using Xunit;

namespace GameHelper.Tests
{
    /// <summary>
    /// xUnit collection definition for ETW-based tests.
    /// Ensures stale ETW sessions are cleaned up before/after the test run
    /// and serializes ETW tests so only one monitor is active at a time.
    /// </summary>
    [CollectionDefinition("ETW")]
    public class EtwCollection : ICollectionFixture<EtwSessionCleanupFixture>
    {
    }

    /// <summary>
    /// Cleans up stale GameHelper ETW sessions before and after the test collection,
    /// preventing ERROR_NO_SYSTEM_RESOURCES (0x800705AA).
    /// </summary>
    public class EtwSessionCleanupFixture : IDisposable
    {
        public EtwSessionCleanupFixture()
        {
            CleanupAllGameHelperSessions();
        }

        public void Dispose()
        {
            CleanupAllGameHelperSessions();
        }

        private static void CleanupAllGameHelperSessions()
        {
            try
            {
                var stale = TraceEventSession.GetActiveSessionNames()
                    .Where(n => n.StartsWith("GameHelper-ETW-", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var name in stale)
                {
                    try
                    {
                        using var session = new TraceEventSession(name);
                        session.Stop();
                    }
                    catch
                    {
                        // Best-effort cleanup
                    }
                }
            }
            catch
            {
                // Enumeration may fail in restricted environments
            }
        }
    }
}
