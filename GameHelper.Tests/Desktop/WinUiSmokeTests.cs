using FlaUI.Core;
using FlaUI.UIA3;

namespace GameHelper.Tests.Desktop;

public sealed class WinUiSmokeTests
{
    [WindowsOnlyFact]
    [Trait("DesktopSuite", "Smoke")]
    public void Shell_ShouldExposeCriticalAutomationIds()
    {
        var appPath = Environment.GetEnvironmentVariable("GAMEHELPER_WINUI_EXE");
        if (string.IsNullOrWhiteSpace(appPath) || !File.Exists(appPath))
        {
            return;
        }

        RetryHelper.Execute(() =>
        {
            using var app = Application.Launch(appPath);
            using var automation = new UIA3Automation();

            var timeoutAt = DateTime.UtcNow + DesktopTestPolicy.LaunchTimeout;
            FlaUI.Core.AutomationElements.Window? mainWindow = null;
            while (DateTime.UtcNow < timeoutAt)
            {
                mainWindow = app.GetMainWindow(automation);
                if (mainWindow is not null)
                {
                    break;
                }

                Thread.Sleep(250);
            }

            Assert.NotNull(mainWindow);

            var requiredIds = new[]
            {
                "Shell_TabView",
                "Shell_ToggleMonitorButton",
                "Settings_SaveButton",
                "Games_ListView",
                "Stats_ListView"
            };

            foreach (var automationId in requiredIds)
            {
                var element = mainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                if (element is null)
                {
                    var artifactPath = Path.Combine(
                        DesktopTestPolicy.ArtifactDirectory,
                        $"missing-automationid-{automationId}-{DateTime.UtcNow:yyyyMMddHHmmss}.txt");
                    File.WriteAllText(artifactPath, $"Missing AutomationId: {automationId}");
                }

                Assert.NotNull(element);
            }
        }, DesktopTestPolicy.MaxRetries);
    }
}
