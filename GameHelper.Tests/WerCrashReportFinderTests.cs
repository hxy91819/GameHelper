using GameHelper.Core.Utilities;
using Xunit;

namespace GameHelper.Tests;

public sealed class WerCrashReportFinderTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "GameHelperTests",
        Guid.NewGuid().ToString("N"));

    private string CreateReportDirectory(string executableName, DateTime? creationTimeUtc = null)
    {
        var archive = Path.Combine(
            _tempRoot, "Microsoft", "Windows", "WER", "ReportArchive");
        var directory = Directory.CreateDirectory(Path.Combine(
            archive,
            $"AppCrash_{executableName}_ce7185d1_{Guid.NewGuid():N}"));
        if (creationTimeUtc.HasValue)
        {
            Directory.SetCreationTimeUtc(directory.FullName, creationTimeUtc.Value);
        }

        return directory.FullName;
    }

    [Fact]
    public void FindLatest_WithMissingArchive_ReturnsNull()
    {
        Assert.Null(WerCrashReportFinder.FindLatest(_tempRoot, "mygame.exe", DateTime.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public void FindLatest_WithRecentReport_ReturnsReportIdentifier()
    {
        Directory.CreateDirectory(_tempRoot);
        var reportDir = CreateReportDirectory("mygame.exe");
        var crashTime = DateTime.UtcNow.AddMinutes(-1);
        File.WriteAllText(
            Path.Combine(reportDir, "Report.wer"),
            "Version=3\r\nEventTime=" + crashTime.ToFileTimeUtc().ToString() + "\r\nReportIdentifier=68c73372-abcd-1234\r\nResponse.BucketId=1300259702256679122\r\n");

        var found = WerCrashReportFinder.FindLatest(
            _tempRoot,
            "mygame.exe",
            DateTime.UtcNow.AddMinutes(-30));

        Assert.NotNull(found);
        Assert.Contains("ReportIdentifier=68c73372-abcd-1234", found, StringComparison.Ordinal);
    }

    [Fact]
    public void FindLatest_WithoutReportTextFile_ReturnsDirectoryPath()
    {
        Directory.CreateDirectory(_tempRoot);
        var reportDir = CreateReportDirectory("plain.exe");
        File.WriteAllText(Path.Combine(reportDir, "sysdata.xml"), "<xml/>");

        var found = WerCrashReportFinder.FindLatest(
            _tempRoot,
            "plain.exe",
            DateTime.UtcNow.AddMinutes(-30));

        Assert.NotNull(found);
        Assert.Contains("AppCrash_plain", found, StringComparison.Ordinal);
    }

    [Fact]
    public void FindLatest_WithReportBeforeAfterUtc_ReturnsNull()
    {
        Directory.CreateDirectory(_tempRoot);
        CreateReportDirectory("oldgame.exe", DateTime.UtcNow.AddHours(-2));

        Assert.Null(WerCrashReportFinder.FindLatest(
            _tempRoot,
            "oldgame.exe",
            DateTime.UtcNow.AddHours(-1)));
    }

    [Fact]
    public void FindLatest_ArchivedLateButCrashedBeforeAfterUtc_ReturnsNull()
    {
        Directory.CreateDirectory(_tempRoot);

        // WER archived the report just now, but the crash itself happened two hours ago (the
        // previous run). Its archive time must not make it look like this run's crash.
        var reportDir = CreateReportDirectory("late.exe");
        var crashTime = DateTime.UtcNow.AddHours(-2);
        File.WriteAllText(
            Path.Combine(reportDir, "Report.wer"),
            "Version=3\r\nEventTime=" + crashTime.ToFileTimeUtc().ToString() + "\r\nReportIdentifier=stale-report\r\n");

        Assert.Null(WerCrashReportFinder.FindLatest(
            _tempRoot,
            "late.exe",
            DateTime.UtcNow.AddMinutes(-30)));
    }

    [Fact]
    public void FindLatest_ReturnsTheNewestMatchingReport()
    {
        Directory.CreateDirectory(_tempRoot);
        CreateReportDirectory("multi.exe", DateTime.UtcNow.AddMinutes(-20));
        var newer = CreateReportDirectory("multi.exe", DateTime.UtcNow.AddMinutes(-10));
        File.WriteAllText(
            Path.Combine(newer, "Report.wer"),
            "ReportIdentifier=newest-report\r\n");

        var found = WerCrashReportFinder.FindLatest(
            _tempRoot,
            "multi.exe",
            DateTime.UtcNow.AddMinutes(-60));

        Assert.NotNull(found);
        Assert.Contains("newest-report", found, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}