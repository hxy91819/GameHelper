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
        private const string BackgroundLaunchPrefix = "cmd.exe /c start \"\" /min ";

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
        /// 期望的注册值：经 cmd 的 start /min 拉起，可执行文件 + --minimized。
        /// start /min 用 SW_SHOWMINNOACTIVE 创建控制台，开机时连一次前台激活都不会发生；
        /// 直接把 exe 写进 Run 键会被资源管理器以正常窗口激活，无法做到零焦点。
        /// </summary>
        private string BuildRegistrationValue() => $"{BackgroundLaunchPrefix}{QuotePath(_executablePath)} {BackgroundArgument}";

        private bool IsCurrentRegistration(string? value) => IsRegistrationFor(value, _executablePath);

        /// <summary>判断注册值是否是"用 start /min 拉起指定可执行文件"的目标形态。</summary>
        internal static bool IsRegistrationFor(string? commandLine, string executablePath)
            => !string.IsNullOrWhiteSpace(commandLine)
                && commandLine.StartsWith(BackgroundLaunchPrefix, StringComparison.OrdinalIgnoreCase)
                && commandLine.Contains(executablePath, StringComparison.OrdinalIgnoreCase);

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
