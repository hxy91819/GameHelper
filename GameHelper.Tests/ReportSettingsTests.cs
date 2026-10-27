using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Core.Validation;
using GameHelper.Desktop;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests;

public sealed class ReportSettingsTests
{
    [Fact]
    public void Update_Report_RoundTripsAndUnrelatedSettingsPreserveIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gh-report-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.yml");
            File.WriteAllText(path, "monitor: ETW\ngames: []\n");
            var provider = new YamlConfigProvider(path);
            var service = new SettingsService(provider);
            var updated = service.Update(new() { Report = new() { Llm = new() { BaseUrl = "https://example.test/v1", Model = "test", ApiKey = "secret" } } });
            Assert.Equal("secret", updated.Report!.Llm!.ApiKey);
            Assert.Contains("report:", File.ReadAllText(path));
            Assert.DoesNotContain("isConfigured", File.ReadAllText(path));
            service.Update(new() { LaunchOnSystemStartup = true });
            Assert.Equal("test", provider.Read().Report!.Llm!.Model);
            Assert.Equal("secret", service.Get().Report!.Llm!.ApiKey);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void BuildReportSettings_Unconfigured_RemainsAbsentUnlessEdited()
    {
        var draft = new GlobalSettingsViewModel(new());
        Assert.Null(draft.BuildReportSettings(""));
        draft.ReportBaseUrl = "https://example.test/v1"; draft.ReportModel = "test";
        Assert.Equal("secret", draft.BuildReportSettings("secret")!.Llm!.ApiKey);
        var existing = new GlobalSettingsViewModel(new() { Report = new() { Llm = new() { BaseUrl = "https://example.test/v1", Model = "old" } } });
        existing.ReportBaseUrl = existing.ReportModel = "";
        Assert.False(existing.BuildReportSettings("")!.Llm!.IsConfigured);
    }

    [Theory]
    [InlineData("file:///tmp/private", "test")]
    [InlineData("https://user:secret@example.test/v1", "test")]
    [InlineData("https://example.test/v1?key=secret", "test")]
    [InlineData("https://example.test/v1", "")]
    public void Validate_Report_RejectsInvalidEndpointOrModel(string url, string model)
    {
        Assert.False(AppConfigValidator.ValidateGlobals(new() { Report = new() { Llm = new() { BaseUrl = url, Model = model } } }).IsValid);
    }
}
