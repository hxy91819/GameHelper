using System;
using System.Collections.Generic;
using System.Security.Principal;
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
        public void SetStopEventsEnabled_ShouldNotThrow()
        {
            // Arrange
            var monitor = new EtwProcessMonitor();

            // Act & Assert
            monitor.SetStopEventsEnabled(true);
            monitor.SetStopEventsEnabled(false);
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
    }
}