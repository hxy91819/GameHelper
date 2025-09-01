using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace GameHelper.Tests
{
    public class EndToEndProcessMonitoringTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly List<Process> _testProcesses = new();

        public EndToEndProcessMonitoringTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void WmiMonitor_ShouldDetectProcessStartAndStop()
        {
            // Arrange
            var processStarted = false;
            var processStopped = false;
            var startEvent = new ManualResetEventSlim(false);
            var stopEvent = new ManualResetEventSlim(false);
            Process? testProcess = null;

            using var monitor = ProcessMonitorFactory.Create(ProcessMonitorType.WMI);
            monitor.ProcessStarted += processName =>
            {
                _output.WriteLine($"WMI: Process started: {processName}");
                if (processName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
                {
                    processStarted = true;
                    startEvent.Set();
                }
            };
            monitor.ProcessStopped += processName =>
            {
                _output.WriteLine($"WMI: Process stopped: {processName}");
                if (processName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
                {
                    processStopped = true;
                    stopEvent.Set();
                }
            };

            try
            {
                // Act
                monitor.Start();
                
                // Start a test process
                testProcess = Process.Start("notepad.exe");
                Assert.NotNull(testProcess);
                _testProcesses.Add(testProcess);
                _output.WriteLine($"Started notepad process with PID: {testProcess.Id}");

                // Wait for start event with timeout
                var startDetected = startEvent.Wait(TimeSpan.FromSeconds(15));
                
                if (startDetected)
                {
                    _output.WriteLine("Start event detected, now terminating process");
                    
                    // Terminate the process gracefully first, then force kill if needed
                    TerminateProcessSafely(testProcess);
                    
                    // Wait for stop event
                    var stopDetected = stopEvent.Wait(TimeSpan.FromSeconds(15));
                    
                    // Assert
                    Assert.True(startDetected, "Process start should be detected within 15 seconds");
                    Assert.True(stopDetected, "Process stop should be detected within 15 seconds");
                    Assert.True(processStarted, "ProcessStarted event should be fired");
                    Assert.True(processStopped, "ProcessStopped event should be fired");
                }
                else
                {
                    Assert.Fail("Failed to detect process start within timeout period");
                }
            }
            finally
            {
                // Cleanup
                SafeCleanupProcess(testProcess);
                SafeStopMonitor(monitor);
            }
        }

        [Fact]
        public async Task EtwMonitor_WhenRunningAsAdmin_ShouldDetectProcessStartAndStop()
        {
            // Skip test if not running as administrator
            if (!IsRunningAsAdministrator())
            {
                _output.WriteLine("Skipping ETW test - requires administrator privileges");
                return;
            }

            // Arrange
            var processStarted = false;
            var processStopped = false;
            var startEvent = new ManualResetEventSlim(false);
            var stopEvent = new ManualResetEventSlim(false);
            Process? testProcess = null;

            using var monitor = ProcessMonitorFactory.Create(ProcessMonitorType.ETW);
            monitor.ProcessStarted += processName =>
            {
                _output.WriteLine($"ETW: Process started: {processName}");
                if (processName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
                {
                    processStarted = true;
                    startEvent.Set();
                }
            };
            monitor.ProcessStopped += processName =>
            {
                _output.WriteLine($"ETW: Process stopped: {processName}");
                if (processName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
                {
                    processStopped = true;
                    stopEvent.Set();
                }
            };

            try
            {
                // Act
                monitor.Start();
                
                // Give ETW a moment to initialize
                await Task.Delay(1000);
                
                // Start a test process
                testProcess = Process.Start("notepad.exe");
                Assert.NotNull(testProcess);
                _testProcesses.Add(testProcess);
                _output.WriteLine($"Started notepad process with PID: {testProcess.Id}");

                // Wait for start event (ETW should be faster than WMI)
                var startDetected = startEvent.Wait(TimeSpan.FromSeconds(10));
                
                if (startDetected)
                {
                    _output.WriteLine("ETW start event detected, now terminating process");
                    
                    // Terminate the process
                    TerminateProcessSafely(testProcess);
                    
                    // Wait for stop event
                    var stopDetected = stopEvent.Wait(TimeSpan.FromSeconds(10));
                    
                    // Assert
                    Assert.True(startDetected, "ETW Process start should be detected within 10 seconds");
                    Assert.True(stopDetected, "ETW Process stop should be detected within 10 seconds");
                    Assert.True(processStarted, "ProcessStarted event should be fired");
                    Assert.True(processStopped, "ProcessStopped event should be fired");
                }
                else
                {
                    Assert.Fail("Failed to detect ETW process start within timeout period");
                }
            }
            finally
            {
                // Cleanup
                SafeCleanupProcess(testProcess);
                SafeStopMonitor(monitor);
            }
        }

        [Fact]
        public void EtwMonitor_WhenNotRunningAsAdmin_ShouldThrowInsufficientPrivilegesException()
        {
            // Skip test if running as administrator
            if (IsRunningAsAdministrator())
            {
                _output.WriteLine("Skipping non-admin ETW test - running as administrator");
                return;
            }

            // Arrange & Act & Assert
            using var monitor = ProcessMonitorFactory.Create(ProcessMonitorType.ETW);
            var exception = Assert.Throws<Infrastructure.Exceptions.InsufficientPrivilegesException>(() => monitor.Start());
            Assert.Contains("administrator", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CreateWithFallback_WhenEtwFails_ShouldFallbackToWmi()
        {
            // Arrange & Act
            using var monitor = ProcessMonitorFactory.CreateWithFallback(ProcessMonitorType.ETW);

            // Assert
            Assert.NotNull(monitor);
            // Should be either ETW (if admin) or WMI (if fallback)
            Assert.True(monitor is EtwProcessMonitor or WmiProcessMonitor);
            
            try
            {
                // Verify it can start without throwing
                monitor.Start();
                
                // Give it a moment to initialize
                Thread.Sleep(500);
            }
            finally
            {
                SafeStopMonitor(monitor);
            }
        }

        private void TerminateProcessSafely(Process? process)
        {
            if (process == null || process.HasExited)
                return;

            try
            {
                _output.WriteLine($"Attempting to close process {process.Id} gracefully");
                
                // Try graceful close first
                if (process.CloseMainWindow())
                {
                    if (process.WaitForExit(3000))
                    {
                        _output.WriteLine("Process closed gracefully");
                        return;
                    }
                }
                
                // Force kill if graceful close failed
                _output.WriteLine("Graceful close failed, force killing process");
                process.Kill();
                process.WaitForExit(5000);
                _output.WriteLine("Process force killed");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Error terminating process: {ex.Message}");
            }
        }

        private void SafeCleanupProcess(Process? process)
        {
            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                {
                    _output.WriteLine($"Cleanup: Force killing remaining process {process.Id}");
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Cleanup process error: {ex.Message}");
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Process dispose error: {ex.Message}");
                }
            }
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
                SafeCleanupProcess(process);
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