using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Controllers;
using GameHelper.Infrastructure.Processes;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace GameHelper.Tests
{
    /// <summary>
    /// Regression tests for the ETW process monitor losing non-ASCII (e.g. Chinese) image names.
    /// The kernel ProcessStart/ProcessStop payloads hold only an ANSI truncated image name and no
    /// path; TraceEvent cannot round-trip non-ASCII names through that field, so the candidate-name
    /// gate used to drop every start/stop event for such games silently - playtime sessions never
    /// existed while break reminders (which poll foreground windows directly) kept working.
    /// These tests drive the real ETW monitor end to end and assert the playtime CSV records the
    /// session, in Chinese and ASCII variants to keep the encoding differential visible.
    /// </summary>
    [Collection("ETW")]
    public sealed class EtwNonAsciiNamePlaytimeEndToEndTests : IDisposable
    {
        private const string ChineseExeName = "情感反诈模拟器";
        private const string AsciiExeName = "ascii-stub";
        private readonly ITestOutputHelper _output;
        private readonly ConcurrentBag<string> _monitorEvents = new();
        private readonly ConcurrentBag<string> _automationLogs = new();

        public EtwNonAsciiNamePlaytimeEndToEndTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public void Dispose()
        {
        }

        [Fact]
        public void RealEtwPipeline_RecordsSession_ForChineseNamedExecutable()
        {
            RunPipelineLoop(ChineseExeName, expectCsvRow: true, expectRecoveredEventIdentity: true);
        }

        [Fact]
        public void RealEtwPipeline_RecordsSession_ForAsciiNamedExecutable()
        {
            RunPipelineLoop(AsciiExeName, expectCsvRow: true, expectRecoveredEventIdentity: false);
        }

        private void RunPipelineLoop(string exeName, bool expectCsvRow, bool expectRecoveredEventIdentity)
        {
            if (!IsRunningAsAdministrator())
            {
                _output.WriteLine("Skipping: ETW requires administrator privileges");
                return;
            }

            var dataDir = Path.Combine(Path.GetTempPath(), "gh-etw-names-" + Guid.NewGuid().ToString("N"));
            var stubDir = Path.Combine(dataDir, "stub");
            Directory.CreateDirectory(stubDir);
            var stubExePath = Path.Combine(stubDir, exeName + ".exe");
            File.Copy(Environment.ExpandEnvironmentVariables("%ComSpec%"), stubExePath, overwrite: true);

            var csvPath = Path.Combine(dataDir, "playtime.csv");
            var configPath = Path.Combine(dataDir, "config.yml");
            File.WriteAllText(configPath, BuildConfig(stubExePath, exeName), new UTF8Encoding(false));

            _monitorEvents.Clear();
            _automationLogs.Clear();

            using var monitor = ProcessMonitorFactory.Create(
                ProcessMonitorType.ETW,
                allowedProcessNames: null,
                loggerFactory: null,
                pathResolver: new WindowsProcessPathResolver());
            monitor.ProcessStarted += pi => _monitorEvents.Add(
                $"START pid={pi.ProcessId} name={pi.ExecutableName} path={pi.ExecutablePath}");
            monitor.ProcessStopped += pi => _monitorEvents.Add(
                $"STOP pid={pi.ProcessId} name={pi.ExecutableName} path={pi.ExecutablePath}");

            var configuration = new YamlConfigProvider(configPath);
            var automation = new GameAutomationService(
                monitor,
                configuration,
                new NoOpHdrController(),
                new CsvBackedPlayTimeService(dataDir),
                new CapturingLogger<GameAutomationService>(_automationLogs),
                new WindowsProcessPathResolver(),
                speedController: null,
                breakReminder: null);

            Process? stub = null;
            try
            {
                automation.Start();
                monitor.Start();
                Thread.Sleep(1500); // ETW session warm-up

                stub = Process.Start(new ProcessStartInfo
                {
                    FileName = stubExePath,
                    Arguments = "/c ping 127.0.0.1 -n 6 > NUL",
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                });
                Assert.True(stub is not null, "Failed to start stub process");

                var exited = stub.WaitForExit(20000);
                Assert.True(exited, "Stub process should exit on its own");
                Thread.Sleep(3000); // stop event delivery / CSV write

                var csv = File.Exists(csvPath) ? File.ReadAllText(csvPath, Encoding.UTF8) : "";
                _output.WriteLine("playtime.csv:");
                _output.WriteLine(csv.Length == 0 ? "<empty>" : csv);
                _output.WriteLine("monitor events: " + string.Join(" | ", _monitorEvents));
                _output.WriteLine("automation logs:");
                foreach (var line in _automationLogs)
                {
                    _output.WriteLine(line);
                }

                if (!expectCsvRow)
                {
                    return;
                }

                // The monitor must have emitted a start event for this process.
                Assert.Contains(_monitorEvents, line =>
                    line.StartsWith("START", StringComparison.Ordinal) && line.Contains($"pid={stub.Id} "));
                if (expectRecoveredEventIdentity)
                {
                    // The gate must have recovered identity from the live path: the start event
                    // carries the real (non-garbled) name and full path, so no matcher fallback
                    // is needed downstream.
                    Assert.Contains(_monitorEvents, line =>
                        line.StartsWith("START", StringComparison.Ordinal) &&
                        line.Contains($"name={exeName}.exe") &&
                        line.Contains(stubExePath));
                }
                // The automation service must have opened the session.
                Assert.Contains(_automationLogs, line =>
                    line.Contains("Process start") && line.Contains($"DataKey={exeName}"));
                // A completed session row must be persisted in the playtime CSV.
                Assert.Contains(exeName, csv, StringComparison.Ordinal);
            }
            finally
            {
                try { if (stub is { HasExited: false }) stub.Kill(); } catch { }
                try { automation.Stop(); } catch { }
                try { monitor?.Dispose(); } catch { }
                try { Directory.Delete(dataDir, recursive: true); } catch { }
            }
        }

        private static string BuildConfig(string stubExePath, string gameName)
        {
            return $"""
monitor: ETW
startup:
  autoStartMonitor: false
  launchOnStartup: false
breakReminder:
  enabled: false
games:
  - dataKey: {gameName}
    executable: {stubExePath}
    displayName: {gameName}
    enabled: true
    hdr: false
""";
        }

        private static bool IsRunningAsAdministrator()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private sealed class CapturingLogger<T> : ILogger<T>
        {
            private readonly ConcurrentBag<string> _sink;
            internal CapturingLogger(ConcurrentBag<string> sink) { _sink = sink; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _sink.Add(formatter(state, exception) +
                          (exception is null ? "" : $" :: {exception}"));
            }
        }
    }
}