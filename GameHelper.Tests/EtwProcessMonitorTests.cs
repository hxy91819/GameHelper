using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Principal;
using GameHelper.Core.Abstractions;
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
            var monitor = new EtwProcessMonitor(null, null);
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
        public void Configure_AppliesCandidateAndStopEventContract()
        {
            var monitor = new EtwProcessMonitor();
            monitor.Configure(new ProcessObservationPolicy(new[] { "GAME" }, observeStopEvents: false));

            Assert.True(monitor.IsAllowedProcessStart("game.exe", null));
            Assert.False(monitor.IsAllowedProcessStart("other.exe", null));
            Assert.False(monitor.IsAllowedProcessStop("game.exe", hadCachedStart: true));

            monitor.Configure(new ProcessObservationPolicy(new[] { "game.exe" }, observeStopEvents: true));

            Assert.True(monitor.IsAllowedProcessStop("game.exe", hadCachedStart: false));
            Assert.True(monitor.IsAllowedProcessStop("renamed.exe", hadCachedStart: true));
            Assert.False(monitor.IsAllowedProcessStop("other.exe", hadCachedStart: false));
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

