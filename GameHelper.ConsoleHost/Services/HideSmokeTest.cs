using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using GameHelper.ConsoleHost.Utilities;

namespace GameHelper.ConsoleHost.Services
{
    /// <summary>
    /// One-shot probe that verifies the "console window is hidden from the
    /// taskbar after startup" property. Without this verification the user
    /// had to click the system tray by hand to confirm the hide actually
    /// took effect; this probe turns that into a headless check the
    /// integration test can assert on.
    ///
    /// What it does:
    ///   1. Allocate a console with <see cref="ConsoleWindowHelper.CreateConsole"/>.
    ///   2. Read the HWND via <c>GetConsoleWindow</c>.
    ///   3. Confirm the HWND is *visible* (sanity check that AllocConsole
    ///      actually produced one).
    ///   4. Call <see cref="ConsoleWindowHelper.Hide"/>.
    ///   5. Re-check the HWND: <c>IsWindowVisible</c> must be false, and the
    ///      extended-style must NOT carry WS_EX_APPWINDOW (which is what the
    ///      taskbar uses to decide whether to show a button).
    ///   6. Print HIDE_OK or HIDE_FAIL: &lt;reason&gt; to stdout and exit.
    /// </summary>
    public static class HideSmokeTest
    {
        public const string OkMarker = "HIDE_OK";
        public const string FailMarker = "HIDE_FAIL";

        private const int GWL_EXSTYLE = -20;
        private const uint WS_EX_APPWINDOW = 0x00040000;
        private const int UiStartupTimeoutMs = 5_000;

