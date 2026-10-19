using System;
using System.Diagnostics;
using System.IO;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GameHelper.Infrastructure.Startup
{
    /// <summary>
    /// Windows-specific implementation that toggles the HKCU\Software\Microsoft\Windows\CurrentVersion\Run value.
    /// </summary>
    public sealed class WindowsAutoStartManager : IAutoStartManager
    {
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        internal const string BackgroundArgument = "--minimized";

        private readonly ILogger<WindowsAutoStartManager> _logger;
        private readonly string _appName;
        private readonly string _executablePath;

        public WindowsAutoStartManager(ILogger<WindowsAutoStartManager> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appName = "GameHelper";
            _executablePath = ResolveExecutablePath();
        }

        public bool IsSupported => OperatingSystem.IsWindows();

        public bool IsEnabled()
        {
            if (!IsSupported)
            {
                return false;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                var value = key?.GetValue(_appName) as string;
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                return IsCurrentRegistration(value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read auto-start registry value");
                return false;
            }
        }

        public void SetEnabled(bool enabled)
        {
            if (!IsSupported)
            {
                throw new PlatformNotSupportedException("Auto-start management is only supported on Windows.");
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key is null)
                {
                    throw new InvalidOperationException("Failed to open or create the Run registry key.");
                }

                if (enabled)
                {
                    var currentValue = key.GetValue(_appName) as string;
                    if (IsCurrentRegistration(currentValue))
                    {
                        return;
                    }

                    key.SetValue(_appName, BuildRegistrationValue());
                    _logger.LogInformation("已注册开机自启动：{Path}", _executablePath);
                }
                else
                {
                    if (key.GetValue(_appName) is null)
                    {
                        return;
                    }

                    key.DeleteValue(_appName, false);
                    _logger.LogInformation("已取消开机自启动");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update auto-start registry value");
                throw new InvalidOperationException("Failed to update auto-start registry value.", ex);
            }
        }

        /// <summary>
        /// 期望的注册值：可执行文件 + --minimized，让开机自启把窗口收进任务栏而不是抢焦点。
        /// </summary>
        private string BuildRegistrationValue() => $"{QuotePath(_executablePath)} {BackgroundArgument}";

        private bool IsCurrentRegistration(string? value)
            => !string.IsNullOrWhiteSpace(value)
                && HasBackgroundArgument(value)
                && string.Equals(
                    NormalizePath(ExtractExecutablePath(value)),
                    NormalizePath(_executablePath),
                    StringComparison.OrdinalIgnoreCase);

        /// <summary>从注册表命令行里取出可执行文件部分，忽略引号与启动参数。</summary>
        internal static string ExtractExecutablePath(string commandLine)
        {
            var trimmed = commandLine.Trim();
            if (trimmed.StartsWith('"'))
            {
                var closing = trimmed.IndexOf('"', 1);
                return closing > 0 ? trimmed[1..closing] : trimmed;
            }

            var marker = " " + BackgroundArgument;
            var index = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return index > 0 ? trimmed[..index] : trimmed;
        }

        internal static bool HasBackgroundArgument(string commandLine)
            => commandLine.Contains(BackgroundArgument, StringComparison.OrdinalIgnoreCase);

        private static string ResolveExecutablePath()
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                return Environment.ProcessPath!;
            }

            using var process = Process.GetCurrentProcess();
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path!;
            }

            return Path.Combine(AppContext.BaseDirectory, "GameHelper.exe");
        }

        private static string QuotePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            return path.Contains(' ') ? $"\"{path}\"" : path;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            path = path.Trim();
            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            {
                path = path[1..^1];
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
