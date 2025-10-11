using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests
{
    public class FileBackedPlayTimeServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _file;

        public FileBackedPlayTimeServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "GameHelperTests_Playtime", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "playtime.json");
        }

        [Fact]
        public void StartStop_CreatesFile_WithSingleSession()
        {
            var svc = new FileBackedPlayTimeService(_dir);
            var name = "witcher3.exe";

            svc.StartTracking(name);
            var session = svc.StopTracking(name);

            Assert.NotNull(session);
            Assert.Equal(name, session!.GameName, StringComparer.OrdinalIgnoreCase);
            Assert.True(session.Duration >= TimeSpan.Zero);

            Assert.True(File.Exists(_file));
            var json = File.ReadAllText(_file);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array);
            Assert.True(games.GetArrayLength() >= 1);

            var entry = games.EnumerateArray().FirstOrDefault(e => e.GetProperty("GameName").GetString() == name);
            Assert.True(entry.ValueKind != JsonValueKind.Undefined);

            var sessions = entry.GetProperty("Sessions");
            Assert.Equal(JsonValueKind.Array, sessions.ValueKind);
            Assert.True(sessions.GetArrayLength() == 1);
        }

        [Fact]
        public void CaseInsensitive_GameName_AccumulatesSessionsInSingleEntry()
        {
            var svc = new FileBackedPlayTimeService(_dir);
            svc.StartTracking("RDR2.EXE");
            svc.StopTracking("RDR2.EXE");

            svc.StartTracking("rdr2.exe");
            svc.StopTracking("rdr2.exe");

            var json = File.ReadAllText(_file);
            using var doc = JsonDocument.Parse(json);
            var games = doc.RootElement.GetProperty("games");

            // only one entry should exist for RDR2 regardless of casing
            var entries = games.EnumerateArray().Where(e => string.Equals(e.GetProperty("GameName").GetString(), "RDR2.EXE", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(entries.Count == 1);
            Assert.Equal(2, entries[0].GetProperty("Sessions").GetArrayLength());
        }

        [Fact]
        public void MalformedExistingFile_DoesNotThrow_AndIsRewritten()
        {
            File.WriteAllText(_file, "not-json");
            var svc = new FileBackedPlayTimeService(_dir);

            svc.StartTracking("msfs.exe");
            var session = svc.StopTracking("msfs.exe");

            Assert.NotNull(session);

            var json = File.ReadAllText(_file);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("games", out _));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
