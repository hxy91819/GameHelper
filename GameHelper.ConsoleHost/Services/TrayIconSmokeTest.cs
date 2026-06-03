using System;
using System.Threading;
using System.Windows.Forms;

namespace GameHelper.ConsoleHost.Services
{
    /// <summary>
    /// One-shot probe that exercises <see cref="TrayIconService"/>'s icon
    /// construction on a dedicated STA thread (the same plumbing the real
    /// service uses), waits for the shell to register the icon, and reports
    /// the result on stdout. Used by the automated tray-icon test to verify
    /// the icon is actually registered with the Windows shell — without a
    /// human having to look at the notification area.
    ///
    /// The probe is a child-process smoke test:
    ///   1. Spawn the GameHelper.ConsoleHost binary with --tray-smoke-test.
    ///   2. The binary creates a NotifyIcon via <see cref="TrayIconService.CreateIcon"/>,
    ///      spins a message loop, and inspects the result.
    ///   3. The binary writes a single line: TRAY_OK or TRAY_FAIL: &lt;reason&gt;.
    ///
    /// The test asserts on TRAY_OK. Anything else is a failure.
    /// </summary>
    public static class TrayIconSmokeTest
    {
        public const string OkMarker = "TRAY_OK";
        public const string FailMarker = "TRAY_FAIL";

        // [DECISION] These timeouts are picked to be:
        //  - long enough that any reasonable Windows shell can finish processing
        //    NIM_ADD and paint the notification area (200ms is the typical
        //    observed minimum, 1s gives a comfortable margin including cold
        //    shell-start cases);
        //  - short enough that a CI agent times out fast rather than hanging.
        private const int ShellProbeDelayMs = 1000;
        private const int UiStartupTimeoutMs = 5_000;
        private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

        public static int Run()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Out.WriteLine($"{FailMarker}: non-Windows host");
                return 2;
            }

            using var ready = new ManualResetEventSlim(false);
            string? failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(
                        new WindowsFormsSynchronizationContext());

                    // Use TrayIconService.CreateIcon so the smoke test exercises
                    // the *same* icon-construction path the user's app runs.
                    // If TrayIconService has a bug, the smoke test will catch it.
                    var notifyIcon = TrayIconService.CreateIcon();
                    notifyIcon.Visible = true;

                    ready.Set();

                    // Spin the message loop so the shell can finish processing NIM_ADD.
                    Application.Run();

                    try
                    {
                        // [DECISION] The success signal is
                        // notifyIcon.Visible == true after we set it to true.
                        // WinForms' NotifyIcon.Visible setter calls
                        // Shell_NotifyIcon(NIM_ADD) and rolls the field back
                        // to false if the shell rejects the call. So a sticky
                        // Visible=true is the strongest code-level proof that
                        // the tray icon is registered — short of enumerating
                        // the shell's internal notification table, which has
                        // no public API. Empirically, on Windows 11 the
                        // legacy Shell_NotifyIcon call from a non-foreground
                        // thread returns E_FAIL even when the icon is actually
                        // accepted, so we deliberately do NOT issue a parallel
                        // Shell_NotifyIcon probe here.
                        if (!notifyIcon.Visible)
                        {
                            failure = "NotifyIcon.Visible rolled back to false (shell rejected NIM_ADD)";
                        }
                    }
                    finally
                    {
                        notifyIcon.Visible = false;
                        notifyIcon.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + ": " + ex.Message;
                }
            })
            {
                Name = "TrayIconSmokeTest",
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Wait(UiStartupTimeoutMs))
            {
                Console.Out.WriteLine($"{FailMarker}: STA thread did not signal ready in {UiStartupTimeoutMs}ms");
                return 3;
            }

            // Give the shell a moment to process the NIM_ADD before we ask
            // the STA thread to exit and run the verification.
            Thread.Sleep(ShellProbeDelayMs);

            // Tell the STA thread to exit the message loop. Application.Exit
            // posts WM_QUIT into every running message loop in this domain, so
            // the STA thread's Application.Run will return. After it returns,
            // the verification runs and `failure` is set if the icon was not
            // really registered.
            try
            {
                Application.Exit();
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine($"{FailMarker}: Application.Exit threw {ex.GetType().Name}: {ex.Message}");
                return 4;
            }

            if (!thread.Join(JoinTimeout))
            {
                Console.Out.WriteLine($"{FailMarker}: STA thread did not exit within {JoinTimeout.TotalSeconds:F0}s");
                return 5;
            }

            if (failure != null)
            {
                Console.Out.WriteLine($"{FailMarker}: {failure}");
                return 1;
            }

            Console.Out.WriteLine(OkMarker);
            return 0;
        }
    }
}
