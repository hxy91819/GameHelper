using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Processes;
using Xunit;
using Xunit.Abstractions;

namespace GameHelper.Tests
{
    /// <summary>
    /// Integration tests for process monitors using lightweight test processes.
    /// These tests use cmd.exe with /c echo commands for faster, more reliable testing.
    /// </summary>
    public class ProcessMonitorIntegrationTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly List<Process> _testProcesses = new();

        public ProcessMonitorIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [WindowsOnlyFact]
        public void WmiMonitor_ShouldDetectShortLivedProcess()
        {
            // Arrange
            var processStarted = false;
            var startEvent = new ManualResetEventSlim(false);

            using var monitor = ProcessMonitorFactory.Create(ProcessMonitorType.WMI);
            monitor.ProcessStarted += processName =>
            {
                _output.WriteLine($"WMI: Process started: {processName}");
                if (processName.Contains("cmd", StringComparison.OrdinalIgnoreCase))
                {
                    processStarted = true;
                    startEvent.Set();
                }
            };

            try
            {
                // Act
                monitor.Start();
                
                // Start a short-lived process
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c echo WMI Test Process",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                
                Assert.NotNull(process);
                _testProcesses.Add(process);
                _output.WriteLine($"Started cmd process with PID: {process.Id}");

                // Wait for start event
                var startDetected = startEvent.Wait(TimeSpan.FromSeconds(10));
                
                // Wait for process to complete naturally
                process.WaitForExit(5000);
                
                // Assert
                Assert.True(startDetected, "Process start should be detected within 10 seconds");
                Assert.True(processStarted, "ProcessStarted event should be fired");
            }
            finally
            {
                SafeStopMonitor(monitor);
            }
        }

        [Fact]
        public void EtwMonitor_WhenRunningAsAdmin_ShouldDetectShortLivedProcess()
        {
            // Skip test if not running as administrator
            if (!IsRunningAsAdministrator())
            {
                _output.WriteLine("Skipping ETW test - requires administrator privileges");
                return;
            }

            // Arrange
            var processStarted = false;
            var startEvent = new ManualResetEventSlim(false);

            using var monitor = ProcessMonitorFactory.Create(ProcessMonitorType.ETW);
            monitor.ProcessStarted += processName =>
            {
                _output.WriteLine($"ETW: Process started: {processName}");
                if (processName.Contains("cmd", StringComparison.OrdinalIgnoreCase))
                {
                    processStarted = true;
                    startEvent.Set();
                }
            };

            try
            {
                // Act
                monitor.Start();
                
                // Give ETW a moment to initialize
                Thread.Sleep(1000);
                
                // Start a short-lived process
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c echo ETW Test Process",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                
                Assert.NotNull(process);
                _testProcesses.Add(process);
                _output.WriteLine($"Started cmd process with PID: {process.Id}");

                // Wait for start event (ETW should be faster)
                var startDetected = startEvent.Wait(TimeSpan.FromSeconds(5));
                
                // Wait for process to complete naturally
                process.WaitForExit(5000);
                
                // Assert
                Assert.True(startDetected, "ETW Process start should be detected within 5 seconds");
                Assert.True(processStarted, "ProcessStarted event should be fired");
            }
            finally
            {
                SafeStopMonitor(monitor);
            }
        }

        [WindowsOnlyFact]
        public void ProcessMonitorFactory_CreateWithFallback_ShouldCreateWorkingMonitor()
        {
            // Arrange & Act
            using var monitor = ProcessMonitorFactory.CreateWithFallback(ProcessMonitorType.ETW);

            // Assert
            Assert.NotNull(monitor);
            Assert.True(monitor is EtwProcessMonitor or WmiProcessMonitor);
            
            var monitorType = monitor is EtwProcessMonitor ? "ETW" : "WMI";
            _output.WriteLine($"Created monitor type: {monitorType}");
            
            try
            {
                // Verify it can start and stop without throwing
                monitor.Start();
                Thread.Sleep(500);
            }
            finally
            {
                SafeStopMonitor(monitor);
            }
        }

        [Fact]
        public void ProcessMonitorFactory_CreateNoOp_ShouldReturnNoOpMonitor()
        {
            // Arrange & Act
            using var monitor = ProcessMonitorFactory.CreateNoOp();

            // Assert
            Assert.NotNull(monitor);
            Assert.IsType<NoOpProcessMonitor>(monitor);
            
            // Should be safe to start and stop
            monitor.Start();
            monitor.Stop();
        }

        private void SafeStopMonitor(IProcessMonitor? monitor)
        {
            if (monitor == null)
                return;

            try
            {
                monitor.Stop();
                _output.WriteLine("Monitor stopped successfully");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Monitor stop error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            // Cleanup any remaining test processes
            foreach (var process in _testProcesses)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                    }
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Process cleanup error: {ex.Message}");
                }
            }
            _testProcesses.Clear();
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