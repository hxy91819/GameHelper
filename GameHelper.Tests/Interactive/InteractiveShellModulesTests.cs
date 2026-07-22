using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Interactive;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console.Testing;

namespace GameHelper.Tests.Interactive;

public sealed class InteractiveShellModulesTests
{
    [Fact]
    public void Create_ComposesInteractiveModulesAroundSharedPromptAndProviders()
    {
        using var host = CreateHost();
        var console = new TestConsole();
        var arguments = new ParsedArguments { MonitorDryRun = true };

        var modules = InteractiveShellModules.Create(host, arguments, console, script: null, monitorLoop: null);

        Assert.Same(console, modules.Console);
        Assert.Same(host.Services.GetRequiredService<IGameConfiguration>(), modules.GameConfiguration);
        Assert.NotNull(modules.PromptUI);
        Assert.NotNull(modules.MonitorUI);
        Assert.NotNull(modules.CatalogUI);
        Assert.NotNull(modules.SettingsUI);
        Assert.NotNull(modules.StatisticsUI);
        Assert.NotNull(modules.ToolsUI);
    }

    private static IHost CreateHost()
    {
        var provider = new FakeGameConfiguration();
        var services = new ServiceCollection()
            .AddSingleton<IGameConfiguration>(provider)
            .AddSingleton<IAutoStartManager, FakeAutoStartManager>()
            .AddSingleton<IGameCatalogService, GameCatalogService>()
            .AddSingleton<IMonitorControlService, FakeMonitorControlService>()
            .AddSingleton<IPlaytimeSnapshotProvider, FilePlaytimeSnapshotProvider>()
            .AddSingleton<IStatisticsService, StatisticsService>()
            .BuildServiceProvider();

        return new TestHost(services);
    }

    private sealed class TestHost : IHost
    {
        private readonly ServiceProvider _services;

        public TestHost(ServiceProvider services)
        {
            _services = services;
        }

        public IServiceProvider Services => _services;

        public void Dispose() => _services.Dispose();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeGameConfiguration : IGameConfiguration
    {
        private AppConfig _config = new() { Games = new List<GameConfig>() };

        public AppConfig Read() => _config;

        public AppConfig Change(Action<AppConfig> change)
        {
            change(_config);
            return _config;
        }
    }

    private sealed class FakeAutoStartManager : IAutoStartManager
    {
        public bool IsSupported => true;

        public bool IsEnabled() => false;

        public void SetEnabled(bool enabled)
        {
        }
    }

    private sealed class FakeMonitorControlService : IMonitorControlService
    {
        public bool IsRunning => false;

        public void Start()
        {
        }

        public void Stop()
        {
        }
    }
}
