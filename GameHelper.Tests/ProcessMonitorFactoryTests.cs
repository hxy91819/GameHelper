using System;
using System.Collections.Generic;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace GameHelper.Tests
{
    public class ProcessMonitorFactoryTests
    {
        [Fact]
        public void Create_WithWmiType_ShouldReturnWmiProcessMonitor()
        {
            // Arrange & Act
            var monitor = ProcessMonitorFactory.Create(ProcessMonitorType.WMI);

            // Assert
            Assert.IsType<WmiProcessMonitor>(monitor);
        }

        [Fact]
        public void Create_WithEtwType_ShouldReturnEtwProcessMonitor()
        {
            // Arrange & Act
            var monitor = ProcessMonitorFactory.Create(ProcessMonitorType.ETW);

            // Assert
            Assert.IsType<EtwProcessMonitor>(monitor);
        }

        [Fact]
        public void Create_WithInvalidType_ShouldThrowArgumentException()
        {
            // Arrange
            var invalidType = (ProcessMonitorType)999;

            // Act & Assert
            Assert.Throws<ArgumentException>(() => ProcessMonitorFactory.Create(invalidType));
        }

        [Fact]
        public void Create_WithAllowedProcessNames_ShouldNotThrow()
        {
            // Arrange
            var allowedProcesses = new[] { "game.exe", "launcher.exe" };

            // Act & Assert
            var wmiMonitor = ProcessMonitorFactory.Create(ProcessMonitorType.WMI, allowedProcesses);
            var etwMonitor = ProcessMonitorFactory.Create(ProcessMonitorType.ETW, allowedProcesses);
            
            Assert.NotNull(wmiMonitor);
            Assert.NotNull(etwMonitor);
        }

        [Fact]
        public void Create_WithLogger_ShouldNotThrow()
        {
            // Arrange
            var mockLogger = new Mock<ILogger>();

            // Act & Assert
            var wmiMonitor = ProcessMonitorFactory.Create(ProcessMonitorType.WMI, null, mockLogger.Object);
            var etwMonitor = ProcessMonitorFactory.Create(ProcessMonitorType.ETW, null, mockLogger.Object);
            
            Assert.NotNull(wmiMonitor);
            Assert.NotNull(etwMonitor);
        }

        [Fact]
        public void CreateWithFallback_WithWmiPreferred_ShouldReturnWmiMonitor()
        {
            // Arrange & Act
            var monitor = ProcessMonitorFactory.CreateWithFallback(ProcessMonitorType.WMI);

            // Assert
            Assert.IsType<WmiProcessMonitor>(monitor);
        }

        [Fact]
        public void CreateWithFallback_WithEtwPreferred_ShouldReturnMonitor()
        {
            // Arrange & Act
            var monitor = ProcessMonitorFactory.CreateWithFallback(ProcessMonitorType.ETW);

            // Assert
            // Should return either ETW (if admin) or WMI (if fallback occurred)
            Assert.True(monitor is EtwProcessMonitor or WmiProcessMonitor);
        }

        [Fact]
        public void CreateNoOp_ShouldReturnNoOpProcessMonitor()
        {
            // Arrange & Act
            var monitor = ProcessMonitorFactory.CreateNoOp();

            // Assert
            Assert.IsType<NoOpProcessMonitor>(monitor);
        }
    }
}