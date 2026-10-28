using GameHelper.ConsoleHost;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Services;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Storage.Sqlite;
using Moq;
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
        File.WriteAllText(_configPath, "monitor: WMI\ngames: []\n");
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

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ISystemTrayService));
        AssertRegistered<IGameConfiguration>(services);
        AssertRegistered<IConfigPathProvider>(services);
        AssertRegistered<IProcessMonitor>(services);
        AssertRegistered<IHdrController>(services);
        AssertRegistered<IPlayTimeService>(services);
        AssertRegistered<IGameAutomationService>(services);
        AssertRegistered<IMonitorControlService>(services);
        AssertRegistered<ISettingsService>(services);
        AssertRegistered<IGameCatalogService>(services);
        AssertRegistered<IGameSpeedController>(services);
        AssertRegistered<IForegroundActivityProvider>(services);
        AssertRegistered<IBreakReminderNotifier>(services);
        AssertRegistered<IBreakReminderService>(services);
        AssertRegistered<IStatisticsService>(services);
        AssertRegistered<FileDropIntake>(services);
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

        var configProvider = host.Services.GetRequiredService<IGameConfiguration>();
        var pathProvider = Assert.IsAssignableFrom<IConfigPathProvider>(configProvider);
        var monitor = host.Services.GetRequiredService<IProcessMonitor>();

        Assert.Equal(_configPath, pathProvider.ConfigPath);
        Assert.IsType<NoOpProcessMonitor>(monitor);
    }

    [Fact]
    public void BackgroundTrayRegistrationFollowsPlatformAndSandboxPolicy()
    {
        var services = new ServiceCollection();
        ConsoleHostBootstrapper.ConfigureServices(services, new ParsedArguments { Background = true });
        var expected = OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMEHELPER_DATA_DIR"));
        Assert.Equal(expected, services.Any(descriptor => descriptor.ServiceType == typeof(ISystemTrayService)));
    }

    [Fact]
    public async Task ProductionLibraryRegistration_ReportIncludesScopedJournalAndRatings()
    {
        var services = new ServiceCollection(); services.AddLogging();
        ConsoleHostBootstrapper.ConfigureServices(services, new ParsedArguments { ConfigOverride = _configPath, MonitorDryRun = true });
        var clock = new ReportClock();
        services.AddSingleton(new SqliteConnectionFactory(_tempDir));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(Mock.Of<IGameAutomationService>());
        services.AddSingleton(Mock.Of<IGameLaunchCoordinator>());
        services.AddSingleton(Mock.Of<IMonitorControlService>());
        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<SessionStorage>();
        await storage.InitializeAsync(_configPath, []);
        var play = (IIdentifiedPlayTimeService)storage.PlayTime;
        var id = Guid.NewGuid();
        play.ResumeTracking("g", clock.GetLocalNow().DateTime.AddMinutes(-20), id, false);
        play.StopTracking("g");
        provider.GetRequiredService<SessionRatings>().Save(new(id, 4, "不进入模型", 0));
        var library = provider.GetRequiredService<IGameLibrary>();
        var response = await library.ExecuteAsync(new() { Operation = GameLibraryOperation.Report, ReportPeriod = new(2026) });
        Assert.True(response.Success); Assert.True(response.Report!.JournalIncluded);
        Assert.Equal(4d, response.Report.AverageRating); Assert.Equal(1, response.Report.RatingSamples);
        Assert.Equal(id, response.Report.HighestRatedSession!.SessionId);
    }
    private sealed class ReportClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
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
