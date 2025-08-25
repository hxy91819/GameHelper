using System;
using System.IO;
using GameHelper.Infrastructure.Resolvers;
using Xunit;

namespace GameHelper.Tests
{
    public class SteamGameResolverIntegrationTests
    {
        [Fact]
        public void Resolve_From_SteamUrl_RealEnv_Print_Result()
        {
            // No mocks, access real registry/Steam. This test will only assert when the app is installed.
            var resolver = new SteamGameResolver();
            var url = "steam://rungameid/2358720";
            var parsedId = resolver.TryParseRunGameId(url);
            Assert.Equal("2358720", parsedId);

            var exe = resolver.TryResolveExeFromAppId(parsedId!);
            if (exe is null)
            {
                // Not installed or Steam not present on this machine; do not fail the test.
                // The purpose is to exercise the real path when available.
                return;
            }

            Assert.True(File.Exists(exe), $"Resolved exe does not exist: {exe}");
            // Also assert we're getting an .exe name
            Assert.EndsWith(".exe", Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase);
        }

    }
}
