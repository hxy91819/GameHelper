using System;
using System.Collections.Generic;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Models;
using Xunit;

namespace GameHelper.Tests
{
    public class BuildInfoHelperTests
    {
        [Fact]
        public void GetVersionDescription_ReturnsNonEmptyString()
        {
            var version = BuildInfoHelper.GetVersionDescription();
            Assert.False(string.IsNullOrWhiteSpace(version));
            Assert.NotEqual("unknown", version);
        }

        [Fact]
        public void GetBuildTimeDescription_ReturnsParsableDateTime()
        {
            var buildTime = BuildInfoHelper.GetBuildTimeDescription();
            Assert.False(string.IsNullOrWhiteSpace(buildTime));

            // When running under dotnet test the assembly location may be empty
            // (single-file or in-memory load), so "unknown" is acceptable.
            if (!string.Equals(buildTime, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(
                    DateTime.TryParseExact(buildTime, "yyyy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out _),
                    $"Build time '{buildTime}' should match format yyyy-MM-dd HH:mm:ss");
            }
        }
    }
}
