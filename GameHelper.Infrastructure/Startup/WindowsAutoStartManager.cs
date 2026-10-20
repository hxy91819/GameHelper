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

        private string BuildRegistrationValue() => QuotePath(GetStartupExecutable(_executablePath));

        private bool IsCurrentRegistration(string? value) => IsRegistrationFor(value, GetStartupExecutable(_executablePath));

        private static string GetStartupExecutable(string executablePath)
        {
            var launcher = Path.Combine(Path.GetDirectoryName(executablePath) ?? string.Empty, "GameHelper.Launcher.exe");
            return File.Exists(launcher) ? launcher : executablePath;
        }

        /// <summary>判断注册值是否指向指定可执行文件（允许引号与历史遗留的启动参数）。</summary>
        internal static bool IsRegistrationFor(string? commandLine, string executablePath)
        {
            if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            var trimmed = commandLine.Trim();
            if (trimmed.StartsWith('"'))
            {
                var closing = trimmed.IndexOf('"', 1);
                if (closing > 0)
                {
                    trimmed = trimmed[1..closing];
                }
            }
            else
            {
                var space = trimmed.IndexOf(' ');
                if (space > 0)
                {
                    trimmed = trimmed[..space];
                }
            }

            return string.Equals(
                NormalizePath(trimmed),
                NormalizePath(executablePath),
                StringComparison.OrdinalIgnoreCase);
        }

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
