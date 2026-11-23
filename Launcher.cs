using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("STC AirDesk")]
[assembly: AssemblyProduct("STC AirDesk")]
[assembly: AssemblyVersion(AirDeskUpdates.InstalledVersion + ".0")]
[assembly: AssemblyFileVersion(AirDeskUpdates.InstalledVersion + ".0")]
[assembly: AssemblyInformationalVersion(AirDeskUpdates.InstalledVersion)]

internal static class Launcher
{
    internal const string AppTitle = "STC AirDesk";
    internal const string LegacyProductName = "STC Camera Game";
    private const int StartupTimeoutMs = 18000;

    [STAThread]
    private static void Main()
    {
        SetProcessDPIAware();
        using (Mutex instance = new Mutex(false, @"Local\STCAirDesk"))
        using (Mutex legacyInstance = new Mutex(false, @"Local\" + LegacyProductName.Replace(" ", "")))
        {
            bool ownsInstance = EnterInstance(instance);
            bool ownsLegacyInstance = ownsInstance && EnterInstance(legacyInstance);
            if (!ownsLegacyInstance)
            {
                if (ownsInstance) instance.ReleaseMutex();
                IntPtr existing = FindWindow("Chrome_WidgetWin_1", AppTitle);
                if (existing == IntPtr.Zero) existing = FindWindow("Chrome_WidgetWin_1", LegacyProductName);
                if (existing != IntPtr.Zero) BringForward(existing);
                return;
            }
            try
            {
                string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string appData = Path.Combine(localData, AppTitle);
                string legacyData = Path.Combine(localData, LegacyProductName);
                MoveAppData(legacyData, appData);
                using (FloatingKeyboard keyboard = new FloatingKeyboard())
                {
                    RunApplication(FindEdge(), appData, legacyData, keyboard);
                }
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                legacyInstance.ReleaseMutex();
                instance.ReleaseMutex();
            }
        }
    }

    private static void MoveAppData(string previous, string current)
    {
        if (Directory.Exists(current) || !Directory.Exists(previous)) return;
        try { Directory.Move(previous, current); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string FindProfile(string appData, string legacyData, string name)
    {
        string current = Path.Combine(appData, name);
        string previous = Path.Combine(legacyData, name);
        return !Directory.Exists(current) && Directory.Exists(previous) ? previous : current;
    }

    private static bool EnterInstance(Mutex instance)
    {
        try { return instance.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    private static Process StartEdge(string edgePath, string profile, string page)
    {
        Process edge = Process.Start(new ProcessStartInfo(edgePath,
            "--app=\"" + page + "\" --user-data-dir=\"" + profile + "\"" +
            " --no-first-run --no-default-browser-check" +
            " --disable-background-mode --disable-background-timer-throttling" +
            " --disable-renderer-backgrounding --disable-backgrounding-occluded-windows")
        {
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Normal
        });
        AllowSetForegroundWindow(edge.Id);
        return edge;
    }

    private static void RunApplication(string edgePath, string appData, string legacyData, FloatingKeyboard keyboard)
    {
        using (ApplicationContext context = new ApplicationContext())
        using (AppServer server = new AppServer(keyboard))
        using (TrayWindow tray = new TrayWindow(keyboard.Icon))
        using (System.Windows.Forms.Timer monitor = new System.Windows.Forms.Timer { Interval = 100 })
        {
            Process edge = StartEdge(edgePath, FindProfile(appData, legacyData, "EdgeProfile"), server.Page);
            Stopwatch elapsed = Stopwatch.StartNew();
            IntPtr window = IntPtr.Zero;
            IntPtr brandedWindow = IntPtr.Zero;
            bool recovered = false;
            long? missingSince = null;
            long? exitedSince = null;
            string startupError = null;
            try
            {
                // Create the keyboard handle on the STA while Edge loads the app page.
                if (keyboard.Handle == IntPtr.Zero) throw new InvalidOperationException("Could not create the floating keyboard.");
                monitor.Tick += (sender, args) =>
                {
                    try
                    {
                        edge.Refresh();
                        bool exited = edge.HasExited;
                        if (window == IntPtr.Zero)
                        {
                            if (server.PageServed) window = FindWindow("Chrome_WidgetWin_1", AppTitle);
                            if (!exited && window == IntPtr.Zero) window = edge.MainWindowHandle;
                            if (window != IntPtr.Zero)
                            {
                                BringForward(window);
                                monitor.Interval = 250;
                            }
                        }
                        if (window != IntPtr.Zero)
                        {
                            // Only brand and minimize the AirDesk app window, not another Edge window.
                            if (window != brandedWindow && server.PageServed &&
                                FindWindow("Chrome_WidgetWin_1", AppTitle) == window)
                            {
                                ApplyWindowIcon(window, keyboard.Icon.Handle);
                                brandedWindow = window;
                            }
                            if ((window == brandedWindow && tray.Observe(window)) || IsWindowVisible(window))
                            {
                                missingSince = null;
                                return;
                            }
                            IntPtr replacement = exited ? IntPtr.Zero : edge.MainWindowHandle;
                            if (replacement != IntPtr.Zero)
                            {
                                window = replacement;
                                missingSince = null;
                                return;
                            }
                            if (!missingSince.HasValue) missingSince = elapsed.ElapsedMilliseconds;
                            if (exited || elapsed.ElapsedMilliseconds - missingSince.Value >= 600)
                                context.ExitThread();
                            return;
                        }
                        if (exited)
                        {
                            if (!exitedSince.HasValue) exitedSince = elapsed.ElapsedMilliseconds;
                            // An existing Edge process can receive the launch and open the page.
                            if (elapsed.ElapsedMilliseconds - exitedSince.Value < 900) return;
                        }
                        if (exited && !recovered)
                        {
                            // Edge may pass the launch to a leftover browser process and exit.
                            // A separate profile gives the app a fresh window and process.
                            recovered = true;
                            Process retry = StartEdge(edgePath, Path.Combine(appData, "EdgeRecoveryProfile"), server.Page);
                            edge.Dispose();
                            edge = retry;
                            elapsed.Restart();
                            exitedSince = null;
                            return;
                        }
                        if (exited || elapsed.ElapsedMilliseconds >= StartupTimeoutMs)
                        {
                            startupError = "The app window could not open. Close any STC AirDesk windows and try again.";
                            context.ExitThread();
                        }
                    }
                    catch (Exception error)
                    {
                        startupError = error.Message;
                        context.ExitThread();
                    }
                };
                monitor.Start();
                Application.Run(context);
            }
            finally
            {
                monitor.Stop();
                edge.Dispose();
            }
            if (startupError != null) throw new InvalidOperationException(startupError);
        }
    }

    private static void BringForward(IntPtr window)
    {
        if (!IsWindowVisible(window) || IsIconic(window)) ShowWindowAsync(window, 9); // SW_RESTORE
        SetForegroundWindow(window);
    }

    private sealed class TrayWindow : IDisposable
    {
        private readonly ContextMenuStrip menu;
        private readonly NotifyIcon icon;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private IntPtr window;
        private bool inTray;
        private long restoreUntil;

        public TrayWindow(System.Drawing.Icon appIcon)
        {
            menu = new ContextMenuStrip();
            menu.Items.Add("Restore AirDesk", null, (sender, args) => Restore());
            menu.Items.Add("Exit AirDesk", null, (sender, args) =>
            {
                if (IsWindow(window)) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            });
            icon = new NotifyIcon { Text = AppTitle, Icon = appIcon, ContextMenuStrip = menu };
            icon.MouseClick += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left) Restore();
            };
        }

        public bool Observe(IntPtr target)
        {
            if (window != target)
            {
                window = target;
                LeaveTray();
            }
            if (!IsWindow(window))
            {
                LeaveTray();
                return false;
            }

            bool visible = IsWindowVisible(window);
            if (visible && !IsIconic(window))
            {
                LeaveTray();
                return true;
            }
            if (visible && clock.ElapsedMilliseconds >= restoreUntil)
            {
                icon.Visible = true;
                if (ShowWindowAsync(window, 0)) inTray = true; // SW_HIDE
                else LeaveTray();
            }
            // A tray window remains alive even if Edge's original launcher process has exited.
            return visible || inTray;
        }

        private void Restore()
        {
            if (!IsWindow(window)) return;
            restoreUntil = clock.ElapsedMilliseconds + 1000;
            BringForward(window);
        }

        private void LeaveTray()
        {
            if (icon.Visible) icon.Visible = false;
            inTray = false;
            restoreUntil = 0;
        }

        public void Dispose()
        {
            LeaveTray();
            icon.Dispose();
            menu.Dispose();
        }
    }

    private static void ApplyWindowIcon(IntPtr window, IntPtr icon)
    {
        const uint setIcon = 0x0080;
        const uint abortIfHung = 0x0002;
        IntPtr previous;
        SendMessageTimeout(window, setIcon, IntPtr.Zero, icon, abortIfHung, 80, out previous);
        SendMessageTimeout(window, setIcon, new IntPtr(1), icon, abortIfHung, 80, out previous);
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static string FindEdge()
    {
        string[] locations = {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (string location in locations)
        {
            if (String.IsNullOrEmpty(location)) continue;
            string candidate = Path.Combine(location, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Microsoft Edge is required to open STC AirDesk.");
    }
}