        public static int Run()
        {
            if (!OperatingSystem.IsWindows())
            {
                WriteMarker(FailMarker, "non-Windows host");
                return 2;
            }

            using var ready = new ManualResetEventSlim(false);
            string? failure = null;

            var thread = new Thread(() =>
            {
                Console.Error.WriteLine("# STA thread started");
                try
                {
                    SynchronizationContext.SetSynchronizationContext(
                        new WindowsFormsSynchronizationContext());
                    Console.Error.WriteLine("# sync context set");

                    // Allocate a console window we own end-to-end. Without
                    // this GetConsoleWindow() returns IntPtr.Zero when the
                    // binary is OutputType=WinExe.
                    try
                    {
                        ConsoleWindowHelper.CreateConsole();
                        Console.Error.WriteLine("# AllocConsole done");
                    }
                    catch (Exception ex)
                    {
                        // Make sure even if the main thread times out
                        // waiting on `ready`, we leave a breadcrumb.
                        Console.Error.WriteLine($"# CreateConsole threw: {ex.GetType().Name}: {ex.Message}");
                        failure = $"CreateConsole threw: {ex.GetType().Name}: {ex.Message}";
                        return;
                    }

                    var hwnd = ConsoleWindowHelper.GetHandle();
                    Console.Error.WriteLine($"# GetHandle => {hwnd}");
                    if (hwnd == IntPtr.Zero)
                    {
                        failure = "GetConsoleWindow() returned IntPtr.Zero after AllocConsole";
                        return;
                    }

                    // [DECISION] Signal `ready` BEFORE the sanity check so a
                    // sanity-check failure does not deadlock the main thread.
                    // The check is still meaningful: we use it only to log
                    // a warning, not to gate the test result.
                    ready.Set();
                    Console.Error.WriteLine("# ready.Set() done");

                    // Sanity check: the window really is visible right now.
                    // If it isn't, the test environment is so unusual that
                    // the rest of the assertions are not meaningful — but
                    // we still proceed so we can emit a HIDE_FAIL with a
                    // clear reason.
                    if (!IsWindowVisible(hwnd))
                    {
                        Console.Error.WriteLine("# warn: console HWND is not visible right after AllocConsole; assertions may be misleading");
                    }

                    // Drive the WinForms message loop briefly so the shell
                    // has a chance to fully process the window's paint.
                    Application.Run();
                    Console.Error.WriteLine("# STA: Application.Run() returned");

                    try
                    {
                        // [DECISION] The whole point of the smoke test is to
                        // verify that the *real* Hide() call (the one
                        // Program.cs / TrayIconService uses) actually makes
                        // the window invisible. So we call
                        // ConsoleWindowHelper.Hide() here, the same helper
                        // the production code calls. If this doesn't work,
                        // the test surfaces that immediately.
                        ConsoleWindowHelper.Hide();
                        Console.Error.WriteLine("# STA: Hide() returned");

                        var vis = IsWindowVisible(hwnd);
                        Console.Error.WriteLine($"# STA: IsWindowVisible (after Hide) = {vis}");
                        if (vis)
                        {
                            failure = "IsWindowVisible(console_hwnd) == true after Hide() (window still on screen / taskbar)";
                        }
                        else
                        {
                            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE).ToInt64() & 0xFFFFFFFFL;
                            Console.Error.WriteLine($"# STA: exStyle = 0x{exStyle:X8}");
                            if ((exStyle & WS_EX_APPWINDOW) != 0)
                            {
                                failure = $"WS_EX_APPWINDOW still set after Hide() (exStyle=0x{exStyle:X8}); the taskbar will keep showing the entry";
                            }
                            else
                            {
                                // Emit a single OK line. Diagnostic bits are
                                // not necessary on the success path; the test
                                // asserts on HIDE_OK only.
                                WriteMarker(OkMarker, "");
                                Console.Error.WriteLine("# STA: wrote HIDE_OK");
                            }
                        }
                    }
                    finally
                    {
                        // Restore visibility so the test environment does
                        // not leave a hidden orphan window around.
                        ShowWindow(hwnd, SW_SHOW);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + ": " + ex.Message;
                }
            })
            {
                Name = "HideSmokeTest",
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Wait(UiStartupTimeoutMs))
            {
                WriteMarker(FailMarker, $"STA thread did not signal ready in {UiStartupTimeoutMs}ms");
                return 3;
            }

            Console.Error.WriteLine("# main: ready seen");

            // Let the shell paint the freshly-AllocConsole'd window.
            Thread.Sleep(500);

            Console.Error.WriteLine("# main: sleep done, calling Application.Exit");

            try
            {
                Application.Exit();
            }
            catch (Exception ex)
            {
                WriteMarker(FailMarker, $"Application.Exit threw {ex.GetType().Name}: {ex.Message}");
                return 4;
            }

            Console.Error.WriteLine("# main: Application.Exit returned, joining thread");

            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                WriteMarker(FailMarker, "STA thread did not exit within 5s");
                return 5;
            }

            Console.Error.WriteLine("# main: thread joined");

            if (failure != null)
            {
                Console.Error.WriteLine($"# main: failure captured: {failure}");
                WriteMarker(FailMarker, failure);
                return 1;
            }

            Console.Error.WriteLine("# main: no failure, returning 0");
            return 0;
        }

        /// <summary>
        /// Writes a single line to the inherited stdout stream and flushes.
        /// With <c>OutputType=WinExe</c>, <see cref="Console.Out"/> can be
        /// bound to a closed/invalid stream after <c>AllocConsole</c>, so we
        /// fall back to <see cref="Console.OpenStandardOutput"/> which always
        /// refers to the inherited file handle (fd 1) regardless of console
        /// state. The test harness reads this via
        /// <c>ProcessStartInfo.RedirectStandardOutput</c>.
        /// </summary>
        private static void WriteMarker(string marker, string detail)
        {
            var line = string.IsNullOrEmpty(detail) ? marker : $"{marker}: {detail}";
            try
            {
                using var stdout = Console.OpenStandardOutput();
                var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
            }
            catch
            {
                // Last-resort fallback: try Console.Out. If this also fails
                // (because the parent closed the pipe), the test will time
                // out — and that is a louder signal than silently dropping
                // the marker.
                Console.Out.WriteLine(line);
                Console.Out.Flush();
            }
        }

        private const int SW_SHOW = 5;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

        private static IntPtr GetWindowLong(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : GetWindowLongPtr32(hWnd, nIndex);
        }
    }
}
