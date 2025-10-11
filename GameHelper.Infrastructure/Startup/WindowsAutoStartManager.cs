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

                var normalizedValue = NormalizePath(value);
                var normalizedExecutable = NormalizePath(_executablePath);
                return string.Equals(normalizedValue, normalizedExecutable, StringComparison.OrdinalIgnoreCase);
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
                    key.SetValue(_appName, QuotePath(_executablePath));
                    _logger.LogInformation("已注册开机自启动：{Path}", _executablePath);
                }
                else
                {
                    key.DeleteValue(_appName, false);
                    _logger.LogInformation("已取消开机自启动");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update auto-start registry value");
                throw;
            }
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
