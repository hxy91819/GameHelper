using System;
// [DECISION] Explicit using required: .csproj removes System.Drawing global using to avoid
// Spectre.Console Color/Panel ambiguity. Do NOT remove this — TrayIconService depends on
// SystemIcons which lives in System.Drawing.
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using GameHelper.ConsoleHost.Utilities;
using Microsoft.Extensions.Hosting;

namespace GameHelper.ConsoleHost.Services
{
    public sealed class TrayIconService : IDisposable
    {
        private readonly IHost _host;
        private NotifyIcon? _notifyIcon;
        private Thread? _messageThread;
        private volatile bool _disposed;
        private volatile bool _exitRequested;
        private WindowsFormsSynchronizationContext? _uiSyncContext;

        public event Action? ExitRequested;

        public TrayIconService(IHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public void Initialize()
        {
            if (!OperatingSystem.IsWindows()) return;

            using var ready = new ManualResetEventSlim(false);

            _messageThread = new Thread(() => RunMessageLoop(ready))
            {
                Name = "TrayIconThread",
                IsBackground = true
            };
            _messageThread.SetApartmentState(ApartmentState.STA);
            _messageThread.Start();

            ready.Wait();
        }

        public void ShowWindow()
        {
            if (_disposed) return;
            PostToUIThread(DoShowWindow);
        }

        public void HideWindow()
        {
            if (_disposed) return;
            PostToUIThread(DoHideWindow);
        }

        private void DoShowWindow()
        {
            ConsoleWindowHelper.Show();
        }

        private void DoHideWindow()
        {
            ConsoleWindowHelper.Hide();
        }

        private void ToggleWindow()
        {
            var hwnd = ConsoleWindowHelper.GetHandle();
            if (hwnd == IntPtr.Zero) return;

            if (ConsoleWindowHelper.IsWindowVisible(hwnd))
            {
                ConsoleWindowHelper.Hide();
                UpdateToggleMenuItem(false);
            }
            else
            {
                ConsoleWindowHelper.Show();
                UpdateToggleMenuItem(true);
            }
        }

        private void UpdateToggleMenuItem(bool visible)
        {
            if (_notifyIcon?.ContextMenuStrip == null || _disposed) return;

            var items = _notifyIcon.ContextMenuStrip.Items;
            if (items.Count > 0 && items[0] is ToolStripMenuItem toggleItem)
            {
                toggleItem.Text = visible ? "隐藏窗口" : "显示窗口";
            }
        }

        private void RunMessageLoop(ManualResetEventSlim ready)
        {
            _uiSyncContext = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_uiSyncContext);

            _notifyIcon = CreateNotifyIcon();
            ConsoleWindowHelper.InstallCloseHandler(OnConsoleClose);

            ready.Set();

            try
            {
                Application.Run();
            }
            catch
            {
            }
            finally
            {
                Cleanup();
            }
        }

        private NotifyIcon CreateNotifyIcon()
        {
            var icon = CreateIcon();

            // [DECISION] Replace the empty stubs the static factory installs
            // with handlers that drive the instance's lifecycle. The static
            // helper stays menu-shape-only so the smoke test can build the
            // same menu without subscribing to anything.
            var items = icon.ContextMenuStrip!.Items;
            ((ToolStripMenuItem)items[0]!).Click -= (_, __) => { };
            ((ToolStripMenuItem)items[0]!).Click += OnToggleClick;
            ((ToolStripMenuItem)items[2]!).Click -= (_, __) => { };
            ((ToolStripMenuItem)items[2]!).Click += OnExitClick;
            icon.DoubleClick -= (_, __) => { };
            icon.DoubleClick += OnDoubleClick;

            return icon;
        }

        /// <summary>
        /// Builds a <see cref="NotifyIcon"/> with the same icon, tooltip, and
        /// context menu the tray service uses. Exposed so the smoke test (and
        /// future tests) can drive the exact same icon-construction path that
        /// real users see, without instantiating the full service.
        /// </summary>
        public static NotifyIcon CreateIcon()
        {
            var icon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "GameHelper",
                Visible = true
            };

            var contextMenu = new ContextMenuStrip();

            var toggleItem = new ToolStripMenuItem("显示窗口", null, (_, __) => { });
            contextMenu.Items.Add(toggleItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(new ToolStripMenuItem("退出", null, (_, __) => { }));

            icon.ContextMenuStrip = contextMenu;
            icon.DoubleClick += (_, __) => { };

            return icon;
        }

        private void OnToggleClick(object? sender, EventArgs e) => ToggleWindow();

        private void OnDoubleClick(object? sender, EventArgs e) => ToggleWindow();

        private void OnExitClick(object? sender, EventArgs e) => RequestExit();

        private bool OnConsoleClose()
        {
            // User pressed X on console window — hide to tray instead of exiting
            ConsoleWindowHelper.Hide();
            UpdateToggleMenuItem(false);
            return true;
        }

        private void RequestExit()
        {
            if (_exitRequested) return;
            _exitRequested = true;

            ExitRequested?.Invoke();

            // [DECISION] Cleanup+Application.Exit run synchronously on the UI thread first to
            // guarantee the NotifyIcon is removed and the message loop stops. Host shutdown is
            // fire-and-forget on purpose: blocking the UI thread for _host.StopAsync() risks
            // deadlock (STA thread hosting WinForms controls), and the host will be stopped by
            // Program.cs's Environment.Exit(0) fallback after 3 seconds if it hasn't finished.
            Cleanup();
            Application.Exit();

            _ = _host.StopAsync(TimeSpan.FromSeconds(10));
        }

        private void PostToUIThread(Action action)
        {
            _uiSyncContext?.Post(_ =>
            {
                if (!_disposed)
                {
                    try { action(); }
                    catch { }
                }
            }, null);
        }

        private void Cleanup()
        {
            try { ConsoleWindowHelper.RemoveCloseHandler(); } catch { }

            try
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.DoubleClick -= OnDoubleClick;
                    _notifyIcon.Visible = false;
                    // [DECISION] Do NOT dispose _notifyIcon.Icon — SystemIcons.Application is a
                    // shared system resource; disposing it corrupts other consumers.
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var posted = false;
            try
            {
                if (_uiSyncContext != null && _messageThread != null && _messageThread.IsAlive)
                {
                    _uiSyncContext.Post(_ =>
                    {
                        try { Cleanup(); } catch { }
                        try { Application.Exit(); } catch { }
                    }, null);
                    posted = true;
                }
            }
            catch { }

            if (!posted)
            {
                Cleanup();
            }
        }
    }
}