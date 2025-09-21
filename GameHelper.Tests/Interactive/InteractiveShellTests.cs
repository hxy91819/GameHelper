using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Interactive;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console.Testing;
using Xunit;
using Xunit.Abstractions;

namespace GameHelper.Tests.Interactive
{
    public class InteractiveShellTests
    {
        private readonly ITestOutputHelper _output;

        public InteractiveShellTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task RunAsync_ViewConfiguration_PrintsConfiguredGames()
        {
            var configProvider = new FakeConfigProvider(new Dictionary<string, GameConfig>
            {
                ["eldenring.exe"] = new GameConfig { Name = "eldenring.exe", Alias = "Elden Ring", IsEnabled = true, HDREnabled = true },
                ["hades.exe"] = new GameConfig { Name = "hades.exe", Alias = "Hades", IsEnabled = false, HDREnabled = false }
            },
            new AppConfig { ProcessMonitorType = ProcessMonitorType.ETW },
            configPath: "C:/configs/gamehelper.yml");

            await using var host = CreateHost(configProvider);
            var console = CreateConsole();
            var script = new InteractiveScript()
                .Enqueue("Configuration")
                .Enqueue("View")
                .Enqueue("Back")
                .Enqueue("Exit");

            var shell = new InteractiveShell(host, new ParsedArguments { EnableDebug = true }, console, script);
            await shell.RunAsync();

            var snapshot = console.Output.ToString();
            _output.WriteLine("[Configuration Snapshot]\n" + snapshot);

            Assert.Contains("eldenring.exe", snapshot);
            Assert.Contains("Hades", snapshot);
            Assert.Contains("ETW", snapshot);
        }

        [Fact]
        public async Task RunAsync_AddGame_PersistsNewEntry()
        {
            var configProvider = new FakeConfigProvider(new Dictionary<string, GameConfig>
            {
                ["witcher3.exe"] = new GameConfig { Name = "witcher3.exe", Alias = "Witcher 3", IsEnabled = true, HDREnabled = true }
            },
            new AppConfig { ProcessMonitorType = ProcessMonitorType.WMI },
            configPath: "C:/configs/gamehelper.yml");

            await using var host = CreateHost(configProvider);
            var console = CreateConsole();
            var script = new InteractiveScript()
                .Enqueue("Configuration")
                .Enqueue("Add")
                .Enqueue("celeste.exe")
                .Enqueue("Celeste")
                .Enqueue("启用")
                .Enqueue("自动开启 HDR")
                .Enqueue("Back")
                .Enqueue("Exit");

            var shell = new InteractiveShell(host, new ParsedArguments(), console, script);
            await shell.RunAsync();

            var snapshot = console.Output.ToString();
            _output.WriteLine("[Add Snapshot]\n" + snapshot);

            var updated = configProvider.Load();
            Assert.True(updated.TryGetValue("celeste.exe", out var entry));
            Assert.Equal("Celeste", entry.Alias);
            Assert.True(entry.IsEnabled);
            Assert.True(entry.HDREnabled);
        }

        [Fact]
        public async Task RunAsync_ShowStatistics_PrintsAggregatedTotals()
        {
            using var scope = new AppDataScope();
            scope.PreparePlaytimeCsv("GameOne", 90, "GameTwo", 45);

            var configProvider = new FakeConfigProvider(new Dictionary<string, GameConfig>
            {
                ["GameOne"] = new GameConfig { Name = "GameOne", Alias = "冒险一号", IsEnabled = true, HDREnabled = true },
                ["GameTwo"] = new GameConfig { Name = "GameTwo", Alias = "试炼二号", IsEnabled = true, HDREnabled = false }
            },
            new AppConfig { ProcessMonitorType = ProcessMonitorType.ETW },
            configPath: scope.ConfigPath);

            await using var host = CreateHost(configProvider);
            var console = CreateConsole();
            var script = new InteractiveScript()
                .Enqueue("Statistics")
                .Enqueue(string.Empty)
                .Enqueue(string.Empty)
                .Enqueue("Exit");

            var shell = new InteractiveShell(host, new ParsedArguments(), console, script);
            await shell.RunAsync();

            var snapshot = console.Output.ToString();
            _output.WriteLine("[Statistics Snapshot]\n" + snapshot);

            Assert.Contains("冒险一号", snapshot);
            Assert.Contains("TOTAL", snapshot);
        }

        [Fact]
        public async Task RunAsync_LaunchMonitor_ShowsSessionSummary()
        {
            using var scope = new AppDataScope();
            var startHistory = new DateTime(2024, 2, 1, 19, 0, 0, DateTimeKind.Unspecified);
            scope.WritePlaytimeCsv(("eldenring.exe", startHistory, startHistory.AddMinutes(60), 60));

            var configProvider = new FakeConfigProvider(new Dictionary<string, GameConfig>
            {
                ["eldenring.exe"] = new GameConfig { Name = "eldenring.exe", Alias = "艾尔登法环", IsEnabled = true, HDREnabled = true }
            },
            new AppConfig { ProcessMonitorType = ProcessMonitorType.WMI },
            configPath: scope.ConfigPath);

            await using var host = CreateHost(configProvider);
            var console = CreateConsole();
            var script = new InteractiveScript()
                .Enqueue("Monitor")
                .Enqueue("开始监控");

            var sessionStart = new DateTime(2024, 2, 2, 21, 15, 0, DateTimeKind.Unspecified);
            async Task HostRunner(IHost _)
            {
                scope.AppendPlaytimeSession("eldenring.exe", sessionStart, sessionStart.AddMinutes(45), 45);
                await Task.CompletedTask;
            }

            var shell = new InteractiveShell(host, new ParsedArguments(), console, script, HostRunner);
            await shell.RunAsync();

            var snapshot = console.Output.ToString();
            _output.WriteLine("[Monitor Snapshot]\n" + snapshot);

            Assert.Contains("历史记录预览", snapshot);
            Assert.Contains("2024-02-02 22:00", snapshot); // 21:15 + 45 min
            Assert.Contains("45 min", snapshot);
            Assert.Contains("TOTAL", snapshot);
            Assert.Contains("本次共计 1 次游戏结束", snapshot);
        }

