using System.Diagnostics;

namespace GameHelper.Infrastructure.Startup;

/// <summary>Starts the existing runtime without allocating a console or transferring to Terminal.</summary>
public static class BackgroundRuntime
{
    public static ProcessStartInfo CreateStartInfo(string executable, string config)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Do not retain the caller's redirected handles after its startup probe exits.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("monitor");
        start.ArgumentList.Add("--background");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(config);
        return start;
    }
}
