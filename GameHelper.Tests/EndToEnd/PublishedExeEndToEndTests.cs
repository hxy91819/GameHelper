using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace GameHelper.Tests.EndToEnd
{
    /// <summary>
    /// 端到端验证：发布产物以独立测试实例运行，通过 GAMEHELPER_DATA_DIR 重定向数据目录，
    /// 在不触碰用户真实数据、不与正在运行的主实例争抢单实例互斥的前提下完成实机验证。
    /// </summary>
    [Collection("EndToEndSequential")]
    public class PublishedExeEndToEndTests
    {
        private readonly ITestOutputHelper _output;

        public PublishedExeEndToEndTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static string? ResolvePublishExePath()
        {
            var exePath = Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "GameHelper.ConsoleHost",
                "bin", "Release", "net8.0-windows", "win-x64", "publish", "GameHelper.ConsoleHost.exe");
            exePath = Path.GetFullPath(exePath);
            return File.Exists(exePath) ? exePath : null;
        }

        [Fact]
        public void PublishedExe_WithRedirectedDataDir_RunsIsolatedTestInstance()
        {
            var exePath = ResolvePublishExePath();
            if (exePath is null)
            {
                // 发布产物不存在时跳过（CI/纯源码检查场景）；本地 workflow 要求先发布再跑此测试。
                return;
            }

            using var sandbox = new DataDirSandbox("gh-e2e");
            var configPath = Path.Combine(sandbox.GameHelperDir, "config.yml");
            File.WriteAllText(configPath, """
                monitor: ETW
                startup:
                  autoStartMonitor: false
                  launchOnStartup: false
                games:
                  - dataKey: e2e_game
                    executable: e2e.exe
                    displayName: "E2E 验证游戏"
                    enabled: true
                    hdr: false
                """);

            // 1. validate-config：验证发布产物可执行且读取的是沙盒配置
            var result = RunTestInstance(exePath, sandbox, "validate-config");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Config is valid.", result.Output);
            Assert.Contains(configPath, result.Output);

            // 2. stats：验证数据目录重定向生效（沙盒 playtime.csv 的数据出现在输出里，真实数据不出现）
            var playtimePath = Path.Combine(sandbox.GameHelperDir, "playtime.csv");
            var today = DateTime.Now;
            var csvHeader = "game,start_time,end_time,duration_minutes";
            var csvRow = $"e2e_game,{today.AddHours(-2):o},{today.AddHours(-1):o},60";
            File.WriteAllText(playtimePath, csvHeader + Environment.NewLine + csvRow + Environment.NewLine);

            result = RunTestInstance(exePath, sandbox, "stats");
            _output.WriteLine("[stats output]" + result.Output);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("E2E 验证游戏", result.Output);
            Assert.Contains("1 h", result.Output);

            // 3. 独立实例证据：测试实例能与主实例共存（互斥被环境变量关闭 + 数据目录隔离）
            Assert.DoesNotContain("检测到 GameHelper 已在运行", result.Output);
        }

        private static ProcessResult RunTestInstance(string exePath, DataDirSandbox sandbox, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 子进程经 ConsoleEncoding.EnsureUtf8 以 UTF-8 输出；父端不显式声明时
                // zh-CN 控制台会按 GBK 解码，中文断言就会出现乱码。
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.EnvironmentVariables["GAMEHELPER_DATA_DIR"] = sandbox.RootDir;
            startInfo.EnvironmentVariables["GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE"] = "1";

            using var process = Process.Start(startInfo)!;
            // Read synchronously to completion to avoid losing buffered output after WaitForExit.
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), $"Test instance '{arguments}' timed out.");
            return new ProcessResult(process.ExitCode, output);
        }

        private readonly record struct ProcessResult(int ExitCode, string Output);

        /// <summary>独立的数据沙盒：目录隔离，测试后自动清理，绝不触碰真实 AppData。</summary>
        private sealed class DataDirSandbox : IDisposable
        {
            public DataDirSandbox(string prefix)
            {
                RootDir = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
                GameHelperDir = Path.Combine(RootDir, "GameHelper");
                Directory.CreateDirectory(GameHelperDir);
            }

            public string RootDir { get; }

            public string GameHelperDir { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(RootDir, recursive: true);
                }
                catch
                {
                    // 沙盒清理失败不应掩盖测试结果。
                }
            }
        }
    }
}