        private static TestConsole CreateConsole()
        {
            var console = new TestConsole();
            console.Profile.Capabilities.Ansi = false;
            console.Profile.Width = 120;
            return console;
        }

        private static AsyncDisposableHost CreateHost(FakeConfigProvider configProvider)
        {
            var services = new ServiceCollection()
                .AddSingleton<IConfigProvider>(configProvider)
                .AddSingleton<IAppConfigProvider>(configProvider)
                .BuildServiceProvider();

            return new AsyncDisposableHost(services);
        }

        private sealed class AsyncDisposableHost : IHost, IAsyncDisposable
        {
            private readonly ServiceProvider _provider;

            public AsyncDisposableHost(ServiceProvider provider)
            {
                _provider = provider;
            }

            public IServiceProvider Services => _provider;

            public void Dispose()
            {
                _provider.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                return _provider.DisposeAsync();
            }

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class FakeConfigProvider : IConfigProvider, IAppConfigProvider, IConfigPathProvider
        {
            private readonly object _lock = new();
            private Dictionary<string, GameConfig> _configs;
            private AppConfig _appConfig;

            public FakeConfigProvider(Dictionary<string, GameConfig> initial, AppConfig appConfig, string configPath)
            {
                _configs = new Dictionary<string, GameConfig>(initial, StringComparer.OrdinalIgnoreCase);
                _appConfig = appConfig;
                ConfigPath = configPath;
            }

            public string ConfigPath { get; }

            public IReadOnlyDictionary<string, GameConfig> Load()
            {
                lock (_lock)
                {
                    return new Dictionary<string, GameConfig>(_configs, StringComparer.OrdinalIgnoreCase);
                }
            }

            public void Save(IReadOnlyDictionary<string, GameConfig> configs)
            {
                lock (_lock)
                {
                    _configs = new Dictionary<string, GameConfig>(configs, StringComparer.OrdinalIgnoreCase);
                }
            }

            public AppConfig LoadAppConfig()
            {
                lock (_lock)
                {
                    return new AppConfig
                    {
                        Games = _configs.Values.Select(v => new GameConfig
                        {
                            Name = v.Name,
                            Alias = v.Alias,
                            IsEnabled = v.IsEnabled,
                            HDREnabled = v.HDREnabled
                        }).ToList(),
                        ProcessMonitorType = _appConfig.ProcessMonitorType
                    };
                }
            }

            public void SaveAppConfig(AppConfig appConfig)
            {
                lock (_lock)
                {
                    _appConfig = appConfig;
                }
            }
        }

        private sealed class AppDataScope : IDisposable
        {
            private readonly string _path;
            private readonly string? _originalAppData;
            private readonly string? _originalXdg;

            public AppDataScope()
            {
                _path = Path.Combine(Path.GetTempPath(), "gh-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(_path, "GameHelper"));

                _originalAppData = Environment.GetEnvironmentVariable("APPDATA");
                _originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                Environment.SetEnvironmentVariable("APPDATA", _path);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _path);
            }

            public string ConfigPath => Path.Combine(_path, "GameHelper", "config.yml");

            public void PreparePlaytimeCsv(string gameA, int minutesA, string gameB, int minutesB)
            {
                var now = DateTime.UtcNow;
                WritePlaytimeCsv(
                    (gameA, now, now.AddMinutes(30), minutesA),
                    (gameB, now.AddMinutes(60), now.AddMinutes(90), minutesB));
            }

            public void WritePlaytimeCsv(params (string Game, DateTime Start, DateTime End, long Minutes)[] sessions)
            {
                var file = Path.Combine(_path, "GameHelper", "playtime.csv");
                var lines = new List<string> { "GameName,StartTime,EndTime,DurationMinutes" };
                lines.AddRange(sessions.Select(s => $"{s.Game},{s.Start:o},{s.End:o},{s.Minutes}"));
                File.WriteAllLines(file, lines);
            }

            public void AppendPlaytimeSession(string game, DateTime start, DateTime end, long minutes)
            {
                var file = Path.Combine(_path, "GameHelper", "playtime.csv");
                if (!File.Exists(file))
                {
                    WritePlaytimeCsv((game, start, end, minutes));
                    return;
                }

                File.AppendAllLines(file, new[] { $"{game},{start:o},{end:o},{minutes}" });
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("APPDATA", _originalAppData);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalXdg);

                try
                {
                    if (Directory.Exists(_path))
                    {
                        Directory.Delete(_path, true);
                    }
                }
                catch
                {
                    // Ignore cleanup failures in tests.
                }
            }
        }
    }
}
