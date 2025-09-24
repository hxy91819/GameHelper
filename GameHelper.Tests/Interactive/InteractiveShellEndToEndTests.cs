using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace GameHelper.Tests.Interactive
{
    public class InteractiveShellEndToEndTests
    {
        private readonly ITestOutputHelper _output;

        public InteractiveShellEndToEndTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task InteractiveShell_ViaPseudoTerminal_PrintsConfigurationTable()
        {
            if (!OperatingSystem.IsLinux())
            {
                _output.WriteLine("Skipping interactive e2e test because the 'script' utility is only available on Linux environments.");
                return;
            }

            var scriptCommand = FindScriptCommandPath();
            if (scriptCommand is null)
            {
                _output.WriteLine("Skipping interactive e2e test because the 'script' command is not available.");
                return;
            }

            var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var consoleProject = Path.Combine(solutionRoot, "GameHelper.ConsoleHost", "GameHelper.ConsoleHost.csproj");

            var tempRoot = Path.Combine(Path.GetTempPath(), "gh-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var configPath = Path.Combine(tempRoot, "config.yml");
            await File.WriteAllTextAsync(configPath, GetSampleConfig());

            try
            {
                var command = $"dotnet run --no-build --project \"{consoleProject}\" -- --config \"{configPath}\" --monitor-dry-run --interactive";
                var psi = new ProcessStartInfo
                {
                    FileName = scriptCommand!,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    WorkingDirectory = solutionRoot,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                psi.ArgumentList.Add("-q");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
                psi.ArgumentList.Add("/dev/null");

                psi.Environment["DOTNET_NOLOGO"] = "1";
                psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
                psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

                var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
                if (!string.IsNullOrEmpty(dotnetRoot))
                {
                    psi.Environment["DOTNET_ROOT"] = dotnetRoot;
                }

                var path = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(path))
                {
                    psi.Environment["PATH"] = path;
                }

                using var process = new Process { StartInfo = psi };

                process.Start();

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await process.StandardInput.WriteLineAsync("2");
                await process.StandardInput.WriteLineAsync("1");
                await process.StandardInput.WriteLineAsync("5");
                await process.StandardInput.WriteLineAsync("1");
                await process.StandardInput.WriteLineAsync("Q");
                await process.StandardInput.WriteLineAsync("5");
                process.StandardInput.Close();

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    TryTerminate(process);
                    throw;
                }

                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                var combined = (stdout + stderr).Replace("\r", string.Empty);

                _output.WriteLine("[interactive output]\n" + combined);

                Assert.Equal(0, process.ExitCode);
                Assert.Contains("GameHelper", combined);
                Assert.Contains("当前配置", combined);
                Assert.Contains("sample.exe", combined);
                Assert.Contains("Sample Game", combined);
                Assert.Contains("Dry-run", combined, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("返回上一级", combined);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static string? FindScriptCommandPath()
        {
            string[] candidates =
            {
                Environment.GetEnvironmentVariable("SCRIPT") ?? string.Empty,
                "/usr/bin/script",
                "/bin/script"
            };

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string GetSampleConfig()
        {
            return "games:\n" +
                   "  - name: sample.exe\n" +
                   "    alias: Sample Game\n" +
                   "    isEnabled: true\n" +
                   "    hdrEnabled: true\n";
        }

        private static void TryTerminate(Process process)
        {
            if (process.HasExited)
            {
                return;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
