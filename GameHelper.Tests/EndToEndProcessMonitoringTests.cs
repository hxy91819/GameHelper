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

        [WindowsOnlyFact]
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
                if (IsTargetProcessEvent(processName, testProcess))
                {
                    _output.WriteLine($"WMI: Process started: {processName}");
                    processStarted = true;
                    startEvent.Set();
                }
            };
            monitor.ProcessStopped += processName =>
            {
                if (IsTargetProcessEvent(processName, testProcess))
                {
                    _output.WriteLine($"WMI: Process stopped: {processName}");
                    processStopped = true;
                    stopEvent.Set();
                }
            };

            try
            {
                // Act
                monitor.Start();

                // Start a short-lived test process that exits on its own
                testProcess = StartShortLivedTestProcess();

                // Wait for start event with timeout
                var startDetected = startEvent.Wait(TimeSpan.FromSeconds(30));
                Assert.True(startDetected, "Process start should be detected within 30 seconds");
                Assert.True(processStarted, "ProcessStarted event should be fired");

                // The helper process should exit naturally within the timeout
                var exitedNaturally = WaitForProcessExit(testProcess, TimeSpan.FromSeconds(30), "WMI monitor");
                Assert.True(exitedNaturally, "Test process should exit on its own within 30 seconds");

                // Ensure the stop event arrives shortly after the process exits
                var stopDetected = stopEvent.Wait(TimeSpan.FromSeconds(5));
                Assert.True(stopDetected, "Process stop should be detected shortly after exit");
                Assert.True(processStopped, "ProcessStopped event should be fired");
            }
            finally
            {
                // Cleanup
                SafeCleanupProcess(testProcess);
                if (testProcess is not null)
                {
                    _testProcesses.Remove(testProcess);
                }

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
                if (IsTargetProcessEvent(processName, testProcess))
                {
                    _output.WriteLine($"ETW: Process started: {processName}");
                    processStarted = true;
                    startEvent.Set();
                }
            };
            monitor.ProcessStopped += processName =>
            {
                if (IsTargetProcessEvent(processName, testProcess))
                {
                    _output.WriteLine($"ETW: Process stopped: {processName}");
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
                
                // Start a short-lived test process that exits on its own
                testProcess = StartShortLivedTestProcess();

                // Wait for start event (ETW should be faster than WMI)
                var startDetected = startEvent.Wait(TimeSpan.FromSeconds(15));
                Assert.True(startDetected, "ETW process start should be detected within 15 seconds");
                Assert.True(processStarted, "ProcessStarted event should be fired");

                // Allow the helper process to exit naturally
                var exitedNaturally = WaitForProcessExit(testProcess, TimeSpan.FromSeconds(30), "ETW monitor");
                Assert.True(exitedNaturally, "Test process should exit on its own within 30 seconds");

                // Wait for stop event notification
                var stopDetected = stopEvent.Wait(TimeSpan.FromSeconds(5));
                Assert.True(stopDetected, "ETW process stop should be detected shortly after exit");
                Assert.True(processStopped, "ProcessStopped event should be fired");
            }
            finally
            {
                // Cleanup
                SafeCleanupProcess(testProcess);
                if (testProcess is not null)
                {
                    _testProcesses.Remove(testProcess);
                }

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

        [WindowsOnlyFact]
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

        private void SafeCleanupProcess(Process? process)
        {
            if (process == null)
                return;

            try
            {
                if (!WaitForProcessExit(process, TimeSpan.FromSeconds(2), "Cleanup"))
                {
                    var description = DescribeProcess(process);
                    _output.WriteLine($"Cleanup: force killing remaining process {description}");

                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception killEx)
                    {
                        _output.WriteLine($"Cleanup: Kill failed for {description}: {killEx.Message}");
                    }

                    try
                    {
                        process.WaitForExit(2000);
                    }
                    catch (Exception waitEx)
                    {
                        _output.WriteLine($"Cleanup: WaitForExit after kill failed for {description}: {waitEx.Message}");
                    }
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
            while (_testProcesses.Count > 0)
            {
                var process = _testProcesses[^1];
                _testProcesses.RemoveAt(_testProcesses.Count - 1);
                SafeCleanupProcess(process);
            }
        }

        private Process StartShortLivedTestProcess()
        {
            var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec");
            var fileName = string.IsNullOrWhiteSpace(commandInterpreter) ? "cmd.exe" : commandInterpreter;

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = "/c ping 127.0.0.1 -n 6 > NUL",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start the integration test helper process.");
            }

            _testProcesses.Add(process);
            _output.WriteLine($"Started test helper process {DescribeProcess(process)} using {startInfo.FileName} {startInfo.Arguments}");

            return process;
        }

        private bool WaitForProcessExit(Process? process, TimeSpan timeout, string context)
        {
            if (process == null)
            {
                return true;
            }

            try
            {
                var waitMilliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
                var exited = process.WaitForExit(waitMilliseconds);
                if (!exited)
                {
                    try
                    {
                        if (process.HasExited)
                        {
                            return true;
                        }
                    }
                    catch (Exception hasExitedEx)
                    {
                        _output.WriteLine($"{context}: checking HasExited failed: {hasExitedEx.Message}");
                    }

                    _output.WriteLine($"{context}: process {DescribeProcess(process)} did not exit within {timeout.TotalSeconds:F1} seconds.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"{context}: WaitForExit error: {ex.Message}");
                return false;
            }
        }

        private static string NormalizeProcessName(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return string.Empty;
            }

            var trimmed = processName.Trim();
            var fileName = Path.GetFileName(trimmed);
            var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
            return string.IsNullOrEmpty(withoutExtension) ? fileName : withoutExtension;
        }

        private bool IsTargetProcessEvent(string? reportedProcessName, Process? trackedProcess)
        {
            if (string.IsNullOrWhiteSpace(reportedProcessName))
            {
                return false;
            }

            var normalizedReportedName = NormalizeProcessName(reportedProcessName);

            if (trackedProcess != null)
            {
                try
                {
                    var trackedName = trackedProcess.ProcessName;
                    if (!string.IsNullOrEmpty(trackedName) && string.Equals(normalizedReportedName, trackedName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Failed to read tracked process name: {ex.Message}");
                }
            }

            return string.Equals(normalizedReportedName, TestProcessBaseName, StringComparison.OrdinalIgnoreCase);
        }

        private string DescribeProcess(Process process)
        {
            try
            {
                var name = process.ProcessName;
                try
                {
                    return $"{name} (PID {process.Id})";
                }
                catch
                {
                    return name;
                }
            }
            catch
            {
                try
                {
                    return $"PID {process.Id}";
                }
                catch
                {
                    return "unknown process";
                }
            }
        }

        private const string TestProcessBaseName = "cmd";

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