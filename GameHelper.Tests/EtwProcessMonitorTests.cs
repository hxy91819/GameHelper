using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Principal;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Exceptions;
using GameHelper.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace GameHelper.Tests
{
    public class EtwProcessMonitorTests
    {
        [Fact]
        public void Constructor_WithNullAllowedProcessNames_ShouldNotThrow()
        {
            // Arrange & Act & Assert
            var monitor = new EtwProcessMonitor((IEnumerable<string>?)null, null);
            Assert.NotNull(monitor);
        }

        [Fact]
        public void Constructor_WithEmptyAllowedProcessNames_ShouldNotThrow()
        {
            // Arrange & Act & Assert
            var monitor = new EtwProcessMonitor(new List<string>(), null);
            Assert.NotNull(monitor);
        }

        [Fact]
        public void Constructor_WithValidAllowedProcessNames_ShouldNotThrow()
        {
            // Arrange
            var allowedProcesses = new[] { "game.exe", "launcher.exe" };

            // Act & Assert
            var monitor = new EtwProcessMonitor(allowedProcesses, null);
            Assert.NotNull(monitor);
        }

        [Fact]
        public void Start_WhenNotRunningAsAdministrator_ShouldThrowInsufficientPrivilegesException()
        {
            // Arrange
            var monitor = new EtwProcessMonitor();

            // Act & Assert
            // Note: This test will pass when running as non-admin, fail when running as admin
            // In a real test environment, you might want to mock the administrator check
            if (!IsRunningAsAdministrator())
            {
                Assert.Throws<InsufficientPrivilegesException>(() => monitor.Start());
            }
        }

        [Fact]
        public void Configure_ShouldNotThrow()
        {
            // Arrange
            var monitor = new EtwProcessMonitor();

            // Act & Assert
            monitor.Configure(new ProcessObservationPolicy(new[] { "game.exe" }, observeStopEvents: true));
            monitor.Configure(new ProcessObservationPolicy(new[] { "game.exe" }, observeStopEvents: false));
        }

        [Fact]
        public void Dispose_ShouldNotThrow()
        {
            // Arrange
            var monitor = new EtwProcessMonitor();

            // Act & Assert
            monitor.Dispose();
            monitor.Dispose(); // Should be safe to call multiple times
        }

        [Fact]
        public void Stop_WhenNotStarted_ShouldNotThrow()
        {
            // Arrange
            var monitor = new EtwProcessMonitor();

            // Act & Assert
            monitor.Stop();
        }

        [Fact]
        public void Start_AfterDispose_ShouldThrowObjectDisposedException()
        {
            // Arrange
            var monitor = new EtwProcessMonitor();
            monitor.Dispose();

            // Act & Assert
            Assert.Throws<ObjectDisposedException>(() => monitor.Start());
        }

        private static bool IsRunningAsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        [Fact]
        public void IsAllowedProcessStart_WhenPathHintFilenameAllowed_ReturnsTrue()
        {
            var monitor = new EtwProcessMonitor(new[] { "game.exe" }, null);
            var allowedByPath = monitor.IsAllowedProcessStart("launcher.exe", @"C:\Steam\game.exe");
            var rejected = monitor.IsAllowedProcessStart("launcher.exe", @"C:\Tools\launcher.exe");

            Assert.True(allowedByPath);
            Assert.False(rejected);
        }

        [Fact]
        public void AcceptStartInfo_WhenKernelNameUnreadable_RecoversIdentityFromLivePath()
        {
            // The kernel ProcessStart payload holds only an ANSI truncated image name; non-ASCII
            // names (Chinese executables) arrive as unrecoverable mojibake with no path payload.
            const string gamePath = @"C:\Games\情感反诈模拟器\情感反诈模拟器.exe";
            var monitor = new EtwProcessMonitor(new[] { "情感反诈模拟器.exe" }, null, new StubPathResolver(processId =>
                processId == 99 ? gamePath : null));

            var recovered = monitor.AcceptStartInfo(new ProcessEventInfo("\uFFFD\uFFFD\uFFFD.exe", null), 99);
            var unrelated = monitor.AcceptStartInfo(new ProcessEventInfo("\uFFFD\uFFFD\uFFFD.exe", null), 7);

            Assert.True(recovered.HasValue);
            Assert.Equal("情感反诈模拟器.exe", recovered!.Value.ExecutableName);
            Assert.Equal(gamePath, recovered!.Value.ExecutablePath);
            Assert.False(unrelated.HasValue);
        }

        [Fact]
        public void AcceptStartInfo_WhenPolicyMatchesName_ReturnsInfoUnchanged()
        {
            var monitor = new EtwProcessMonitor(new[] { "game.exe" }, null, new ThrowingPathResolver());

            var info = monitor.AcceptStartInfo(new ProcessEventInfo("launcher.exe", @"C:\Steam\game.exe"), 12);

            Assert.True(info.HasValue);
            Assert.Equal("launcher.exe", info!.Value.ExecutableName);
            Assert.Equal(@"C:\Steam\game.exe", info!.Value.ExecutablePath);
        }

        private sealed class StubPathResolver(Func<int, string?> resolver) : GameHelper.Core.Abstractions.IProcessPathResolver
        {
            public string? TryResolveExecutablePath(int processId) => resolver(processId);
        }

        private sealed class ThrowingPathResolver : GameHelper.Core.Abstractions.IProcessPathResolver
        {
            public string? TryResolveExecutablePath(int processId) => throw new InvalidOperationException("must not be called");
        }

        [Fact]
        public void IsResourceExhausted_RecognizesErrorNoSystemResources()
        {
            var ex = new System.Runtime.InteropServices.COMException("test", unchecked((int)0x800705AA));
            var method = typeof(EtwProcessMonitor).GetMethod("IsResourceExhausted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = (bool)method!.Invoke(null, new object[] { ex })!;
            Assert.True(result);
        }

        [Fact]
        public void IsResourceExhausted_ReturnsFalseForOtherExceptions()
        {
            var ex = new System.Runtime.InteropServices.COMException("test", unchecked((int)0x80070005)); // Access denied
            var method = typeof(EtwProcessMonitor).GetMethod("IsResourceExhausted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = (bool)method!.Invoke(null, new object[] { ex })!;
            Assert.False(result);
        }
    }
}

