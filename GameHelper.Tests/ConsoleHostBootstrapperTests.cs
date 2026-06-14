using GameHelper.ConsoleHost;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameHelper.Tests;

public sealed class ConsoleHostBootstrapperTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConsoleHostBootstrapperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelperTests_Bootstrapper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.yml");
        File.WriteAllText(_configPath, "processMonitorType: WMI\ngames: []\n");
    }

    [Fact]
    public void ConfigureServices_RegistersCoreShellAndInfrastructureSeams()
    {
        var services = new ServiceCollection();

        ConsoleHostBootstrapper.ConfigureServices(services, new ParsedArguments
        {
            ConfigOverride = _configPath,
            MonitorDryRun = true
        });

        AssertRegistered<IConfigProvider>(services);
        AssertRegistered<IAppConfigProvider>(services);
        AssertRegistered<IProcessMonitor>(services);
        AssertRegistered<IHdrController>(services);
        AssertRegistered<IPlayTimeService>(services);
        AssertRegistered<IGameAutomationService>(services);
        AssertRegistered<IMonitorControlService>(services);
        AssertRegistered<ISettingsService>(services);
        AssertRegistered<IGameCatalogService>(services);
        AssertRegistered<IStatisticsService>(services);
        AssertRegistered<IFileDropRequestHandler>(services);
        AssertRegistered<IFileDropIpcServer>(services);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void CreateBuilder_WithConfigOverrideAndDryRun_UsesYamlConfigAndNoOpMonitor()
    {
        using var host = ConsoleHostBootstrapper.CreateBuilder(Array.Empty<string>(), new ParsedArguments
        {
            ConfigOverride = _configPath,
            MonitorDryRun = true
        }).Build();

        var configProvider = host.Services.GetRequiredService<IConfigProvider>();
        var appConfigProvider = host.Services.GetRequiredService<IAppConfigProvider>();
        var pathProvider = Assert.IsAssignableFrom<IConfigPathProvider>(configProvider);
        var monitor = host.Services.GetRequiredService<IProcessMonitor>();

        Assert.Same(configProvider, appConfigProvider);
        Assert.Equal(_configPath, pathProvider.ConfigPath);
        Assert.IsType<NoOpProcessMonitor>(monitor);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void AssertRegistered<TService>(IServiceCollection services)
    {
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(TService));
    }
}
