// Race Mode - the window and the on/off logic.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RaceMode
{
    public class LaunchItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Args { get; set; }
        public string ProcessName { get; set; }
        public bool Enabled { get; set; }
        [ScriptIgnore] public string Detail { get { var d = string.IsNullOrEmpty(Args) ? Path : Path + "  " + Args; return Installed ? d : d + "  · not installed"; } }
        // False only for a full path to an .exe that doesn't exist (e.g. OBS listed before it's installed).
        [ScriptIgnore] public bool Installed
        {
            get
            {
                try { return string.IsNullOrEmpty(Path) || !System.IO.Path.IsPathRooted(Path) || !Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || File.Exists(Path); }
                catch { return true; }
            }
        }
    }

    public class GraphicsLevels
    {
        public int Level;
        public Dictionary<string, string> Overrides = new Dictionary<string, string>();
    }

    public class Config
    {
        public List<LaunchItem> Launch = new List<LaunchItem>();
        public List<string> Close = new List<string>();
        public List<string> Lower = new List<string>();
        public bool CloseApps = true, LaunchApps = true, StartIRacing = true, SimHighPriority = true;
        public bool LowerBackground = true, GpuWarning = true, AutoOff = true, Logging = true;
        public int LogIntervalSec = 2;
        public string IRacingUI = InstallFinder.IRacingUI();     // where iRacing is installed (registry), else the default folder
        public DisplayGuardConfig DisplayGuard = new DisplayGuardConfig();
        public string GraphicsMode = "Race";                    // "Race" or "Photo" (GraphicsPresets.cs)
        public bool Streaming = false, StreamBoost = true, VramCheck = true;
        public bool StreamSeeded = false;                       // OBS added to the streaming list once (never re-added if removed)
        public List<LaunchItem> StreamLaunch = new List<LaunchItem>();
        // Slider level + pinned settings (Advanced editor) behind the Race and Photo presets (GraphicsCatalog.cs).
        // Null until first run, then worked out from the saved presets.
        public GraphicsLevels GraphicsRace, GraphicsPhoto;
        // Window size and state, restored at start (0 = default 580 x 840). Maximised shows the full dashboard.
        public double WindowWidth, WindowHeight;
        public bool WindowMaximized;
        // Updates (Updater.cs): check every 12 hours; test versions = GitHub pre-releases from the dev branch.
        public bool UpdateCheck = true, UpdateTestVersions = false;
        public bool TrayOnClose = true, TrayHintShown = false;
        public bool? SetupDone;   // false until first-run setup is finished; null in configs from before this existed   // closing the window keeps Race Mode in the tray
        // OBS dock (ObsDock.cs): local port and the private key in its link (made on first use).
        public int DockPort = 47815;
        public string DockToken = "";

        public static LaunchItem DefaultObs()
        {
            return new LaunchItem { Name = "OBS Studio", Path = InstallFinder.ObsExe() ?? InstallFinder.DefaultObs, Args = "", ProcessName = "obs64", Enabled = true };
        }

        public static Config Defaults()
        {
            // First run on a new PC: nothing to close or slow down, and only race apps that are actually
            // installed go on the Launch list. The screen check stays off until a racing monitor is picked
            // (Settings tab), so a friend's iRacing screen settings are never changed by someone else's defaults.
            // Only quick default-path checks here (this runs on the UI thread); the full scan of Start menu
            // shortcuts and Settings > Apps runs in the background later (Shell.StartLaunchScan).
            List<LaunchItem> quick;
            try { quick = LaunchScan.Find(AppScan.Apps, new string[0], null, null, LaunchScan.Budget); } catch { quick = null; }
            return Defaults(quick);
        }

        // detected: race apps a LaunchScan found (null: only the fixed default paths).
        public static Config Defaults(IEnumerable<LaunchItem> detected)
        {
            var c = new Config { SetupDone = false };
            c.DisplayGuard = new DisplayGuardConfig { KeepFpsCap = false };
            c.Launch.AddRange(KnownRaceApps(detected));
            c.StreamLaunch.Add(DefaultObs());
            return c;
        }

        // Race apps on this PC: the detected ones first, then the fixed default paths below for apps the detection
        // didn't find (one item per app). Only items whose exe exists are returned.
        public static List<LaunchItem> KnownRaceApps(IEnumerable<LaunchItem> detected)
        {
            var list = (detected ?? new LaunchItem[0]).Where(i => i != null && File.Exists(i.Path)).ToList();
            List<KnownApp> apps; try { apps = AppScan.Apps; } catch { apps = new List<KnownApp>(); }
            var fixedFound = FixedRaceApps().Where(i => File.Exists(i.Path)).ToList();
            foreach (var it in LaunchScan.NotListed(apps, fixedFound, list)) list.Add(it);
            return list;
        }

        // Common sim-racing helpers at their default install paths (the fallback when detection finds nothing).
        static IEnumerable<LaunchItem> FixedRaceApps()
        {
            var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return new LaunchItem { Name = "SimHub", Path = System.IO.Path.Combine(x86, @"SimHub\SimHubWPF.exe"), ProcessName = "SimHubWPF", Enabled = true };
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            // wheelbase apps from several makers (only the installed ones are added)
            yield return new LaunchItem { Name = "Logitech G HUB", Path = System.IO.Path.Combine(pf, @"LGHUB\lghub.exe"), ProcessName = "lghub", Enabled = true };
            yield return new LaunchItem { Name = "SimPro Manager", Path = System.IO.Path.Combine(x86, @"Simagic\Simpro3\bin\simpro3.exe"), ProcessName = "simpro3", Enabled = true };
            yield return new LaunchItem { Name = "Trading Paints", Path = System.IO.Path.Combine(x86, @"Rhinode LLC\Trading Paints\Trading Paints.exe"), ProcessName = "Trading Paints", Enabled = true };
            yield return new LaunchItem { Name = "Crew Chief", Path = System.IO.Path.Combine(x86, @"Britton IT Ltd\CrewChiefV4\CrewChiefV4.exe"), ProcessName = "CrewChiefV4", Enabled = true };
            yield return new LaunchItem { Name = "Garage 61", Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"garage61-install\garage61-agent.exe"), ProcessName = "garage61-agent", Enabled = true };
        }
    }

    public class Program
    {
        [STAThread]
        public static void Main()
        {
            try { Run(); }
            catch (Exception ex)
            {
                // Startup failures (missing RaceMode.xaml, bad XAML, etc.) used to exit silently.
                Shell.LogCrash(ex);
                // A just-installed update that can't start puts the previous version back and starts that instead.
                // Let go of the single-instance lock first, or the old version would see "already running" and quit.
                try { if (_instance != null) { _instance.Dispose(); _instance = null; } } catch { }
                if (Updater.RollBackIfFreshUpdate()) return;
                try { MessageBox.Show("Race Mode couldn't start:\n\n" + ex.Message + "\n\nDetails were saved to crash.log.", "Race Mode", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
            }
        }

        static Shell _shell;
        static DateTime _lastErrorBox = DateTime.MinValue;
        static System.Threading.Mutex _instance;               // held for the app's lifetime (GC.KeepAlive at the end of Run)
        static System.Threading.EventWaitHandle _showSignal;   // a second launch sets this to bring this window forward

        // Waits for a second launch's signal and brings the window to the front.
        static void ListenForShow(Window w)
        {
            if (_showSignal == null) return;
            var t = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    try { _showSignal.WaitOne(); } catch { return; }
                    try
                    {
                        w.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!w.IsVisible) w.Show();
                            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                            w.Activate(); w.Topmost = true; w.Topmost = false; w.Focus();
                        }));
                    }
                    catch { return; }
                }
            });
            t.IsBackground = true; t.Name = "RaceMode.Show"; t.Start();
        }

        // --uninstall (from Settings > Apps): asks twice, then removes Race Mode (Setup.cs Installer.Uninstall).
        static void RunUninstall()
        {
            const string T = "Uninstall Race Mode";
            bool first;
            var only = new System.Threading.Mutex(true, @"Local\RaceMode.Uninstall" + (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RACEMODE_TEST_ROOT")) ? "" : ".Test"), out first);
            if (!first) { only.Dispose(); MessageBox.Show("Race Mode is already being uninstalled.", T, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            try { RunUninstallOnce(T); } finally { only.ReleaseMutex(); only.Dispose(); }
        }

        static void RunUninstallOnce(string T)
        {
            if (Installer.RaceModeRunning()) { MessageBox.Show("Race Mode is running. Quit it first (right-click its icon in the system tray, next to the clock, and choose Quit, or close its window if it has no icon there), then uninstall again.", T, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (!Installer.IsInstalledCopy) { MessageBox.Show("This copy of Race Mode wasn't installed, so there's nothing to uninstall. To remove it, delete its folder.", T, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (MessageBox.Show("Remove Race Mode from this PC?\n\nYour iRacing settings stay as they are.", T, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var keep = MessageBox.Show("Keep your Race Mode settings and race logs?\n\nYes keeps them, so installing again picks them up.\nNo deletes them too.", T, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
            try
            {
                var done = Installer.Uninstall(keep);
                MessageBox.Show(string.Join("\n", done.ToArray()) + Obs.UninstallNote(), T, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { Shell.LogCrash(ex); MessageBox.Show("Race Mode couldn't be fully removed: " + ex.Message, T, MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        static int ArgInt(string[] args, int i, int fallback, int min, int max)
        {
            int v;
            if (args.Length <= i || !int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return fallback;
            return Math.Max(min, Math.Min(max, v));
        }

        static void Run()
        {
            // Software rendering: WPF's GPU path (a Direct3D device plus surfaces) cost ~37 MB private memory and
            // pushed Race Mode past its 120 MB budget (measured: about 143 -> 106 MB private, and slightly less CPU).
            // The window is small and updates once a second, so the CPU draws it cheaply, and the GPU stays iRacing's.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            // Reduced motion: with Windows' "Animation effects" off, every animation (switch thumbs, the Advanced
            // chevron) runs at 1 frame per second, so it completes in its first frame, i.e. it just jumps.
            if (!SystemParameters.ClientAreaAnimation)
                System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata { DefaultValue = 1 });
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Shell.LogCrash(e.Exception);
                e.Handled = true;
                // One dialog at most every 30 s; repeats only go to crash.log so a looping error can't bury the screen in dialogs.
                if ((DateTime.Now - _lastErrorBox).TotalSeconds < 30) return;
                _lastErrorBox = DateTime.Now;
                MessageBox.Show("Race Mode hit an error:\n\n" + e.Exception.Message + "\n\nDetails were saved to crash.log.", "Race Mode", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            // If the process dies anyway, still put priorities back so the PC isn't left with lowered/boosted processes.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception; if (ex != null) Shell.LogCrash(ex);
                try { if (_shell != null) _shell.RestorePriorities(); } catch { }
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { if (_shell != null) _shell.RestorePriorities(); } catch { } };

            var args = Environment.GetCommandLineArgs();
            // One real Race Mode at a time: two copies would both guard and rewrite iRacing's ini.
            // Test modes (--logtest, --savepreset, --selftest, --snapshot) skip this so they never
            // clash with the user's running app; they run from isolated copies.
            // Real modes (not tests): --after-update (updater), --install / --uninstall (Setup.cs),
            // --minimized (start with Windows), --setup (show the first-run questions again).
            string mode = args.Length >= 2 ? args[1] : "";
            bool afterUpdate = args.Length >= 3 && mode == "--after-update";
            bool realMode = afterUpdate || mode == "--install" || mode == "--uninstall" || mode == "--minimized" || mode == "--setup";
            bool testMode = mode.StartsWith("--", StringComparison.Ordinal) && !realMode;
            if (mode == "--install") { InstallerWindow.Run(app); return; }
            if (mode == "--uninstall") { RunUninstall(); return; }
            if (afterUpdate) Updater.FinishSwap(args[2]);    // waits for the old copy to quit, then removes RaceMode.exe.old
            if (!testMode)
            {
                bool created;
                // Under RACEMODE_TEST_ROOT (tests) the names get ".Test", matching Installer.RaceModeRunning, so a test
                // copy never talks to (or is mistaken for) the real Race Mode.
                var sfx = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RACEMODE_TEST_ROOT")) ? "" : ".Test";
                _instance = new System.Threading.Mutex(true, @"Local\RaceMode.Instance" + sfx, out created);
                if (!created)
                {
                    // Already running: ask it to come to the front, then quit quietly.
                    try { System.Threading.EventWaitHandle wh; if (System.Threading.EventWaitHandle.TryOpenExisting(@"Local\RaceMode.Show" + sfx, out wh)) { wh.Set(); wh.Dispose(); } } catch { }
                    return;
                }
                _showSignal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\RaceMode.Show" + sfx);
                if (!afterUpdate) Updater.NoticeEarlierUpdate();   // an update installed while on, starting only now
            }
            // Tests: read a fake iRacing telemetry map (see tests\IrsdkTests.cs) instead of the real one.
            for (int i = 1; i + 1 < args.Length; i++)
                if (args[i] == "--irsdk-map") { Irsdk.MapName = args[i + 1]; Irsdk.ForceOpen = true; }
            if (args.Length >= 3 && args[1] == "--logtest")
            {
                // Records a short log without turning Race Mode on (used to check the logger).
                var m = new Monitor(); m.Start(1000); m.StartLogging(args[2]);
                System.Threading.Thread.Sleep(ArgInt(args, 3, 10, 1, 3600) * 1000);
                m.StopLogging(); m.Stop();
                return;
            }
            if (args.Length >= 4 && args[1] == "--savepreset")
            {
                // Saves an ini (e.g. a hand-built or researched one) as the Race/Photo preset next to the exe, then exits.
                try { File.WriteAllText(System.IO.Path.Combine(Shell.Data, "savepreset.result.txt"), "saved " + GraphicsPresets.SaveFromIni(args[2], args[3]) + " settings as " + GraphicsPresets.Normalize(args[2])); }
                catch (Exception ex) { Shell.LogCrash(ex); }
                return;
            }
            if (args.Length >= 2 && args[1] == "--selftest" && !File.Exists(System.IO.Path.Combine(Shell.Data, "selftest.ok")))
            {
                // --selftest flips the real switch with the config.json next to the exe. It only runs in an
                // isolated copy that has a selftest.ok marker, so it can never act on the real config.
                try { File.WriteAllText(System.IO.Path.Combine(Shell.Data, "selftest.refused.txt"), "Refused: no selftest.ok next to the exe. Run --selftest only in an isolated copy with a throwaway config.json."); } catch { }
                return;
            }
            var shell = new Shell();
            // Sign-out, restart or shutdown: really close (turn off, save, write the summary) instead of hiding to the tray.
            app.SessionEnding += (s2, e2) => shell.PrepareQuit();
            shell.TrayAllowed = !testMode;   // test and snapshot modes must really close
            _shell = shell;
            if (args.Length >= 3 && args[1] == "--selftest")
            {
                // Flips the real switch on, waits, flips it off, then exits. Uses whatever config.json says.
                int secs = ArgInt(args, 2, 10, 1, 3600);
                shell.Window.Loaded += (s, e) =>
                {
                    var sw = (ToggleButton)shell.Window.FindName("Switch");
                    var t1 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    t1.Tick += (a, b) =>
                    {
                        t1.Stop(); sw.IsChecked = true;
                        var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
                        t2.Tick += (c, d) =>
                        {
                            t2.Stop(); sw.IsChecked = false;
                            var t3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                            t3.Tick += (x, y) => { t3.Stop(); shell.Window.Close(); };
                            t3.Start();
                        };
                        t2.Start();
                    };
                    t1.Start();
                };
            }
            if (args.Length >= 3 && args[1] == "--snapshot")
            {
                // Renders the window to a PNG after a few seconds and exits (used to check the design).
                var outPath = args[2]; int tab = ArgInt(args, 3, 0, 0, 5);
                // wide snapshots wait longer so the 60 s charts have some history to show
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(args.Length >= 5 && args[4].Contains("wide") ? 20 : 4) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    shell.Window.UpdateLayout();
                    shell.Window.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Render);
                    var root = (FrameworkElement)shell.Window.Content;
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)shell.Window.ActualWidth, (int)shell.Window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(shell.Window) , null, new Rect(0, 0, shell.Window.ActualWidth, shell.Window.ActualHeight));
                    bmp.Render(dv);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using (var fs = File.Create(outPath)) enc.Save(fs);
                    shell.Window.Close();
                };
                shell.Window.Loaded += (s, e) =>
                {
                    var tabs = shell.Window.FindName("Tabs") as TabControl; if (tabs != null) tabs.SelectedIndex = tab;
                    if (args.Length >= 5 && args[4] == "on") shell.PreviewOn();
                    if (args.Length >= 5 && args[4].Contains("wide")) { shell.Window.Width = ArgInt(args, 5, 1600, 1100, 3840); shell.Window.Height = 900; }
                    if (args.Length >= 5 && args[4].Contains("mon"))
                        shell.Window.Dispatcher.BeginInvoke(new Action(() => { var l = shell.Window.FindName("OptTestVersions") as FrameworkElement; if (l != null) l.BringIntoView(); }), DispatcherPriority.ApplicationIdle);
                    if (args.Length >= 5 && args[4].Contains("adv"))
                    {
                        var adv = shell.Window.FindName("AdvToggle") as ToggleButton; if (adv != null) adv.IsChecked = true;
                        // scroll so the first editor rows are on screen
                        shell.Window.Dispatcher.BeginInvoke(new Action(() => { var l = shell.Window.FindName("AdvList") as FrameworkElement; if (l != null) l.BringIntoView(new Rect(0, 0, 1, 420)); }), DispatcherPriority.ApplicationIdle);
                    }
                    t.Start();
                };
            }
            if (mode == "--minimized")
            {
                // Start with Windows: straight to the tray (or minimised, if the tray is switched off).
                shell.Window.WindowState = WindowState.Minimized; shell.Window.ShowActivated = false;
                if (shell.TrayOnClose) shell.Window.Loaded += (s, e) => shell.HideToTray(false);
            }
            ListenForShow(shell.Window);
            app.Run(shell.Window);
            GC.KeepAlive(_instance);
        }
    }

    public partial class Shell
    {
        // Home = where the exe lives (and the dev-only RaceMode.xaml / graphics-catalog.json overrides).
        // Data = config, graphics, logs, crash.log. See DataDirs.Resolve for the order it is picked in.
        public static readonly string Home = AppDomain.CurrentDomain.BaseDirectory;
        public static string Data { get { if (_data == null) _data = DataDirs.Resolve(Home, Environment.GetCommandLineArgs()); return _data; } }
        static string _data;
        static string ConfigPath { get { return System.IO.Path.Combine(Data, "config.json"); } }
        static string LogsRoot { get { return System.IO.Path.Combine(Data, "logs"); } }
        static readonly string[] AlwaysProtected = { "iRacingUI", "iRacingSim64DX11", "iRacingService64", "RaceMode", "explorer",
            // Windows itself: closing any of these breaks the desktop, typing or sound
            "sihost", "ctfmon", "svchost", "RuntimeBroker", "TextInputHost", "ApplicationFrameHost", "dwm", "csrss", "winlogon",
            "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "SearchApp", "taskhostw", "conhost", "fontdrvhost",
            "SecurityHealthSystray", "smartscreen", "audiodg", "lsass", "services", "wininit", "System", "Idle", "dllhost", "LockApp" };

        public Window Window;
        Config _cfg;
        readonly Monitor _mon = new Monitor();
        readonly ObservableCollection<LaunchItem> _launch = new ObservableCollection<LaunchItem>();
        readonly ObservableCollection<string> _close = new ObservableCollection<string>();
        readonly DispatcherTimer _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        bool _on, _simSeen, _uiSeen, _simBoosted, _suppressToggle, _bound, _guardBusy;
        readonly DispatcherTimer _guardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        string _guardSig, _lastWarning, _lastSkip;

        // Activation (Off -> Turning on -> Activated): what was started/closed, and the confirmation result.
        readonly ObservableCollection<LaunchItem> _stream = new ObservableCollection<LaunchItem>();
        readonly DispatcherTimer _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        DateTime _onAt;
        string _phase = "off";                 // off | turning | active | issues
        string _activation = "";               // first status line once confirmed
        readonly List<string> _issues = new List<string>();
        readonly List<string> _started = new List<string>(), _alreadyOpen = new List<string>(), _closed = new List<string>(), _notInstalled = new List<string>();
        int _forced;
        readonly HashSet<string> _confirmedRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool _vramWarned;

        // Graphics presets (Photo mode) and display summary for the status line.
        bool _graphicsPending, _graphicsOk, _bindingGraphics;
        string _graphicsLine = "", _displayLine = "", _gfxStamp;
        int _goneTicks;
        readonly Dictionary<int, string> _lowered = new Dictionary<int, string>();
        readonly Dictionary<int, string> _boosted = new Dictionary<int, string>();
        DispatcherTimer _killTimer;
        string _lastSummary;

        T Get<T>(string name) where T : class { return Window.FindName(name) as T; }

        public Shell()
        {
            // The window layout is built into the exe. A RaceMode.xaml next to the exe overrides it, in local builds only:
            // XAML can run code, so a released build never loads one from disk.
            var xamlFile = System.IO.Path.Combine(Home, "RaceMode.xaml");
            using (var fs = BuildInfo.Repo.Length == 0 && File.Exists(xamlFile) ? File.OpenRead(xamlFile) : typeof(Shell).Assembly.GetManifestResourceStream("RaceMode.xaml"))
                Window = (Window)XamlReader.Load(fs);
            HookTrayClose();   // first Closing handler: a close that only hides to the tray must skip the others' cleanup

            // Fit the window on short screens (the window is resizable, 840 px is just the preferred height).
            var work = SystemParameters.WorkArea;
            if (Window.Height > work.Height - 16) Window.Height = Math.Max(Window.MinHeight, work.Height - 16);

            var migrated = DataDirs.MigrateIfNeeded(Data);
            LoadConfig();
            RestoreWindowSize();
            BindUi();
            if (migrated != null) Tick(migrated);
            MaybeStartFirstRun();
            Window.Loaded += (s, e) =>
            {
                // An update just installed: once this window is up, the old exe isn't needed for a rollback.
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                t.Tick += (a, b) => { t.Stop(); if (Updater.ConfirmSwap()) { Installer.RefreshUninstallVersion(); Tick("Updated to Race Mode " + Updater.Current); } };
                t.Start();
            };
            _mon.Start(_cfg.LogIntervalSec * 1000);
            _uiTimer.Tick += (s, e) => RefreshStats();
            _uiTimer.Start();
            _watch.Tick += (s, e) => WatchTick();
            // Display guard runs whenever the app is open (on or off), so a renumbered monitor, an iRacing
            // exit write, an irSidekick profile apply or a UI reset is undone before the next launch.
            _guardTimer.Tick += (s, e) => GuardTick();
            _guardTimer.Start();
            _confirmTimer.Tick += (s, e) => ConfirmTick();
            // First run: if there's no Race preset yet, iRacing's current graphics become it (nothing changes in iRacing).
            GraphicsPresets.EnsureRaceSeeded(IniPath());
            GraphicsPresets.EnsurePhotoSeeded(IniPath(), _cfg.GraphicsPhoto != null && _cfg.GraphicsPhoto.Level > 0 ? _cfg.GraphicsPhoto.Level : 2);
            InitGraphicsLevels();
            RefreshGraphicsStatus();
            Window.Closing += (s, e) =>
            {
                if (e.Cancel) return;   // only hidden to the tray
                if (_gfxCommit.IsEnabled) { _gfxCommit.Stop(); CommitGraphicsLevels(); }   // a slider move made just before closing
                RememberWindowSize();
                if (_on) TurnOff("app closed");
                _guardTimer.Stop();
                SaveConfig();
                _mon.Stop();
            };
            ShowPowerPlan();
            LoadLastSummary();
            if (_cfgBroken)
                Tick("Your settings file (config.json) couldn't be read, so Race Mode won't close, start or slow down any apps, and won't save changes. A copy was kept next to it as config.broken-….json" + (_cfgWarning != null ? " (" + _cfgWarning + ")" : ""));
        }

        // ---------------- config ----------------
        // Set when config.json exists but can't be read. The broken file is kept (and backed up) and
        // nothing is saved over it, so a typo never silently turns into the default Close list.
        bool _cfgBroken;
        string _cfgWarning;

        void LoadConfig()
        {
            if (File.Exists(ConfigPath))
            {
                try { _cfg = new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(ConfigPath)); }
                catch (Exception ex) { _cfg = null; _cfgWarning = ex.Message; }
                if (_cfg == null)
                {
                    _cfgBroken = true;
                    try { File.Copy(ConfigPath, System.IO.Path.Combine(Data, "config.broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json"), true); } catch { }
                    // Safe fallback: nothing to close, launch or lower until the file is fixed.
                    _cfg = new Config { CloseApps = false, LaunchApps = false, LowerBackground = false };
                }
            }
            else { _cfg = Config.Defaults(); SaveConfig(); _freshConfig = true; }
            Sanitize(_cfg);
            if (_cfg.LogIntervalSec != 1 && _cfg.LogIntervalSec != 2 && _cfg.LogIntervalSec != 5) _cfg.LogIntervalSec = 2;
        }

        // Launch/Stream items: drop blanks, fill missing names. A blank ProcessName gets the exe's name, because
        // without one the "already open" check can't work and the app would be started twice. Launchers like
        // explorer.exe / cmd.exe start something else, so those keep whatever name was set by hand.
        static List<LaunchItem> CleanItems(List<LaunchItem> items)
        {
            var list = (items ?? new List<LaunchItem>()).Where(l => l != null && !string.IsNullOrWhiteSpace(l.Path)).ToList();
            foreach (var l in list)
            {
                if (string.IsNullOrWhiteSpace(l.Name)) l.Name = System.IO.Path.GetFileNameWithoutExtension(l.Path);
                if (l.Args == null) l.Args = "";
                if (l.ProcessName == null) l.ProcessName = "";
                var target = l.Path;
                if (l.ProcessName.Length == 0 && target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) { try { target = DataDirs.ShortcutTarget(target) ?? ""; } catch { target = ""; } }
                if (l.ProcessName.Length == 0 && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var exe = System.IO.Path.GetFileNameWithoutExtension(target);
                    if (!new[] { "explorer", "cmd", "powershell", "pwsh", "wscript", "cscript", "rundll32" }.Contains(exe, StringComparer.OrdinalIgnoreCase)) l.ProcessName = exe;
                }
            }
            return list;
        }

        // Drops null or blank entries a hand-edited config.json can contain, so ON can't crash halfway.
        static void Sanitize(Config c)
        {
            if (c.Launch == null) c.Launch = new List<LaunchItem>();
            if (c.Close == null) c.Close = new List<string>();
            if (c.Lower == null) c.Lower = new List<string>();
            // An existing config.json from before Stream mode has no StreamLaunch: start it with OBS.
            if (c.StreamLaunch == null) c.StreamLaunch = new List<LaunchItem>();
            if (!c.StreamSeeded) { if (c.StreamLaunch.Count == 0) c.StreamLaunch.Add(Config.DefaultObs()); c.StreamSeeded = true; }
            c.Launch = CleanItems(c.Launch);
            c.StreamLaunch = CleanItems(c.StreamLaunch);
            c.GraphicsMode = GraphicsPresets.Normalize(c.GraphicsMode);
            foreach (var g in new[] { c.GraphicsRace, c.GraphicsPhoto })
                if (g != null && g.Overrides == null) g.Overrides = new Dictionary<string, string>();
            if (c.GraphicsRace != null) c.GraphicsRace.Level = Math.Max(0, Math.Min(10, c.GraphicsRace.Level));
            if (c.GraphicsPhoto != null) c.GraphicsPhoto.Level = Math.Max(0, Math.Min(3, c.GraphicsPhoto.Level));
            c.Close =c.Close.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
            c.Lower = c.Lower.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
            // Older configs saved the default folder: if iRacing isn't there, look up where it is installed.
            if (string.IsNullOrWhiteSpace(c.IRacingUI) || !File.Exists(c.IRacingUI)) c.IRacingUI = InstallFinder.IRacingUI();
            if (c.DisplayGuard == null) c.DisplayGuard = new DisplayGuardConfig();
            if (c.DisplayGuard.IniPath == null) c.DisplayGuard.IniPath = "";
        }

        void SaveConfig()
        {
            if (_cfgBroken) return;
            try
            {
                ReadOptionsFromUi();
                var json = new JavaScriptSerializer().Serialize(_cfg);
                // Write to a temp file first so a crash mid-write can't leave a half-written config.json.
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, PrettyJson(json));
                if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
                else File.Move(tmp, ConfigPath);
            }
            catch (Exception ex) { Tick("Couldn't save settings: " + ex.Message); }
        }

        static string PrettyJson(string json)
        {
            var sb = new System.Text.StringBuilder(); int indent = 0; bool quoted = false, escaped = false;
            for (int i = 0; i < json.Length; i++)
            {
                var ch = json[i];
                if (quoted)
                {
                    sb.Append(ch);
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') quoted = false;
                    continue;
                }
                if (ch == '"') { quoted = true; sb.Append(ch); continue; }
                switch (ch)
                {
                    case '{': case '[': sb.Append(ch).Append('\n').Append(new string(' ', ++indent * 2)); break;
                    case '}': case ']': sb.Append('\n').Append(new string(' ', --indent * 2)).Append(ch); break;
                    case ',': sb.Append(ch).Append('\n').Append(new string(' ', indent * 2)); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        // ---------------- UI wiring ----------------
        void BindUi()
        {
            Get<Grid>("TitleBar").MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { ToggleMaximize(); return; }                 // double-click the title bar, like any Windows app
                if (e.ButtonState == MouseButtonState.Pressed) Window.DragMove();
            };
            Get<Button>("MinBtn").Click += (s, e) => Window.WindowState = WindowState.Minimized;
            Get<Button>("MaxBtn").Click += (s, e) => ToggleMaximize();
            Window.StateChanged += (s, e) => OnWindowState();
            Window.SizeChanged += (s, e) => ApplyLayout();
            { var tc = Get<TabControl>("Tabs"); if (tc != null) for (int ti = 0; ti < tc.Items.Count; ti++) { var item = tc.Items[ti] as TabItem; if (item != null) item.ToolTip = item.Header + "  (Ctrl+" + (ti + 1) + ")"; } }
            // Ctrl+1..6 jumps to a tab (Launch, Close, Stream, Graphics, Settings, Logs).
            Window.PreviewKeyDown += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || Get<UIElement>("SetupOverlay").Visibility == Visibility.Visible) return;
                int n = e.Key >= Key.D1 && e.Key <= Key.D6 ? e.Key - Key.D1 : e.Key >= Key.NumPad1 && e.Key <= Key.NumPad6 ? e.Key - Key.NumPad1 : -1;
                if (n < 0) return;
                var tabs = Get<TabControl>("Tabs"); if (tabs != null && n < tabs.Items.Count) { tabs.SelectedIndex = n; e.Handled = true; }
            };
            Get<Button>("CloseBtn").Click += (s, e) => Window.Close();
            Window.SourceInitialized += (s, e) => RoundCorners();
            HookMaximiseFit();   // maximised = the monitor's free area, so the taskbar stays visible
            LoadAppIcon();
            Get<Border>("CpuTempCell").MouseLeftButtonUp += (s, e) => CpuTempAction();
            Get<Border>("CpuTempCell").KeyDown += (s, e) => { if (e.Key == Key.Enter || e.Key == Key.Space) { CpuTempAction(); e.Handled = true; } };
            Get<Button>("PowerBtn").Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("control.exe", "powercfg.cpl") { UseShellExecute = true }); }
                catch (Exception ex) { Tick("Couldn't open power options: " + ex.Message); }
            };
            Window.Activated += (s, e) => ShowPowerPlan(); // pick up a plan changed in Windows

            // Launch list: Tab goes straight to each row's checkbox (the row itself isn't a stop),
            // and focusing a checkbox selects its row so Remove works from the keyboard.
            var launchList = Get<ListBox>("LaunchList");
            launchList.ItemContainerGenerator.StatusChanged += (s, e) =>
            {
                if (launchList.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated) return;
                foreach (var item in launchList.Items)
                {
                    var c = launchList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
                    if (c != null) c.IsTabStop = false;
                }
            };
            launchList.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
            {
                var c = ItemsControl.ContainerFromElement(launchList, e.OriginalSource as DependencyObject) as ListBoxItem;
                if (c != null) c.IsSelected = true;
            }));

            foreach (var i in _cfg.Launch) _launch.Add(i);
            _launch.CollectionChanged += (s, e) => RefreshLaunchSuggest();   // "Found on your PC" leaves out what's listed
            StartLaunchScan();
            foreach (var c in _cfg.Close) _close.Add(c);
            foreach (var i in _cfg.StreamLaunch) _stream.Add(i);
            Get<ListBox>("LaunchList").ItemsSource = _launch;
            Get<ListBox>("CloseList").ItemsSource = _close;
            // Close list: the app's friendly name, then its process name and whether it's open now (with its memory).
            // The list still stores process names.
            {
                var sp = new FrameworkElementFactory(typeof(StackPanel));
                sp.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
                var title = new FrameworkElementFactory(typeof(TextBlock));
                title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new FriendlyNameConverter(), ConverterParameter = "title" });
                title.SetValue(TextBlock.FontSizeProperty, 13.0);
                var sub = new FrameworkElementFactory(typeof(TextBlock));
                sub.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new CloseStateConverter() });
                sub.SetValue(FrameworkElement.StyleProperty, Window.Resources["Sub"]);
                sp.AppendChild(title); sp.AppendChild(sub);
                Get<ListBox>("CloseList").ItemTemplate = new DataTemplate { VisualTree = sp };
                var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                closeTimer.Tick += (s, e) =>
                {
                    var lb = Get<ListBox>("CloseList");
                    if (!lb.IsVisible || lb.IsKeyboardFocusWithin || lb.IsMouseOver) return;
                    if (CloseStateConverter.Snapshot(_close)) lb.Items.Refresh();
                };
                closeTimer.Start();
                CloseStateConverter.KeptBecause = n =>
                    AlwaysProtected.Any(p => string.Equals(p, n, StringComparison.OrdinalIgnoreCase)) ? "never closed" :
                    InList(_launch, n) ? "kept: it's on your Launch list" :
                    _cfg.Streaming && InList(_stream, n) ? "kept while Streaming is on" : null;
                Get<ListBox>("CloseList").IsVisibleChanged += (s, e) =>
                {
                    var lb = (ListBox)s;
                    if (lb.IsVisible) { CloseStateConverter.Snapshot(_close); lb.Items.Refresh(); }
                };
            }
            // Lists with a checkbox per row: one Tab stop per row (the checkbox), and the row is selected while its
            // checkbox has focus, so Remove works from the keyboard.
            foreach (var n in new[] { "LaunchList", "StreamList" })
            {
                var lb = Get<ListBox>(n); if (lb == null) continue;
                var st = new Style(typeof(ListBoxItem), lb.ItemContainerStyle);
                st.Setters.Add(new Setter(Control.IsTabStopProperty, false));
                lb.ItemContainerStyle = st;
                // Selected as a local value (not a style trigger), so it stays selected when focus moves on to Remove.
                if (lb != launchList)
                    lb.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
                    {
                        var c = ItemsControl.ContainerFromElement((ItemsControl)s, e.OriginalSource as DependencyObject) as ListBoxItem;
                        if (c != null) c.IsSelected = true;
                    }));
            }
            Get<ListBox>("StreamList").ItemsSource = _stream;

            // Stream tab
            Get<CheckBox>("OptStream").IsChecked = _cfg.Streaming;
            Get<CheckBox>("OptStreamBoost").IsChecked = _cfg.StreamBoost;
            Get<CheckBox>("OptVram").IsChecked = _cfg.VramCheck;
            Get<CheckBox>("OptStream").Checked += (s, e) => { SaveConfig(); if (_on) StreamOn(); StartObsFeatures(); };
            Get<CheckBox>("OptStream").Unchecked += (s, e) => { SaveConfig(); if (_on) StreamOff(); StopObsFeatures(); };
            BindObs();
            foreach (var n in new[] { "OptStreamBoost", "OptVram" })
            {
                Get<CheckBox>(n).Checked += (s, e) => SaveConfig();
                Get<CheckBox>(n).Unchecked += (s, e) => SaveConfig();
            }
            Get<ListBox>("StreamList").AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) => SaveConfig()));
            Get<ListBox>("StreamList").AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((s, e) => SaveConfig()));
            Get<Button>("AddStreamBtn").Click += (s, e) => AddAppTo(_stream, "Stream");
            Get<Button>("RemoveStreamBtn").Click += (s, e) =>
            {
                var sel = Get<ListBox>("StreamList").SelectedItem as LaunchItem;
                if (sel == null) { Tick("Pick an app in the list first, then Remove"); return; }
                int at = _stream.IndexOf(sel); _stream.Remove(sel); SaveConfig();
                OfferUndo("Removed " + sel.Name + " from the streaming apps", () => { _stream.Insert(Math.Min(at, _stream.Count), sel); SaveConfig(); });
            };

            // iRacing graphics (Photo mode)
            _bindingGraphics = true;
            Get<CheckBox>("OptPhoto").IsChecked = _cfg.GraphicsMode == "Photo";
            _bindingGraphics = false;
            Get<CheckBox>("OptPhoto").Checked += (s, e) => SetGraphicsMode("Photo");
            Get<CheckBox>("OptPhoto").Unchecked += (s, e) => SetGraphicsMode("Race");
            Get<Button>("SaveRaceBtn").Click += (s, e) => SaveCurrentAs("Race");
            Get<Button>("SavePhotoBtn").Click += (s, e) => SaveCurrentAs("Photo");
            Get<Button>("RestoreGfxBtn").Click += (s, e) => ApplyGraphics("restore");
            // Race quality slider + Photo replay quality + Advanced editor (values are set in InitGraphicsLevels)
            Get<Slider>("RaceSlider").ValueChanged += (s, e) => OnRaceSlider((int)Math.Round(e.NewValue));
            for (int i = 1; i <= 3; i++) { int lv = i; Get<RadioButton>("PhotoLvl" + i).Checked += (s, e) => OnPhotoLevel(lv); }
            _gfxCommit.Tick += (s, e) => { _gfxCommit.Stop(); CommitGraphicsLevels(); };
            Get<ToggleButton>("AdvToggle").Checked += (s, e) => ShowAdvanced(true);
            Get<ToggleButton>("AdvToggle").Unchecked += (s, e) => ShowAdvanced(false);
            Get<TextBox>("AdvSearch").TextChanged += (s, e) => { _advFilter = Get<TextBox>("AdvSearch").Text.Trim(); BuildAdvanced(); };
            Get<RadioButton>("AdvRace").Checked += (s, e) => { _advPhoto = false; BuildAdvanced(); };
            Get<RadioButton>("AdvPhoto").Checked += (s, e) => { _advPhoto = true; BuildAdvanced(); };

            Get<CheckBox>("OptClose").IsChecked = _cfg.CloseApps;
            Get<CheckBox>("OptLaunch").IsChecked = _cfg.LaunchApps;
            Get<CheckBox>("OptIRacing").IsChecked = _cfg.StartIRacing;
            Get<CheckBox>("OptHigh").IsChecked = _cfg.SimHighPriority;
            Get<CheckBox>("OptLower").IsChecked = _cfg.LowerBackground;
            Get<CheckBox>("OptGpuWarn").IsChecked = _cfg.GpuWarning;
            Get<CheckBox>("OptAutoOff").IsChecked = _cfg.AutoOff;
            Get<CheckBox>("OptTray").IsChecked = _cfg.TrayOnClose;
            Get<CheckBox>("OptLog").IsChecked = _cfg.Logging;
            Get<TextBox>("LowerBox").Text = string.Join(", ", _cfg.Lower);
            Get<RadioButton>(_cfg.LogIntervalSec == 1 ? "Int1" : _cfg.LogIntervalSec == 5 ? "Int5" : "Int2").IsChecked = true;
            foreach (var n in new[] { "Int1", "Int2", "Int5" })
                Get<RadioButton>(n).Checked += (s, e) => { ReadOptionsFromUi(); _mon.SetInterval(_cfg.LogIntervalSec * 1000); SaveConfig(); };
            foreach (var n in new[] { "OptClose", "OptLaunch", "OptIRacing", "OptHigh", "OptLower", "OptGpuWarn", "OptAutoOff", "OptLog", "OptTray" })
            {
                Get<CheckBox>(n).Checked += (s, e) => SaveConfig();
                Get<CheckBox>(n).Unchecked += (s, e) => SaveConfig();
            }
            Get<TextBox>("LowerBox").LostFocus += (s, e) => SaveConfig();
            FillMonitorPick();
            Get<ComboBox>("MonitorPick").SelectionChanged += (s, e) => OnMonitorPicked();
            Get<ComboBox>("MonitorPick").DropDownOpened += (s, e) => { if (!_bindingMonitor) FillMonitorPick(); };
            BindUpdates();
            Get<CheckBox>("OptAutostart").IsChecked = Installer.StartsWithWindows;
            Get<CheckBox>("OptAutostart").Checked += (s, e) => SetAutostart(true);
            Get<CheckBox>("OptAutostart").Unchecked += (s, e) => SetAutostart(false);
            Get<ListBox>("LaunchList").AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) => SaveConfig()));
            Get<ListBox>("LaunchList").AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((s, e) => SaveConfig()));

            Get<Button>("AddLaunchBtn").Click += (s, e) => AddLaunchApp();
            Get<Button>("RemoveLaunchBtn").Click += (s, e) =>
            {
                var sel = Get<ListBox>("LaunchList").SelectedItem as LaunchItem;
                if (sel == null) { Tick("Pick an app in the list first, then Remove"); return; }
                int at = _launch.IndexOf(sel); _launch.Remove(sel); SaveConfig();
                OfferUndo("Removed " + sel.Name + " from the Launch list", () => { _launch.Insert(Math.Min(at, _launch.Count), sel); SaveConfig(); });
            };
            Get<Button>("AddCloseBtn").Click += (s, e) => AddClose(Get<TextBox>("NewCloseBox").Text);
            Get<TextBox>("NewCloseBox").KeyDown += (s, e) => { if (e.Key == Key.Enter) AddClose(Get<TextBox>("NewCloseBox").Text); };
            Get<Button>("RemoveCloseBtn").Click += (s, e) =>
            {
                var sel = Get<ListBox>("CloseList").SelectedItem as string;
                if (sel == null) { Tick("Pick an app in the list first, then Remove"); return; }
                int at = _close.IndexOf(sel); _close.Remove(sel); SaveConfig();
                OfferUndo("Removed " + FriendlyApp(sel) + " from the Close list", () => { if (!_close.Contains(sel)) _close.Insert(Math.Min(at, _close.Count), sel); SaveConfig(); });
            };
            Get<Button>("PickRunningBtn").Click += (s, e) => ToggleRunningPicker();
            Action addPicked = () =>
            {
                var rl = Get<ListBox>("RunningList");
                var it = rl.SelectedItem as RunItem; if (it == null || it.Name == null) return;
                var before = _close.Count;
                foreach (var n in it.Names != null && it.Names.Length > 0 ? it.Names : new[] { it.Name }) AddClose(n);
                if (_close.Count == before) return;   // nothing added (protected, already listed, or the user said no)
                var src = rl.ItemsSource as List<RunItem>; if (src != null) { src.Remove(it); rl.Items.Refresh(); }
            };
            Get<ListBox>("RunningList").MouseDoubleClick += (s, e) => addPicked();
            Get<ListBox>("RunningList").KeyDown += (s, e) => { if (e.Key == Key.Enter) { addPicked(); e.Handled = true; } };
            Get<Button>("RerunSetupBtn").Click += (s, e) => StartFirstRun();
            // Settings tab, CPU temperature: HWiNFO's state and the button that moves it on, refreshed while it shows.
            var hwPanel = Get<StackPanel>("HwinfoPanel");
            var hwTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            hwTimer.Tick += (s, e) => { if (hwPanel.IsVisible && !_helperBusy) RenderHwinfo(hwPanel, null); };
            hwTimer.Start();
            hwPanel.IsVisibleChanged += (s, e) => { if (hwPanel.IsVisible) RenderHwinfo(hwPanel, null); };

            Get<Button>("OpenLogsBtn").Click += (s, e) =>
            {
                Directory.CreateDirectory(LogsRoot);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + LogsRoot + "\"") { UseShellExecute = true });
            };
            Get<Button>("CopySummaryBtn").Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_lastSummary))
                {
                    try { Clipboard.SetText(_lastSummary); Tick("Summary copied to the clipboard."); }
                    catch { Tick("Another app is using the clipboard. Try again in a moment."); }
                }
                else Tick("No summary yet");
            };

            _bound = true;
            var sw = Get<ToggleButton>("Switch");
            sw.Checked += (s, e) => { if (!_suppressToggle) TurnOn(); };
            sw.Unchecked += (s, e) => { if (!_suppressToggle) TurnOff("switched off"); };
        }

        void ReadOptionsFromUi()
        {
            if (!_bound) return;
            _cfg.Launch = _launch.ToList();
            _cfg.Close = _close.ToList();
            _cfg.StreamLaunch = _stream.ToList();
            _cfg.Streaming = Get<CheckBox>("OptStream").IsChecked == true;
            _cfg.StreamBoost = Get<CheckBox>("OptStreamBoost").IsChecked == true;
            _cfg.VramCheck = Get<CheckBox>("OptVram").IsChecked == true;
            _cfg.GraphicsMode = Get<CheckBox>("OptPhoto").IsChecked == true ? "Photo" : "Race";
            _cfg.CloseApps = Get<CheckBox>("OptClose").IsChecked == true;
            _cfg.LaunchApps = Get<CheckBox>("OptLaunch").IsChecked == true;
            _cfg.StartIRacing = Get<CheckBox>("OptIRacing").IsChecked == true;
            _cfg.SimHighPriority = Get<CheckBox>("OptHigh").IsChecked == true;
            _cfg.LowerBackground = Get<CheckBox>("OptLower").IsChecked == true;
            _cfg.GpuWarning = Get<CheckBox>("OptGpuWarn").IsChecked == true;
            _cfg.AutoOff = Get<CheckBox>("OptAutoOff").IsChecked == true;
            _cfg.TrayOnClose = Get<CheckBox>("OptTray").IsChecked == true;
            _cfg.Logging = Get<CheckBox>("OptLog").IsChecked == true;
            _cfg.UpdateCheck = Get<CheckBox>("OptUpdateCheck").IsChecked == true;
            _cfg.UpdateTestVersions = Get<CheckBox>("OptTestVersions").IsChecked == true;
            _cfg.Lower = Get<TextBox>("LowerBox").Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => CleanName(x)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _cfg.LogIntervalSec = Get<RadioButton>("Int1").IsChecked == true ? 1 : Get<RadioButton>("Int5").IsChecked == true ? 5 : 2;
        }

        static string CleanName(string s)
        {
            s = (s ?? "").Trim();
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s;
        }

        void AddLaunchApp() { AddAppTo(_launch, "Launch"); }

        void AddClose(string raw)
        {
            var name = CleanName(raw);
            if (name.Length == 0) return;
            if (ProtectedFromClose(name)) { Tick(name + " is protected (iRacing, or on the Launch or streaming list)"); return; }
            if (_close.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase))) { Tick(name + " is already on the list"); return; }
            var app = AppScan.AppFor(name);
            if (app != null && !app.ClosableWhen(_cfg.Streaming) &&
                MessageBox.Show(Window, app.label + " is usually kept open while racing (" + app.why + ").\n\nClose it every time Race Mode turns on?", "Race Mode",
                                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _close.Add(name);
            Get<TextBox>("NewCloseBox").Text = "";
            SaveConfig();
            Tick("Added " + name + " to the Close list");
        }

        // Close tab "Choose Running…": the measured suggestions first (name, why, memory on the right), then every
        // other open app with a window. Double-click adds it.
        void ToggleRunningPicker()
        {
            var list = Get<ListBox>("RunningList");
            if (list.Visibility == Visibility.Visible) { list.Visibility = Visibility.Collapsed; return; }
            list.ItemTemplate = RunItemTemplate();
            list.ItemsSource = new List<RunItem> { new RunItem { Title = "Checking what's open…", Sub = "This takes a few seconds." } };
            list.Visibility = Visibility.Visible;
            StartCloseScan(_cfg.Streaming, () =>
            {
                if (list.Visibility != Visibility.Visible) return;   // closed while it was checking
                var items = new List<RunItem>();
                foreach (var a in _closeScan.Where(x => x.Open && !_close.Contains(x.Process, StringComparer.OrdinalIgnoreCase)))
                    items.Add(new RunItem { Name = a.Process, Names = a.Names, Title = a.Label, Sub = SuggestionWhy(a, "add"), Value = MemText(a.RamMB) });
                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    try { if (p.SessionId == MySession && p.MainWindowHandle != IntPtr.Zero && !ProtectedFromClose(p.ProcessName) && !_close.Contains(p.ProcessName)) names.Add(p.ProcessName); }
                    catch { }
                    finally { p.Dispose(); }
                }
                var rest = names.Where(n => !items.Any(i => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
                foreach (var n in rest)
                {
                    var app = AppScan.AppFor(n);
                    var kept = app != null && !app.ClosableWhen(_cfg.Streaming);
                    items.Add(new RunItem { Name = n, Title = FriendlyApp(n), Sub = kept ? "Usually kept open: " + app.why : (FriendlyApp(n) == n ? "" : n) });
                }
                if (items.Count == 0) items.Add(new RunItem { Title = "Nothing else is open that could be closed" });
                list.ItemsSource = items;
                Tick("Double-click an app (or press Enter) to add it");
            });
        }

        public class RunItem
        {
            public string[] Names;
            public string Name { get; set; }
            public string Title { get; set; }
            public string Sub { get; set; }
            public string Value { get; set; }
            public override string ToString() { return Title; }
        }

        // Two lines per app (name, then why) with the memory on the right.
        DataTemplate RunItemTemplate()
        {
            var grid = new FrameworkElementFactory(typeof(Grid));
            var c0 = new FrameworkElementFactory(typeof(ColumnDefinition)); c0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var c1 = new FrameworkElementFactory(typeof(ColumnDefinition)); c1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            grid.AppendChild(c0); grid.AppendChild(c1);
            grid.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
            var sp = new FrameworkElementFactory(typeof(StackPanel));
            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
            title.SetValue(TextBlock.FontSizeProperty, 13.0);
            title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            var sub = new FrameworkElementFactory(typeof(TextBlock));
            sub.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Sub"));
            var subStyle = new Style(typeof(TextBlock), (Style)Window.Resources["Sub"]);
            var empty = new Trigger { Property = TextBlock.TextProperty, Value = "" };
            empty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            subStyle.Triggers.Add(empty);
            sub.SetValue(FrameworkElement.StyleProperty, subStyle);
            sp.AppendChild(title); sp.AppendChild(sub);
            grid.AppendChild(sp);
            var val = new FrameworkElementFactory(typeof(TextBlock));
            val.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value"));
            val.SetValue(Grid.ColumnProperty, 1);
            val.SetValue(TextBlock.FontFamilyProperty, Window.Resources["Display"]);
            val.SetValue(TextBlock.FontSizeProperty, 14.0);
            val.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            val.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 4, 0));
            grid.AppendChild(val);
            return new DataTemplate { VisualTree = grid };
        }

        static bool InList(IEnumerable<LaunchItem> items, string name)
        {
            return items.Any(l => !string.IsNullOrEmpty(l.ProcessName) && string.Equals(l.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        }

        // Never closed: iRacing, Race Mode itself, Windows, every Launch app, and streaming apps while Streaming is on.
        bool ProtectedFromClose(string name)
        {
            if (AlwaysProtected.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))) return true;
            // streaming apps are only protected while Streaming is on (a racer who doesn't stream can close OBS)
            return InList(_launch, name) || (_cfg.Streaming && InList(_stream, name));
        }

        // Never lowered: iRacing etc. and streaming apps (OBS must keep its priority). Launch apps CAN be lowered
        // when you list them under Lower (e.g. Discord: started with Race Mode, but shouldn't compete with the sim).
        bool ProtectedFromLower(string name)
        {
            if (AlwaysProtected.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))) return true;
            return InList(_stream, name);
        }

        void AddAppTo(ObservableCollection<LaunchItem> list, string label)
        {
            var dlg = new OpenFileDialog { Title = "Pick an app to start with " + (label == "Stream" ? "Streaming" : "Race Mode"), Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files|*.*" };
            if (dlg.ShowDialog(Window) != true) return;
            var path = dlg.FileName;
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (list.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) { Tick(name + " is already on the list"); return; }
            var item = new LaunchItem { Name = name, Path = path, Args = "", ProcessName = "", Enabled = true };
            item = CleanItems(new List<LaunchItem> { item })[0];   // fills ProcessName from the exe, or a shortcut's target
            list.Add(item);
            SaveConfig();
            Tick("Added " + name + " to the " + (label == "Stream" ? "streaming apps" : "Launch list"));
        }

        // ---------------- live stats ----------------
        void SetRun(string name, string text) { var r = Window.FindName(name) as Run; if (r != null) r.Text = text; }
        void SetText(string name, string text) { var t = Get<TextBlock>(name); if (t != null) t.Text = text; }
        static string N0(double d) { return double.IsNaN(d) ? "--" : d.ToString("0", CultureInfo.InvariantCulture); }
        static string N1(double d) { return double.IsNaN(d) ? "--" : d.ToString("0.0", CultureInfo.InvariantCulture); }

        void RefreshStats()
        {
            var s = _mon.Live;
            if (s.Time == DateTime.MinValue) return;
            RefreshObs(s);
            SetRun("CpuVal", N0(s.Cpu));
            SetText("CpuSub", "Max core " + N0(s.CpuMaxCore) + "%");
            SetMeter("CpuBar", s.Cpu, 100, s.CpuMaxCore >= 95);
            var fahr = Monitor.HwinfoFahrenheit;
            SetRun("CpuTempUnit", fahr ? " °F" : " °C");
            SetRun("GpuTempUnit", fahr ? " °F" : " °C");
            SetRun("CpuTempVal", N0(fahr ? s.CpuTemp * 9 / 5 + 32 : s.CpuTemp));
            SetMeter("CpuTempBar", s.CpuTemp, 100, s.CpuTemp >= 85);
            if (Window.IsVisible) RefreshCpuTempHint(s);   // hidden in the tray: skip its process and file checks
            SetRun("GpuVal", N0(s.GpuUtil));
            SetText("GpuSub", double.IsNaN(s.GpuUtil) ? "NVIDIA only" : N0(s.GpuPower) + " W");
            SetMeter("GpuBar", s.GpuUtil, 100, false);
            SetRun("GpuTempVal", N0(fahr ? s.GpuTemp * 9 / 5 + 32 : s.GpuTemp));
            SetText("GpuTempSub", double.IsNaN(s.GpuTemp) ? "NVIDIA only" : "VRAM " + N1(s.VramUsedMB / 1024.0) + " GB");
            SetMeter("GpuTempBar", s.GpuTemp, 100, s.GpuTemp >= 83);
            SetRun("RamVal", N1(s.RamTotalGB - s.RamUsedGB));
            SetText("RamSub", "Used " + N0(s.RamUsedGB) + "/" + N0(s.RamTotalGB) + " GB");
            var commitHot = s.CommitLimitGB > 0 && s.CommitGB > s.CommitLimitGB * 0.9;
            var ramHot = s.RamTotalGB > 0 && s.RamUsedGB > s.RamTotalGB * 0.9;
            SetMeter("RamBar", s.RamTotalGB > 0 ? 100.0 * s.RamUsedGB / s.RamTotalGB : double.NaN, 100, ramHot || commitHot);
            Get<TextBlock>("RamSub").Foreground = commitHot ? (Brush)Window.Resources["Warn"] : (Brush)Window.Resources["Faint"];
            FeedDashboard(s);
        }

        void SetMeter(string name, double value, double max, bool hot)
        {
            var bar = Get<ProgressBar>(name);
            if (bar == null) return;
            bar.Maximum = max;
            bar.Value = double.IsNaN(value) ? 0 : Math.Max(0, Math.Min(max, value));
            bar.Foreground = hot ? (Brush)Window.Resources["Warn"] : (Brush)Window.Resources["Ink"];
        }

        // CPU temperature comes from HWiNFO's Gadget registry values. Explain what is missing instead of a bare "--".
        DateTime _hwinfoCheckedAt = DateTime.MinValue;
        bool _hwinfoRunning;
        static string HwinfoExe
        {
            get { return InstallFinder.HwinfoExe(); }   // registry first, then Program Files (x86 too), then the default path
        }

        void RefreshCpuTempHint(LiveStats s)
        {
            var cell = Get<Border>("CpuTempCell");
            var sub = Get<TextBlock>("CpuTempSub");
            var bar = Get<ProgressBar>("CpuTempBar");
            if (!double.IsNaN(s.CpuTemp))
            {
                SetText("CpuTempSub", s.CpuMHz > 0 ? (s.CpuMHz / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " GHz" : "");
                if (sub != null) { sub.Foreground = (Brush)Window.Resources["Faint"]; sub.TextDecorations = null; }
                if (bar != null) bar.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
                if (cell != null) { cell.Cursor = null; cell.ToolTip = "CPU package temperature, from HWiNFO"; }
                return;
            }
            // No reading: say so in words, and make the tile itself the way to fix it (no dead gauge).
            SetRun("CpuTempVal", "");
            SetRun("CpuTempUnit", "Not set up");
            if (bar != null) bar.Visibility = Visibility.Hidden;
            if (sub != null) { sub.Foreground = (Brush)Window.Resources["Ink"]; sub.TextDecorations = TextDecorations.Underline; }
            if (cell != null) cell.Cursor = Cursors.Hand;
            if ((DateTime.Now - _hwinfoCheckedAt).TotalSeconds >= 5)
            {
                _hwinfoCheckedAt = DateTime.Now;
                var ps = Process.GetProcessesByName("HWiNFO64"); _hwinfoRunning = ps.Length > 0; foreach (var p in ps) p.Dispose();
            }
            if (_hwinfoRunning)
            {
                SetText("CpuTempSub", "Set up");
                if (cell != null) cell.ToolTip = "HWiNFO is running but isn't sharing the CPU temperature yet (" + (Monitor.HwinfoReason ?? "no reading") + "). Click for the three steps.";
            }
            else if (File.Exists(HwinfoExe))
            {
                SetText("CpuTempSub", "Open HWiNFO");
                if (cell != null) cell.ToolTip = "CPU temperature comes from HWiNFO, which isn't running. Click to open it.\nTip: in HWiNFO's settings, tick Auto Start so it's always there.";
            }
            else
            {
                SetText("CpuTempSub", "Set up");
                if (cell != null) cell.ToolTip = "CPU temperature needs HWiNFO, a free sensor app. Click to install it.";
            }
        }

        void CpuTempAction()
        {
            if (!double.IsNaN(_mon.Live.CpuTemp)) return;
            var tabs = Get<TabControl>("Tabs");
            var item = tabs == null ? null : tabs.Items.OfType<TabItem>().FirstOrDefault(t => "Settings".Equals(t.Header as string));
            if (item != null) tabs.SelectedItem = item;
            Window.Dispatcher.BeginInvoke(new Action(() => { var p = Get<StackPanel>("HwinfoPanel"); if (p != null) { RenderHwinfo(p, null); p.BringIntoView(); } }), DispatcherPriority.Loaded);
        }

        void StartHwinfo()
        {
            if (!double.IsNaN(_mon.Live.CpuTemp) || _hwinfoRunning || !File.Exists(HwinfoExe)) return;
            try
            {
                Process.Start(new ProcessStartInfo(HwinfoExe) { UseShellExecute = true });
                _hwinfoCheckedAt = DateTime.MinValue;
                Tick("Starting HWiNFO…");
            }
            catch (Exception ex) { Tick("Couldn't start HWiNFO: " + ex.Message); }
        }

        // Used by --snapshot ... on to render the ON look without touching any process.
        public void PreviewOn()
        {
            SetSwitch(true);
            SetPhase("active");
            SetText("StatusText", "Ready to race");
            var hc = Get<System.Windows.Controls.Primitives.UniformGrid>("HeroChecks");
            if (hc == null) return;
            hc.Children.Clear(); hc.Visibility = Visibility.Visible;
            hc.Children.Add(MiniCheck("Apps", "3 of 3 running", "Live"));
            hc.Children.Add(MiniCheck("Close list", "all closed", "Live"));
            hc.Children.Add(MiniCheck("iRacing", "racing, put first", "Live"));
            hc.Children.Add(MiniCheck("Screen", "OK", "Live"));
            hc.Children.Add(MiniCheck("Race graphics", "set", "Live"));
            hc.Children.Add(MiniCheck("Background", "5 slowed down", "Live"));
        }

        // Windows 11: ask DWM for rounded corners on the borderless window.
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void RoundCorners()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(Window).Handle;
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
            }
            catch { }
        }

        void ShowPowerPlan()
        {
            string text = "Unknown";
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes"))
                {
                    var guid = (k == null ? "" : Convert.ToString(k.GetValue("ActivePowerScheme"))).ToLowerInvariant();
                    var known = new Dictionary<string, string> {
                        { "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "High Performance" },
                        { "e9a42b02-d5df-448d-aa00-03f14749eb61", "Ultimate Performance" },
                        { "381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced. High Performance is better for racing." },
                        { "a1841308-3541-4fab-bc81-f71556f20b4a", "Power Saver. Switch to High Performance." } };
                    string label;
                    text = known.TryGetValue(guid, out label) ? label : "Custom";
                }
            }
            catch { }
            SetText("PowerText", text);
        }

        // The Logs tab: a real summary shows as log text (mono, no wrapping); before the first race it's a plain sentence.
        void ShowSummary(string text)
        {
            _lastSummary = text;
            var box = Get<TextBox>("SummaryBox");
            box.Text = text;
            box.VerticalAlignment = VerticalAlignment.Stretch;
            box.FontFamily = (FontFamily)Window.Resources["Mono"]; box.FontSize = 11; box.TextWrapping = TextWrapping.NoWrap;
            Get<Button>("CopySummaryBtn").IsEnabled = true;
            ShowLastSession();
        }

        void LoadLastSummary()
        {
            try
            {
                if (!Directory.Exists(LogsRoot)) return;
                var last = new DirectoryInfo(LogsRoot).GetDirectories().OrderByDescending(d => d.Name)
                    .Select(d => System.IO.Path.Combine(d.FullName, "summary.txt")).FirstOrDefault(File.Exists);
                if (last != null) ShowSummary(File.ReadAllText(last));
            }
            catch { }
        }

        // ---------------- on / off ----------------
        void SetSwitch(bool on)
        {
            _suppressToggle = true;
            Get<ToggleButton>("Switch").IsChecked = on;
            _suppressToggle = false;
        }

        // irSidekick's graphics profiles can re-apply old settings over the Race preset. Warn once per app run
        // (activity line + events.log, no dialog); RaceMode never fights it by rewriting in a loop.
        bool _sidekickWarned;
        void WarnIrSidekick()
        {
            if (_sidekickWarned) return;
            try
            {
                bool found = false;
                foreach (var name in new[] { "irSidekickProfiles", "irSidekickLaunch", "irSidekick" })
                {
                    var ps = Process.GetProcessesByName(name);
                    if (ps.Length > 0) found = true;
                    foreach (var p in ps) p.Dispose();
                    if (found) break;
                }
                if (!found) return;
                _sidekickWarned = true;
                const string msg = "irSidekick is running: its graphics profile can undo Race settings. Update its profile from iRacing, or turn off its auto-apply";
                Tick(msg);
                _mon.Event(msg);
            }
            catch (Exception ex) { LogCrash(ex); }
        }

        void TurnOn()
        {
            ReadOptionsFromUi();
            SaveConfig();

            if (_cfg.GpuWarning && !GpuCheckOk()) { SetSwitch(false); Tick("Didn't turn on: the graphics card was busy"); return; }

            _on = true; _simSeen = false; _uiSeen = false; _simBoosted = false; _goneTicks = 0; _lowered.Clear(); _boosted.Clear();
            _simLoggedPid = -1; _prioNoted.Clear();
            _onAt = DateTime.Now; _issues.Clear(); _started.Clear(); _alreadyOpen.Clear(); _closed.Clear(); _notInstalled.Clear(); _leftOpen.Clear(); _reopened.Clear(); _runId++;
            _forced = 0; _confirmedRunning.Clear(); _vramWarned = false; _activation = "Turning on…";
            SetPhase("turning");
            try
            {
                if (_cfg.Logging)
                {
                    var dir = System.IO.Path.Combine(LogsRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
                    _mon.SetInterval(_cfg.LogIntervalSec * 1000);
                    _mon.FpsCap = FpsCap();
                    try { _mon.StartLogging(dir); } catch (Exception ex) { Tick("Couldn't start the log: " + ex.Message); }
                }
                _mon.Event("Race Mode ON");

                if (_cfg.CloseApps) CloseApps();
                if (_cfg.LaunchApps) LaunchItems(_launch.Where(i => i.Enabled), "app");
                if (_cfg.Streaming) LaunchItems(_stream.Where(i => i.Enabled), "streaming app");
                CheckDisplay("before launch", true);
                ApplyGraphics("before launch");
                WarnIrSidekick();
                if (_cfg.StartIRacing) StartIRacing();
                if (_cfg.LowerBackground) LowerBackground();
                BoostStreamApps();

                UpdateStatus();
                _watch.Start();
                _confirmTimer.Start();
            }
            catch (Exception ex)
            {
                // Never leave Race Mode half on: undo whatever already happened.
                LogCrash(ex);
                TurnOff("error while turning on");
                Tick("Couldn't turn on: " + ex.Message + ". Everything was put back.");
            }
        }

        void TurnOff(string why)
        {
            if (!_on) return;
            _on = false;
            _watch.Stop();
            _confirmTimer.Stop();
            _confirmedRunning.Clear();
            _activation = "";
            if (_killTimer != null) { _killTimer.Stop(); _killTimer = null; }
            int restored = RestorePriorities();
            _mon.Event("Race Mode OFF (" + why + "), restored " + restored + " priorities");
            string summary = null;
            try { summary = _mon.StopLogging(); } catch { }
            if (summary != null) ShowSummary(summary);
            SetSwitch(false);
            SetPhase("off");
            SetText("StatusText", "Turn it on before you race.");
            Tick("Turned off (" + why + ")" + (restored > 0 ? " · " + restored + " priorities restored" : "") + (summary != null ? " · log saved" : ""));
        }

        // Puts every process Race Mode changed back to Normal: lowered background jobs and the boosted sim.
        // Checks the name first so a reused PID (process ended, new one got its number) is left alone.
        // Safe to call more than once and from the crash/exit handlers.
        public int RestorePriorities()
        {
            int restored = 0;
            foreach (var kv in _lowered.Concat(_boosted).ToList())
            {
                try
                {
                    using (var p = Process.GetProcessById(kv.Key))
                    {
                        if (!string.Equals(p.ProcessName, kv.Value, StringComparison.OrdinalIgnoreCase)) continue;
                        if (p.PriorityClass != ProcessPriorityClass.Normal) { p.PriorityClass = ProcessPriorityClass.Normal; restored++; }
                    }
                }
                catch { }
            }
            _lowered.Clear(); _boosted.Clear();
            return restored;
        }

        // The icon PNG is embedded in the exe (build.cmd /resource), so nothing extra has to sit next to it.
        void LoadAppIcon() { ApplyIcon(Window); }

        public static void ApplyIcon(Window w)
        {
            try
            {
                using (var s = typeof(Shell).Assembly.GetManifestResourceStream("icon.png"))
                {
                    if (s == null) return;
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.StreamSource = s; bmp.EndInit(); bmp.Freeze();
                    var img = w.FindName("AppIcon") as Image; if (img != null) img.Source = bmp;
                    w.Icon = bmp;
                }
            }
            catch { }
        }

        public static void LogCrash(Exception ex)
        {
            try { File.AppendAllText(System.IO.Path.Combine(Data, "crash.log"), DateTime.Now + "  " + ex + Environment.NewLine); } catch { }
        }

        bool GpuCheckOk()
        {
            var s = _mon.Live;
            if (s.SimRunning || double.IsNaN(s.GpuUtil) || s.GpuUtil < 50) return true;
            // While streaming, OBS using the GPU is expected, so it isn't treated as a stray job.
            var suspects = new[] { "python", "ffmpeg", "HandBrake", "obs64", "blender" }
                .Where(n => !(_cfg.Streaming && n == "obs64"))
                .Where(n => { var ps = Process.GetProcessesByName(n); var any = ps.Length > 0; foreach (var p in ps) p.Dispose(); return any; }).ToList();
            var msg = "Your graphics card is already at " + N0(s.GpuUtil) + "% before iRacing has started.\n\n" +
                      (suspects.Count > 0 ? "Running now: " + string.Join(", ", suspects) + "\n\n" : "") +
                      "Something else is using it (a video export, a render or another game), and iRacing would have to share it.\n\nTurn Race Mode on anyway?";
            return MessageBox.Show(Window, msg, "Race Mode", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        // Apps Race Mode chose to leave open after asking (process name -> why), so turning on doesn't wait for them.
        readonly Dictionary<string, string> _leftOpen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Apps that came straight back after closing (process names), kept apart so FinishActivation lists them again.
        readonly List<string> _reopened = new List<string>();
        int _runId;   // +1 on every TurnOn, so a timer from an earlier run does nothing

        public enum PidState { Gone, Close, Window, Admin }

        // Which pids may be force-closed: only those of apps with no pid left open. If any pid of an app still shows
        // a window (maybe a "Save changes?" prompt) or runs as administrator, none of that app's pids are killed,
        // because killing a helper can take the open document down with it.
        public static List<int> PidsToKill(IDictionary<int, string> pids, IDictionary<int, PidState> states)
        {
            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in states) if ((kv.Value == PidState.Window || kv.Value == PidState.Admin) && pids.ContainsKey(kv.Key)) held.Add(pids[kv.Key]);
            return states.Where(kv => kv.Value == PidState.Close && pids.ContainsKey(kv.Key) && !held.Contains(pids[kv.Key])).Select(kv => kv.Key).OrderBy(k => k).ToList();
        }

        void CloseApps()
        {
            var names = _close.Where(n => !ProtectedFromClose(n)).ToList();
            var asked = new List<string>();
            var pids = new Dictionary<int, string>();   // every process asked to close, by id: only these can be force-closed
            foreach (var name in names)
            {
                if (string.Equals(name, "steam", StringComparison.OrdinalIgnoreCase))
                {
                    if (new[] { "vrserver", "vrmonitor", "vrcompositor" }.Any(IsRunning)) { _mon.Event("Left Steam open: SteamVR is running"); continue; }
                    var steam = Mine("steam");
                    if (steam.Length > 0)
                    {
                        string exe = null; try { exe = steam[0].MainModule.FileName; } catch { }
                        if (exe == null) exe = InstallFinder.SteamExe();
                        try { Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = true }); asked.Add("steam"); _closed.Add("steam"); _mon.Event("Asked Steam to shut down"); } catch { }
                    }
                    foreach (var p in steam) p.Dispose();
                    continue;
                }
                var procs = Mine(name).Where(p => { bool ok = AppScan.BelongsTo(name, AppScan.ImagePath(p.Id)); if (!ok) p.Dispose(); return ok; }).ToArray();
                if (procs.Length == 0) continue;
                foreach (var p in procs) { try { pids[p.Id] = name; if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { } finally { p.Dispose(); } }
                asked.Add(name);
                _closed.Add(name);
                _mon.Event("Asked " + name + " to close (" + procs.Length + " processes)");
            }
            if (asked.Count == 0) { Tick("Nothing on the Close list was running"); return; }
            Tick("Closing " + asked.Count + " app" + (asked.Count == 1 ? "" : "s") + "…");
            int run = _runId;
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _killTimer = later;
            later.Tick += (s, e) =>
            {
                later.Stop();
                if (_killTimer != later || !_on) return; // turned off (or on again) within the 5 s: don't force-close anything
                _killTimer = null;
                int forced = 0;
                var waiting = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var denied = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                const string adminWhy = "runs as administrator, so Windows doesn't let Race Mode close it";
                // Pass 1: what each pid is doing now. Nothing is closed yet.
                var states = new Dictionary<int, PidState>();
                foreach (var kv in pids)
                {
                    states[kv.Key] = PidState.Gone;
                    if (ProtectedFromClose(kv.Value)) continue;   // protected since (Streaming turned on, added to Launch)
                    Process p;
                    try { p = Process.GetProcessById(kv.Key); } catch { continue; }   // already gone
                    try
                    {
                        if (p.HasExited || !string.Equals(p.ProcessName, kv.Value, StringComparison.OrdinalIgnoreCase)) continue;   // gone, or the id was reused
                        // runs as administrator: Windows doesn't let Race Mode close it
                        bool locked = false; try { var h = p.Handle; } catch (System.ComponentModel.Win32Exception) { locked = true; }
                        if (locked) { states[kv.Key] = PidState.Admin; denied.Add(FriendlyApp(kv.Value)); _leftOpen[kv.Value] = adminWhy; continue; }
                        // still showing a window: probably a "Save changes?" prompt, so it's left for the user
                        if (p.MainWindowHandle != IntPtr.Zero) { states[kv.Key] = PidState.Window; waiting.Add(FriendlyApp(kv.Value)); _leftOpen[kv.Value] = "is still open: it may be asking you to save"; continue; }
                        states[kv.Key] = PidState.Close;
                    }
                    catch (System.ComponentModel.Win32Exception) { states[kv.Key] = PidState.Admin; denied.Add(FriendlyApp(kv.Value)); _leftOpen[kv.Value] = adminWhy; }
                    catch { }
                    finally { p.Dispose(); }
                }
                // Pass 2: force-close only apps with nothing left open (see PidsToKill).
                foreach (var id in PidsToKill(pids, states))
                {
                    var name = pids[id];
                    Process p;
                    try { p = Process.GetProcessById(id); } catch { continue; }
                    try
                    {
                        if (p.HasExited || !string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) continue;
                        p.Kill(); forced++;
                    }
                    catch (System.ComponentModel.Win32Exception) { denied.Add(FriendlyApp(name)); _leftOpen[name] = adminWhy; }
                    catch { }
                    finally { p.Dispose(); }
                }
                _forced += forced;
                // Apps left open on purpose (save prompt, admin) were asked to close but didn't: don't count them as closed.
                _closed.RemoveAll(n => _leftOpen.ContainsKey(n));
                if (forced > 0) _mon.Event("Force-closed " + forced + " leftover processes");
                if (waiting.Count > 0) _mon.Event("Left open (still showing a window): " + string.Join(", ", waiting.ToArray()));
                if (denied.Count > 0) _mon.Event("Couldn't close (runs as administrator): " + string.Join(", ", denied.ToArray()));
                var notes = new List<string>();
                if (denied.Count > 0) notes.Add(string.Join(", ", denied.ToArray()) + " runs as administrator, so Windows doesn't let Race Mode close it");
                if (waiting.Count > 0) notes.Add(string.Join(", ", waiting.ToArray()) + (waiting.Count == 1 ? " is" : " are") + " still open: it may be asking you to save");
                Tick(notes.Count > 0 ? string.Join(". ", notes.ToArray())
                     : "Closed " + asked.Count + " app" + (asked.Count == 1 ? "" : "s") + (forced > 0 ? " (" + forced + " forced)" : ""));
                // 10 s later: anything that came straight back was reopened by its own background service
                var recheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                recheck.Tick += (s2, e2) =>
                {
                    recheck.Stop();
                    if (!_on || run != _runId) return;   // turned off, or a timer from an earlier run
                    var back = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    var backNames = new List<string>();
                    // Apps left open on purpose (save prompt, admin) never closed, so they didn't "come back".
                    foreach (var name in asked.Where(n => !string.Equals(n, "steam", StringComparison.OrdinalIgnoreCase) && !ProtectedFromClose(n) && !_leftOpen.ContainsKey(n)))
                        foreach (var p in Mine(name))
                        {
                            try { if (!pids.ContainsKey(p.Id) && AppScan.BelongsTo(name, AppScan.ImagePath(p.Id))) { back.Add(FriendlyApp(name)); if (!backNames.Contains(name)) backNames.Add(name); } } catch { }
                            finally { p.Dispose(); }
                        }
                    if (back.Count == 0) return;
                    _mon.Event("Reopened by itself after closing: " + string.Join(", ", back.ToArray()));
                    foreach (var n in backNames) if (!_reopened.Contains(n)) _reopened.Add(n);
                    // Still confirming: FinishActivation lists them from _reopened. Already confirmed: add them now.
                    if (_confirmTimer.IsEnabled == false)
                    {
                        foreach (var b in back) if (!_issues.Contains(b + " reopened itself")) _issues.Add(b + " reopened itself");
                        _activation = NeedsALook(); SetPhase("issues"); UpdateStatus();
                    }
                    Tick(string.Join(", ", back.ToArray()) + (back.Count == 1 ? " reopened itself" : " reopened themselves") +
                         " (a background service starts it again). Turn off its start-with-Windows option in its own settings, or take it off the Close list");
                };
                recheck.Start();
            };
            later.Start();
        }

        // Starts each item unless it's already running (so nothing is ever opened twice) and records what happened:
        // started, already open, not installed, failed. ConfirmTick then checks that each one really came up.
        void LaunchItems(IEnumerable<LaunchItem> items, string label)
        {
            int started = 0;
            foreach (var item in items.ToList())
            {
                if (!item.Installed) { _notInstalled.Add(item.Name); _mon.Event(item.Name + " is not installed (" + item.Path + ")"); continue; }
                if (!string.IsNullOrEmpty(item.ProcessName) && IsRunning(item.ProcessName))
                {
                    _alreadyOpen.Add(item.Name);
                    _mon.Event(item.Name + " already open, not started again");
                    continue;
                }
                try
                {
                    var psi = new ProcessStartInfo(item.Path, item.Args ?? "") { UseShellExecute = true };
                    var dir = System.IO.Path.GetDirectoryName(item.Path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                    Process.Start(psi);
                    started++;
                    _started.Add(item.Name);
                    _mon.Event("Started " + item.Name);
                }
                catch (Exception ex) { _issues.Add(item.Name + " couldn't start"); Tick("Couldn't start " + item.Name + ": " + ex.Message); _mon.Event("Failed to start " + item.Name + ": " + ex.Message); }
            }
            if (started > 0) Tick("Started " + started + " " + label + (started == 1 ? "" : "s"));
        }

        void StartIRacing()
        {
            var ui = Mine("iRacingUI"); var open = ui.Length > 0; foreach (var p in ui) p.Dispose();
            if (open) return;
            if (!File.Exists(_cfg.IRacingUI)) { Tick("iRacing UI not found at " + _cfg.IRacingUI); return; }
            try
            {
                Process.Start(new ProcessStartInfo(_cfg.IRacingUI) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(_cfg.IRacingUI) });
                _mon.Event("Started iRacing UI");
            }
            catch (Exception ex) { Tick("Couldn't start iRacing: " + ex.Message); }
        }

        // Every 5 s while the sim isn't running: re-check only when the ini or the monitor layout changed.
        void GuardTick()
        {
            if (_guardBusy || _cfg == null) return;
            if (IsRunning(Monitor.SimName)) return; // never touch the file while the sim has it loaded
            // A Photo/Race switch made while iRacing was open is applied now that it's closed.
            if (_graphicsPending) ApplyGraphics("iRacing closed");
            if (_cfg.DisplayGuard != null && _cfg.DisplayGuard.Enabled && DisplayGuard.Signature(_cfg.DisplayGuard) != _guardSig)
                CheckDisplay(_guardSig == null ? "app started" : "settings or monitors changed", false);
            // iRacing (or you, in its menus) changed the file: compare with the preset, but don't undo it.
            if (FileStamp(IniPath()) != _gfxStamp) RefreshGraphicsStatus();
        }

        // ---------------- racing monitor picker (Settings tab) ----------------
        List<MonitorInfo> _pickMonitors = new List<MonitorInfo>();
        bool _bindingMonitor;

        string MonitorLabel(MonitorInfo m)
        {
            string where = "";
            var main = _pickMonitors == null ? null : _pickMonitors.FirstOrDefault(x => x.Primary);
            if (m.Primary) where = " · main screen";
            else if (main != null) where = m.X + m.Width <= main.X ? " · left of main" : m.X >= main.X + main.Width ? " · right of main" : m.Y + m.Height <= main.Y ? " · above main" : m.Y >= main.Y + main.Height ? " · below main" : "";
            bool twin = _pickMonitors != null && _pickMonitors.Count(x => x.Width == m.Width && x.Height == m.Height && x.Hz == m.Hz) > 1;
            return "Screen " + (m.Index + 1) + " · " + m.Width + " × " + m.Height + " · " + m.Hz + " Hz" + where
                + (DisplayGuard.LooksJoined(m) ? " · looks like joined screens (Surround or Eyefinity)" : "")
                + (twin && !string.IsNullOrEmpty(m.MonitorId) ? "  (" + m.MonitorId + ")" : "");
        }

        // Rows of three same-size screens side by side, offered after the single screens.
        List<List<MonitorInfo>> _pickTriples = new List<List<MonitorInfo>>();

        static string TripleLabel(List<MonitorInfo> row)
        {
            return "Three screens side by side · screens " + string.Join(", ", row.Select(m => (m.Index + 1).ToString()).ToArray())
                + " · 3 × " + row[0].Width + " × " + row[0].Height + " · " + row.Min(m => m.Hz) + " Hz";
        }

        void FillMonitorPick()
        {
            var box = Get<ComboBox>("MonitorPick"); if (box == null) return;
            _bindingMonitor = true;
            try
            {
                try { _pickMonitors = DisplayGuard.ListMonitors(); } catch { _pickMonitors = new List<MonitorInfo>(); }
                try { _pickTriples = DisplayGuard.TripleRows(_pickMonitors); } catch { _pickTriples = new List<List<MonitorInfo>>(); }
                box.Items.Clear();
                box.Items.Add("Don't check (leave iRacing's screen settings alone)");
                int sel = 0;
                var g = _cfg.DisplayGuard;
                bool triple = g != null && g.Enabled && g.IsTriple;
                foreach (var m in _pickMonitors)
                {
                    box.Items.Add(MonitorLabel(m));
                    if (sel == 0 && !triple && g != null && g.Enabled && !string.IsNullOrEmpty(g.MonitorId) && (m.MonitorId ?? "").IndexOf(g.MonitorId, StringComparison.OrdinalIgnoreCase) >= 0) sel = box.Items.Count - 1;
                }
                foreach (var row in _pickTriples)
                {
                    box.Items.Add(TripleLabel(row));
                    if (sel == 0 && triple && Enumerable.Range(0, 3).All(k => (row[k].MonitorId ?? "").IndexOf(g.Triple[k], StringComparison.OrdinalIgnoreCase) >= 0)) sel = box.Items.Count - 1;
                }
                // The picked monitor is switched off right now: keep the choice and say so.
                if (sel == 0 && g != null && g.Enabled && !string.IsNullOrEmpty(g.MonitorId))
                {
                    box.Items.Add(triple ? "Your three racing screens · not all connected side by side right now" : "Your racing monitor (" + g.MonitorId + ") · not connected right now");
                    sel = box.Items.Count - 1;
                }
                box.SelectedIndex = sel;
            }
            finally { _bindingMonitor = false; }
        }

        void OnMonitorPicked()
        {
            if (_bindingMonitor) return;
            var box = Get<ComboBox>("MonitorPick");
            int i = box.SelectedIndex;
            var g = _cfg.DisplayGuard ?? (_cfg.DisplayGuard = new DisplayGuardConfig());
            if (i <= 0) { g.Enabled = false; SaveConfig(); _guardSig = null; Tick("Screen check off: iRacing's screen settings are left alone"); UpdateStatus(); return; }
            if (i > _pickMonitors.Count + _pickTriples.Count) return;   // the "not connected" entry: nothing changes
            string label;
            if (i > _pickMonitors.Count)
            {
                var row = _pickTriples[i - 1 - _pickMonitors.Count];
                if (row.Any(x => string.IsNullOrEmpty(x.MonitorId) || x.Hz <= 0)) { Tick("Couldn't read those screens' details, so nothing changed"); FillMonitorPick(); return; }
                g.Triple = row.Select(x => x.MonitorId).ToList();
                g.MonitorId = row[1].MonitorId; g.Width = row[0].Width; g.Height = row[0].Height; g.RefreshRate = row.Min(x => x.Hz);
                label = TripleLabel(row);
            }
            else
            {
                var m = _pickMonitors[i - 1];
                if (string.IsNullOrEmpty(m.MonitorId) || m.Width <= 0 || m.Height <= 0 || m.Hz <= 0) { Tick("Couldn't read that screen's details, so nothing changed"); FillMonitorPick(); return; }
                g.Triple = null;
                g.MonitorId = m.MonitorId; g.Width = m.Width; g.Height = m.Height; g.RefreshRate = m.Hz;
                label = MonitorLabel(m);
            }
            g.Enabled = true;
            SaveConfig();
            _guardSig = null;
            Tick("Racing monitor set to " + label + (g.EnforcesFpsCap ? ", FPS cap kept at " + g.FpsLimit : ""));
            CheckDisplay("monitor picked", true);
            UpdateStatus();
        }

        // iRacing's own cap from its settings file (0 = no cap or unreadable).
        int ReadIniFpsCap()
        {
            try
            {
                var ini = IniFile.Read(IniPath());
                Dictionary<string, string> k; string v, lim; int n;
                if (ini.TryGetValue("Graphics Options", out k) && k.TryGetValue("LimitFrameRate", out lim) && lim.Trim() == "1"
                    && k.TryGetValue("DesiredFPSLimit", out v) && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) return n;
            }
            catch { }
            return 0;
        }

        // ---------------- updates (Settings tab, Updater.cs) ----------------
        readonly DispatcherTimer _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        UpdateInfo _update;
        bool _updateBusy, _updateInstalled;   // installed = waiting for a restart; never offered or installed twice

        void SetAutostart(bool on)
        {
            try { Installer.SetStartWithWindows(on); Tick(on ? "Race Mode will start with Windows (minimised)" : "Race Mode won't start with Windows"); }
            catch (Exception ex) { Tick("Couldn't change Start with Windows: " + ex.Message); }
        }

        void BindUpdates()
        {
            SetText("VersionText", "Race Mode " + (Updater.Enabled ? Updater.Current : "(local build)"));
            Get<CheckBox>("OptUpdateCheck").IsChecked = _cfg.UpdateCheck;
            Get<CheckBox>("OptTestVersions").IsChecked = _cfg.UpdateTestVersions;
            Get<CheckBox>("OptUpdateCheck").Checked += (s, e) => SaveConfig();
            Get<CheckBox>("OptUpdateCheck").Unchecked += (s, e) => SaveConfig();
            Get<CheckBox>("OptTestVersions").Checked += (s, e) => { SaveConfig(); if (_cfg.UpdateCheck) CheckForUpdate(false); };
            Get<CheckBox>("OptTestVersions").Unchecked += (s, e) => { SaveConfig(); if (_cfg.UpdateCheck) CheckForUpdate(false); };
            Get<Button>("UpdateBtn").Click += (s, e) => { if (_update != null) InstallUpdate(); else CheckForUpdate(false); };
            if (!Updater.Enabled)
            {
                SetText("UpdateText", "Built on this PC, so it doesn't update itself");
                Get<Button>("UpdateBtn").IsEnabled = false;
                return;
            }
            // First check 45 s after start (out of the way of the race-start rush), then every 12 hours.
            _updateTimer.Tick += (s, e) => { _updateTimer.Interval = TimeSpan.FromHours(12); if (_cfg.UpdateCheck) CheckForUpdate(true); };
            _updateTimer.Start();
        }

        void CheckForUpdate(bool quiet)
        {
            if (!Updater.Enabled || _updateBusy || _updateInstalled) return;
            _updateBusy = true;
            bool test = _cfg.UpdateTestVersions;
            SetText("UpdateText", "Checking…");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateInfo u = null; string err = null;
                try { u = Updater.Check(test); } catch (Exception ex) { err = ex.Message; }
                Window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _updateBusy = false;
                    _update = u;
                    if (err != null) SetText("UpdateText", quiet ? "Couldn't check for updates (no internet?)" : "Couldn't check: " + err);
                    else if (u == null) SetText("UpdateText", "Up to date · checked " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture));
                    else
                    {
                        SetText("UpdateText", (u.Prerelease ? "Test version " : "Version ") + u.Version + " is ready");
                        Tick("Race Mode " + u.Version + " is ready to install (Settings tab, Updates)");
                    }
                    Get<Button>("UpdateBtn").Content = u != null ? "Update" : "Check Now";
                }));
            });
        }

        void InstallUpdate()
        {
            if (_update == null || _updateBusy || _updateInstalled) return;
            // Never mid-race: the swap restarts Race Mode, which would put priorities and apps back.
            if (_on || IsRunning(Monitor.SimName)) { SetText("UpdateText", "Turn Race Mode off and close iRacing first, then click Update"); return; }
            _updateBusy = true;
            var u = _update;
            SetText("UpdateText", "Downloading " + u.Version + "…");
            Get<Button>("UpdateBtn").IsEnabled = false;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string err;
                try { err = Updater.Install(u, Data); } catch (Exception ex) { err = ex.Message; }
                Window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _updateBusy = false;
                    if (err != null) { Get<Button>("UpdateBtn").IsEnabled = true; SetText("UpdateText", err); Tick(err); return; }
                    _update = null; _updateInstalled = true;   // the new exe is in place: the button stays off until the next start
                    if (_on || IsRunning(Monitor.SimName))
                    {
                        // Turned on (or iRacing started) during the download: the new version waits for the next start.
                        SetText("UpdateText", "Version " + u.Version + " is installed and starts next time you open Race Mode");
                        Tick("Race Mode " + u.Version + " is ready. It starts next time you open Race Mode.");
                        return;
                    }
                    Tick("Installed " + u.Version + ", restarting Race Mode");
                    try { Updater.StartNew(); }
                    catch (Exception ex) { SetText("UpdateText", "Installed, but couldn't restart: " + ex.Message + ". Start Race Mode again to use it."); return; }
                    Quit();
                }));
            });
        }
        // Puts iRacing's monitor, resolution and FPS cap back if they drifted (see DisplayGuard.cs).
        void CheckDisplay(string when, bool force)
        {
            if (_guardBusy || _cfg.DisplayGuard == null || !_cfg.DisplayGuard.Enabled) return;
            _guardBusy = true;
            try
            {
                var r = DisplayGuard.Run(_cfg.DisplayGuard, true);
                // Remember what we last saw (after our own write) so the timer only reacts to new changes.
                _guardSig = DisplayGuard.Signature(_cfg.DisplayGuard);
                _displayLine = r.Skipped ? "screen not checked" : r.Warning != null ? "screen needs a look" : "screen OK";
                bool newWarning = r.Warning != null && r.Warning != _lastWarning;
                bool newSkip = r.Skipped && r.Message != _lastSkip;
                _lastWarning = r.Warning; _lastSkip = r.Skipped ? r.Message : null;
                if (force || r.Changed || newWarning || newSkip)
                    _mon.Event("Display check (" + when + "): " + r.Message + (r.Backup != null ? " · backup " + System.IO.Path.GetFileName(r.Backup) : "") + (r.Warning != null ? " · WARNING " + r.Warning : ""));
                if (newWarning)
                {
                    Tick(r.Warning);
                    // Once per new problem: iRacing can't fix a monitor Windows is running at the wrong mode.
                    MessageBox.Show(Window, r.Warning + ".\n\niRacing runs in a window that fills the screen, so it uses whatever size and refresh rate Windows has set for that monitor. Change it in Windows display settings.", "Race Mode", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else if (r.Changed) Tick("Put iRacing back " + (_cfg.DisplayGuard.IsTriple ? "across your three screens" : "on the right screen") + " (" + r.Changes.Count + " setting" + (r.Changes.Count == 1 ? "" : "s") + " fixed)");
                else if (newSkip || (force && r.Skipped)) Tick(r.Message);
            }
            finally { _guardBusy = false; }
        }

        // ---------------- helpers ----------------
        string IniPath() { return _cfg.DisplayGuard == null || string.IsNullOrWhiteSpace(_cfg.DisplayGuard.IniPath) ? DisplayGuard.DefaultIniPath : _cfg.DisplayGuard.IniPath; }
        // Processes with this name in the user's own Windows session. Another signed-in session (a second account,
        // or a disconnected one) can't be closed, lowered or boosted from here, and its apps don't compete with this
        // desktop's windows, so they're neither touched nor reported as "still running". SessionId comes from the
        // same process snapshot as the name: no process handle is opened.
        static readonly int MySession = CurrentSession();
        static int CurrentSession() { using (var me = Process.GetCurrentProcess()) return me.SessionId; }
        static Process[] Mine(string name)
        {
            var mine = new List<Process>();
            foreach (var p in Process.GetProcessesByName(name))
            {
                bool keep = false;
                try { keep = p.SessionId == MySession; } catch { }
                if (keep) mine.Add(p); else p.Dispose();
            }
            return mine.ToArray();
        }

        // Names of the processes in this session, from one snapshot that's reused for up to a second.
        // Every timer asks about several apps; one snapshot per name used to cost 10-30 snapshots every 3 s.
        static HashSet<string> _runSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static DateTime _runAt = DateTime.MinValue;
        static bool IsRunning(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if ((DateTime.UtcNow - _runAt).TotalMilliseconds > 900)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    try { if (p.SessionId == MySession) set.Add(p.ProcessName); } catch { }
                    p.Dispose();
                }
                _runSet = set; _runAt = DateTime.UtcNow;
            }
            return _runSet.Contains(name);
        }
        static readonly int MyPid = CurrentPid();
        static int CurrentPid() { using (var me = Process.GetCurrentProcess()) return me.Id; }
        static string FileStamp(string path)
        {
            try { var fi = new FileInfo(path); return fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : "missing"; } catch { return "error"; }
        }

        // ---------------- graphics presets (Photo mode) ----------------
        void SetGraphicsMode(string mode)
        {
            if (_bindingGraphics) return;
            _cfg.GraphicsMode = GraphicsPresets.Normalize(mode);
            if (_cfg.GraphicsMode == "Photo" && GraphicsPresets.EnsurePhotoSeeded(IniPath(), _cfg.GraphicsPhoto != null ? _cfg.GraphicsPhoto.Level : 2)) { DerivePhotoLevel(); ShowGraphicsLevels(); }
            SaveConfig();
            ApplyGraphics("Photo mode " + (_cfg.GraphicsMode == "Photo" ? "on" : "off"));
        }

        void SaveCurrentAs(string mode)
        {
            if (IsRunning(Monitor.SimName)) { Tick("Close iRacing first: it rewrites its settings when it closes"); return; }
            try
            {
                int n = GraphicsPresets.SaveFromIni(mode, IniPath());
                _mon.Event("Saved iRacing's current graphics as the " + mode + " preset (" + n + " settings)");
                Tick("Saved iRacing's current graphics as your " + mode + " settings");
                // The slider and pins follow the new preset (settings off any level become Custom).
                if (GraphicsPresets.Normalize(mode) == "Race") { DeriveRaceLevel(); _committedRace = _cfg.GraphicsRace.Level; } else DerivePhotoLevel();
                SaveConfig(); ShowGraphicsLevels(); BuildAdvanced();
            }
            catch (Exception ex) { LogCrash(ex); Tick("Couldn't save your " + mode + " settings: " + ex.Message); }
            RefreshGraphicsStatus();
        }

        // Writes the selected preset now, or marks it pending until the sim closes (never touches a running sim).
        void ApplyGraphics(string why)
        {
            var r = GraphicsPresets.Apply(_cfg.GraphicsMode, IniPath());
            _graphicsPending = r.Pending;
            _graphicsOk = r.Ok;
            _graphicsLine = r.Message;
            _gfxStamp = FileStamp(IniPath()); // our own write isn't "a change made in iRacing"
            if (!r.Pending)
                _mon.Event("Graphics (" + why + "): " + r.Message + (r.Backup != null ? " · backup " + System.IO.Path.GetFileName(r.Backup) : "") +
                           (r.Changes.Count > 0 ? " · " + string.Join(", ", r.Changes.Take(12).ToArray()) + (r.Changes.Count > 12 ? " …" : "") : ""));
            ShowGraphicsStatus();
            if (r.Ok && r.Changed > 0) Tick(r.Mode + " graphics set (" + r.Changed + " setting" + (r.Changed == 1 ? "" : "s") + " changed)");
            else if (!r.Ok) Tick(r.Message);
            if (_on) UpdateStatus();
        }

        // Re-reads iRacing's file and says whether it still matches the selected preset (no changes are made).
        void RefreshGraphicsStatus()
        {
            if (_graphicsPending) { ShowGraphicsStatus(); return; }
            try
            {
                var mode = _cfg.GraphicsMode;
                if (!GraphicsPresets.Exists(mode)) { _graphicsOk = false; _graphicsLine = "No " + mode + " settings saved yet. Set the graphics in iRacing, close it, then press Save Current as " + mode + "."; }
                else if (!File.Exists(IniPath())) { _graphicsOk = false; _graphicsLine = "iRacing's graphics file wasn't found."; }
                else
                {
                    var diff = GraphicsPresets.Differences(mode, IniPath());
                    _graphicsOk = diff.Count == 0;
                    _graphicsLine = diff.Count == 0
                        ? mode + " graphics are set, and iRacing matches your settings"
                        : "Graphics were changed in iRacing (" + diff.Count + " setting" + (diff.Count == 1 ? "" : "s") + ", e.g. " + string.Join(", ", diff.Take(3).Select(FriendlyDiff).ToArray()) + "). Press Save Current as " + mode + " to keep them, or Put My Settings Back.";
                }
            }
            catch (Exception ex) { _graphicsOk = false; _graphicsLine = "Couldn't read iRacing's graphics: " + ex.Message; }
            _gfxStamp = FileStamp(IniPath());
            ShowGraphicsStatus();
        }

        // "LODMinFPSTarget 150 -> 123 (replay)" -> "Level of detail target 150 → 123 (replay)", using the catalog's plain label.
        static string FriendlyDiff(string d)
        {
            int sp = d.IndexOf(' '); if (sp <= 0) return d;
            var k = GraphicsCatalog.Find(d.Substring(0, sp));
            return (k != null && !string.IsNullOrEmpty(k.label) ? k.label : d.Substring(0, sp)) + d.Substring(sp).Replace(" -> ", " → ");
        }

        void ShowGraphicsStatus()
        {
            var t = Get<TextBlock>("GraphicsStatus");
            if (t == null) return;
            t.Text = _graphicsLine;
            t.Foreground = _graphicsOk || _graphicsPending ? (Brush)Window.Resources["Dim"] : (Brush)Window.Resources["Warn"];
        }

        // ---------------- window: maximise, compact/wide layout, remembered size ----------------
        const double WideAt = 1100;   // at this width (e.g. maximised) the dashboard appears beside the app
        bool _wide;

        void ToggleMaximize() { Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }

        void OnWindowState()
        {
            bool max = Window.WindowState == WindowState.Maximized;
            // Maximised, the window is exactly the monitor's free area (WindowFit.cs), so nothing hangs over the edges.
            var root = Get<Grid>("Root");
            if (root != null) root.Margin = new Thickness(0);
            var icon = Get<System.Windows.Shapes.Path>("MaxIcon");
            if (icon != null) icon.Data = Geometry.Parse(max ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z" : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
            var btn = Get<Button>("MaxBtn");
            if (btn != null) { btn.ToolTip = max ? "Restore" : "Maximize: shows the full dashboard"; AutomationProperties.SetName(btn, max ? "Restore" : "Maximize"); }
            if (Window.WindowState != WindowState.Minimized) RenderDashboard();
        }

        // Short windows (a 1080p laptop at 150% scaling has about 690 px): a tighter hero and stats strip, so the
        // tabs keep room for their content. Nothing is hidden that carries meaning; only padding and the meters go.
        bool _compact;
        void ApplyCompact(bool compact)
        {
            if (compact == _compact) return;
            _compact = compact;
            var hero = Get<Border>("Hero");
            if (hero != null) hero.Padding = compact ? new Thickness(16, 12, 18, 12) : new Thickness(16, 22, 22, 22);
            var head = Get<TextBlock>("Headline"); if (head != null) head.FontSize = compact ? 22 : 28;
            var tel = Get<UniformGrid>("Telemetry");
            if (tel != null)
                foreach (var cell in tel.Children.OfType<Border>()) cell.Padding = compact ? new Thickness(cell.Padding.Left, 8, cell.Padding.Right, 8) : new Thickness(cell.Padding.Left, 14, cell.Padding.Right, 14);
            foreach (var n in new[] { "CpuBar", "CpuTempBar", "GpuBar", "GpuTempBar", "RamBar" })
            { var b = Get<ProgressBar>(n); if (b != null && b.Visibility != Visibility.Hidden) b.Visibility = compact ? Visibility.Collapsed : Visibility.Visible; }
        }

        void ApplyLayout()
        {
            bool wide = Window.ActualWidth >= WideAt;
            ApplyCompact(!wide && Window.ActualHeight < 720);
            if (wide)
            {
                // the dashboard is the window minus the 580 px app column and margins
                double dash = Window.ActualWidth - 620;
                var cg = Get<UniformGrid>("ChartGrid"); if (cg != null) cg.Columns = dash >= 900 ? 3 : 2;
                var bg = Get<UniformGrid>("DashBottom"); if (bg != null) bg.Columns = dash >= 900 ? 2 : 1;
            }
            if (wide == _wide && _chartsBuilt == wide) return;
            _wide = wide;
            Get<ColumnDefinition>("MainCol").Width = wide ? new GridLength(580) : new GridLength(1, GridUnitType.Star);
            Get<ColumnDefinition>("DashCol").Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Get<ScrollViewer>("Dash").Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            if (wide) { BuildDashboard(); UpdateChecklist(); ShowLastSession(); RenderDashboard(); }
        }

        void RestoreWindowSize()
        {
            var work = SystemParameters.WorkArea;
            if (_cfg.WindowWidth >= Window.MinWidth) Window.Width = Math.Min(_cfg.WindowWidth, work.Width);
            if (_cfg.WindowHeight >= Window.MinHeight) Window.Height = Math.Min(_cfg.WindowHeight, work.Height - 16);
            if (_cfg.WindowMaximized) Window.Loaded += (s, e) => Window.WindowState = WindowState.Maximized;
        }

        void RememberWindowSize()
        {
            var b = Window.WindowState == WindowState.Normal ? new Rect(0, 0, Window.ActualWidth, Window.ActualHeight) : Window.RestoreBounds;
            if (b.Width > 0 && b.Height > 0) { _cfg.WindowWidth = Math.Round(b.Width); _cfg.WindowHeight = Math.Round(b.Height); }
            _cfg.WindowMaximized = Window.WindowState == WindowState.Maximized;
        }

        // ---------------- dashboard: 60 s charts, checklist, last session ----------------
        List<ChartTile> _charts;
        ChartTile _fpsChart, _coreChart, _cpuChart, _gpuChart, _tempChart, _vramChart;
        int _dashTick;

        // The cap iRacing will run at: Race Mode's kept cap, else what iRacing's own file says (re-read only when it changes).
        int FpsCap()
        {
            var g = _cfg.DisplayGuard;
            if (g != null && g.Enabled && g.EnforcesFpsCap) return g.FpsLimit;
            try
            {
                var fi = new FileInfo(IniPath());
                if (!fi.Exists) return 0;
                var stamp = fi.LastWriteTimeUtc.Ticks;
                if (stamp != _iniCapStamp) { _iniCapStamp = stamp; _iniCap = ReadIniFpsCap(); }
                return _iniCap;
            }
            catch { return 0; }
        }
        long _iniCapStamp; int _iniCap;

        bool _chartsBuilt;

        // The tiles' visuals are built the first time the window goes wide; their data is recorded from launch.
        void BuildDashboard()
        {
            if (_chartsBuilt) return;
            if (_charts == null) CreateCharts();
            var grid = Get<UniformGrid>("ChartGrid");
            grid.Margin = new Thickness(-7, -7, -7, -7);
            foreach (var c in _charts) grid.Children.Add(c.Build(Window.Resources));
            _chartsBuilt = true;
        }

        void CreateCharts()
        {
            int cap = FpsCap();
            _fpsChart = new ChartTile { Label = "FPS (iRacing)", Unit = "fps", Min = cap > 0 ? Math.Max(0, cap - 85) : 0, Max = cap > 0 ? cap + 10 : 160,
                                        Ref = cap > 0 ? cap : double.NaN, RefLabel = cap > 0 ? "cap " + cap : "",
                                        IsHot = v => cap > 0 && v < cap - 5, HotWord = v => "below " + (cap - 5),
                                        Idle = () => "Shows up once you're in iRacing", Note = () => _mon.Live.SimContext == "replay" ? "replay" : "" };
            _coreChart = new ChartTile { Label = "Busiest CPU core", Unit = "%", Ref = 95, RefLabel = "95%", IsHot = v => v >= 95, HotWord = v => "maxed",
                                         Note = () => "iRacing usually maxes out this core first" };
            _cpuChart = new ChartTile { Label = "CPU, all cores", Unit = "%", Note = () => { var m = _mon.Live.CpuMHz; return m > 0 ? (m / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " GHz" : ""; } };
            _gpuChart = new ChartTile { Label = "GPU", Unit = "%", Note = () => double.IsNaN(_mon.Live.GpuPower) ? "" : N0(_mon.Live.GpuPower) + " W" };
            _tempChart = new ChartTile { Label = "CPU temperature", Unit = Monitor.HwinfoFahrenheit ? "°F" : "°C", Min = 30, Max = 105, Ref = 85,
                                         RefLabel = Monitor.HwinfoFahrenheit ? "185 °F" : "85 °C", IsHot = v => v >= 85, HotWord = v => "hot",
                                         Show = v => Monitor.HwinfoFahrenheit ? v * 9 / 5 + 32 : v,
                                         Idle = () => Get<TextBlock>("CpuTempSub") != null ? "No reading · " + Get<TextBlock>("CpuTempSub").Text : "No reading" };
            _vramChart = new ChartTile { Label = "Video memory", Unit = "GB", Min = 0, Max = 8, Ref = 7, RefLabel = "",
                                         IsHot = v => false, HotWord = v => "nearly full", Format = v => v.ToString("0.0", CultureInfo.InvariantCulture),
                                         Note = () => double.IsNaN(_mon.Live.VramTotalMB) ? "" : "of " + (_mon.Live.VramTotalMB / 1024).ToString("0", CultureInfo.InvariantCulture) + " GB" };
            _charts = new List<ChartTile> { _fpsChart, _coreChart, _cpuChart, _gpuChart, _tempChart, _vramChart };
        }

        // Called every second from RefreshStats: always records, only draws while the dashboard is on screen.
        int _chartCap = -1;
        void FeedDashboard(LiveStats s)
        {
            if (_charts == null) CreateCharts();
            if (!double.IsNaN(s.VramTotalMB) && s.VramTotalMB > 0)
            {
                _vramChart.Max = Math.Round(s.VramTotalMB / 1024);
                _vramChart.Ref = Math.Min(_vramChart.Max * 0.875, _vramChart.Max);
                _vramChart.RefLabel = _vramChart.Ref.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
                var lim = _vramChart.Ref; _vramChart.IsHot = v => v >= lim;
            }
            // The cap can change (monitor picked, cap changed in iRacing): keep the FPS chart's scale and line on it.
            int cap = FpsCap();
            if (cap != _chartCap)
            {
                _chartCap = cap;
                _fpsChart.Min = cap > 0 ? Math.Max(0, cap - 85) : 0; _fpsChart.Max = cap > 0 ? cap + 10 : 160;
                _fpsChart.Ref = cap > 0 ? cap : double.NaN; _fpsChart.RefLabel = cap > 0 ? "cap " + cap : "";
                _fpsChart.IsHot = v => cap > 0 && v < cap - 5; _fpsChart.HotWord = v => "below " + (cap - 5);
            }
            _fpsChart.Push(s.Fps);
            _coreChart.Push(s.CpuMaxCore);
            _cpuChart.Push(s.Cpu);
            _gpuChart.Push(s.GpuUtil);
            _tempChart.Push(s.CpuTemp);
            _vramChart.Push(double.IsNaN(s.VramUsedMB) ? double.NaN : s.VramUsedMB / 1024.0);
            if (++_dashTick % 3 == 0) UpdateChecklist();
            RenderDashboard();
        }

        void RenderDashboard()
        {
            if (!_chartsBuilt || !_wide || Window.WindowState == WindowState.Minimized || !Window.IsVisible) return;
            foreach (var c in _charts) c.Render();
        }

        // One checklist row: a dot plus words, so the state never depends on colour alone.
        FrameworkElement CheckRow(string name, string state, string brushKey)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 10, 0),
                                    Fill = brushKey == "Off" ? new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x60)) : (Brush)Window.Resources[brushKey] };
            var n = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var st = new TextBlock { Text = state, Style = (Style)Window.Resources["Sub"], TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                                     Foreground = brushKey == "Warn" ? (Brush)Window.Resources["Warn"] : (Brush)Window.Resources["Dim"] };
            Grid.SetColumn(n, 1); Grid.SetColumn(st, 2);
            g.Children.Add(dot); g.Children.Add(n); g.Children.Add(st);
            return g;
        }

        void UpdateChecklist()
        {
            var list = Get<StackPanel>("Checklist");
            if (list == null || !_wide) return;
            list.Children.Clear();
            bool on = _on;
            list.Children.Add(CheckRow("Race Mode", _phase == "off" ? "off" : _phase == "turning" ? "turning on" : _phase == "issues" ? "on, needs a look" : "on, all checked",
                                       _phase == "off" ? "Off" : _phase == "issues" ? "Warn" : _phase == "turning" ? "Dim" : "Live"));
            foreach (var item in _launch.Where(i => i.Enabled && !string.IsNullOrEmpty(i.ProcessName)))
            {
                bool run = IsRunning(item.ProcessName);
                list.Children.Add(CheckRow(item.Name, !item.Installed ? "not installed" : run ? "running" : "not running", run ? "Live" : on ? "Warn" : "Off"));
            }
            if (_cfg.Streaming)
                foreach (var item in _stream.Where(i => i.Enabled && !string.IsNullOrEmpty(i.ProcessName)))
                {
                    bool run = IsRunning(item.ProcessName);
                    list.Children.Add(CheckRow(item.Name, !item.Installed ? "not installed" : run ? "running" : "not running", run ? "Live" : on && item.Installed ? "Warn" : "Off"));
                }
            bool sim = IsRunning(Monitor.SimName), ui = IsRunning("iRacingUI");
            list.Children.Add(CheckRow("iRacing", sim ? "racing" + (_simBoosted ? ", put first" : "") : ui ? "app open" : "closed", sim || ui ? "Live" : "Off"));
            if (on)
            {
                int open = _close.Count(n => !ProtectedFromClose(n) && IsRunning(n));
                list.Children.Add(CheckRow("Close list", open == 0 ? "all closed" : open + " still running", open == 0 ? "Live" : "Warn"));
            }
            list.Children.Add(CheckRow("Screen", string.IsNullOrEmpty(_displayLine) ? (_cfg.DisplayGuard != null && _cfg.DisplayGuard.Enabled ? "checked when Race Mode turns on" : "check is off") : _displayLine.Replace("screen ", ""),
                                       _displayLine == "screen OK" ? "Live" : _displayLine == "screen needs a look" ? "Warn" : "Off"));
            list.Children.Add(CheckRow(_cfg.GraphicsMode + " graphics", _graphicsPending ? "waiting for iRacing to close" : _graphicsOk ? "set" : "see the Graphics tab",
                                       _graphicsPending ? "Dim" : _graphicsOk ? "Live" : "Warn"));
            if (_cfg.Streaming)
            {
                var o = Obs.Latest;
                list.Children.Add(CheckRow("OBS", !o.Connected ? (o.Status.StartsWith("OBS isn't open") ? "not open" : o.Status.Contains("remote control is off") ? "remote control is off" : "not connected") :!o.Streaming ? "connected" : (double.IsNaN(o.Kbps) ? "streaming" : "streaming " + N0(o.Kbps) + " kbps") + (o.Reconnecting ? ", reconnecting" : ""),
                                           !o.Connected ? "Off" : o.Reconnecting ? "Warn" : "Live"));
            }
        }

        // The last session's key lines from summary.txt, as label / value rows.
        void ShowLastSession()
        {
            var box = Get<StackPanel>("LastSession");
            if (box == null || !_wide) return;
            box.Children.Clear();
            if (string.IsNullOrEmpty(_lastSummary))
            {
                box.Children.Add(new TextBlock { Text = "No session logged yet. Turn Race Mode on before you race.", Style = (Style)Window.Resources["Sub"], Margin = new Thickness(0, 5, 0, 5) });
                return;
            }
            var wanted = new HashSet<string> { "Started", "Duration", "FPS drive", "FPS replay", "Sim load", "CPU", "CPU temp", "GPU", "RAM", "Total use", "Commit", "iRacing" };
            foreach (var raw in _lastSummary.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = raw.TrimEnd();
                int c = line.IndexOf(':'); if (c <= 0 || line.StartsWith(" ")) continue;
                var label = line.Substring(0, c).Trim(); var value = line.Substring(c + 1).Trim();
                if (!wanted.Contains(label)) continue;
                if (label == "Started" || label == "Duration") value = value.Replace("  ", " ");
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new TextBlock { Text = label, Style = (Style)Window.Resources["Sub"] });
                var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 17,
                                        Foreground = value.Contains("<<") ? (Brush)Window.Resources["Warn"] : (Brush)Window.Resources["Ink"] };
                Grid.SetColumn(v, 1); g.Children.Add(v);
                box.Children.Add(g);
            }
        }

        // ---------------- graphics quality: Race slider, Photo replay quality, Advanced editor ----------------
        // Moving the slider only updates the readout; the preset is composed and saved 0.7 s after the last change
        // (so dragging across five levels doesn't write iRacing's file five times), then applied through the
        // usual path: now if iRacing is closed, otherwise pending until it closes.
        readonly DispatcherTimer _gfxCommit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        bool _bindingLevels, _raceDirty, _photoDirty, _advPhoto;
        int _committedRace = 6;

        static Dictionary<string, string> Section(Dictionary<string, Dictionary<string, string>> m, string s)
        {
            Dictionary<string, string> d;
            return m != null && m.TryGetValue(s, out d) ? d : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        void InitGraphicsLevels()
        {
            var keys = GraphicsCatalog.Keys;   // loads the catalog
            if (GraphicsCatalog.LoadError != null || keys.Count == 0)
            {
                Get<Slider>("RaceSlider").IsEnabled = false;
                SetText("RaceLevelSub", GraphicsCatalog.LoadError ?? "The graphics catalog is empty");
                return;
            }
            if (_cfg.GraphicsRace == null || _cfg.GraphicsRace.Level == 0) DeriveRaceLevel();
            if (_cfg.GraphicsPhoto == null || _cfg.GraphicsPhoto.Level == 0) DerivePhotoLevel();
            _committedRace = _cfg.GraphicsRace.Level;
            ShowGraphicsLevels();
        }

        // Works out the slider level from the saved Race preset. Settings that differ from that level become
        // pinned ("Custom"), so composing level + pins rebuilds the preset exactly.
        void DeriveRaceLevel()
        {
            var g = new GraphicsLevels { Level = 6 };
            try
            {
                if (GraphicsPresets.Exists("Race"))
                {
                    var race = Section(GraphicsPresets.Load("Race"), GraphicsCatalog.Race);
                    bool exact; int differing;
                    g.Level = GraphicsCatalog.NearestLevel(race, out exact, out differing);
                    foreach (var k in GraphicsCatalog.Keys.Where(x => x.IsSlider))
                    {
                        string v;
                        if (race.TryGetValue(k.key, out v) && !IniFile.SameValue(v, k.levels[g.Level - 1]) && k.IsValid(v)) g.Overrides[k.key] = v;
                    }
                }
            }
            catch (Exception ex) { LogCrash(ex); }
            _cfg.GraphicsRace = g;
        }

        void DerivePhotoLevel()
        {
            var g = new GraphicsLevels { Level = 3 };
            try
            {
                if (GraphicsPresets.Exists("Photo"))
                {
                    var replay = Section(GraphicsPresets.Load("Photo"), GraphicsCatalog.Replay);
                    bool exact;
                    g.Level = GraphicsCatalog.NearestPhotoLevel(replay, out exact);
                    foreach (var k in GraphicsCatalog.Keys.Where(x => x.photoLevels != null))
                    {
                        string v;
                        if (replay.TryGetValue(k.key, out v) && !IniFile.SameValue(v, k.photoLevels[g.Level - 1]) && k.IsValid(v)) g.Overrides[k.key] = v;
                    }
                }
            }
            catch (Exception ex) { LogCrash(ex); }
            _cfg.GraphicsPhoto = g;
        }

        void ShowGraphicsLevels()
        {
            if (_cfg.GraphicsRace == null || _cfg.GraphicsPhoto == null) return;
            _bindingLevels = true;
            try
            {
                Get<Slider>("RaceSlider").Value = _cfg.GraphicsRace.Level;
                var pr = Get<RadioButton>("PhotoLvl" + _cfg.GraphicsPhoto.Level); if (pr != null) pr.IsChecked = true;
            }
            finally { _bindingLevels = false; }
            ShowRaceReadout(_cfg.GraphicsRace.Level);
            int pins = _cfg.GraphicsPhoto.Overrides.Count;
            SetText("PhotoLevelSub", "Used while Photo mode is on" + (pins > 0 ? " · " + pins + " setting" + (pins == 1 ? "" : "s") + " changed by hand" : "") + ".");
        }

        void ShowRaceReadout(int level)
        {
            SetText("RaceLevelNum", level.ToString(CultureInfo.InvariantCulture));
            SetText("RaceLevelName", GraphicsCatalog.LevelNames[level]);
            int pins = _cfg.GraphicsRace.Overrides.Count;
            SetText("RaceLevelSub", pins == 0 ? "Your Race settings. 1 favours FPS, 10 favours looks."
                                              : "Your Race settings, with " + pins + " changed by hand in Advanced.");
        }

        void OnRaceSlider(int level)
        {
            if (_bindingLevels || _cfg.GraphicsRace == null) return;
            level = Math.Max(1, Math.Min(10, level));
            if (level == _cfg.GraphicsRace.Level) return;
            _cfg.GraphicsRace.Level = level;
            ShowRaceReadout(level);
            var t = Get<TextBlock>("RaceChange");
            if (t != null)
            {
                t.Text = DescribeLevelChange(_committedRace, level);
                t.Visibility = string.IsNullOrEmpty(t.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
            _raceDirty = true;
            _gfxCommit.Stop(); _gfxCommit.Start();
        }

        // "Level 7 vs 6 changes 4 settings: Static shadow resolution, …" (settings pinned by hand don't move).
        string DescribeLevelChange(int from, int to)
        {
            if (from == to) return "Back to your saved level.";
            var changed = GraphicsCatalog.Keys
                .Where(k => k.IsSlider && !_cfg.GraphicsRace.Overrides.ContainsKey(k.key) && !IniFile.SameValue(k.levels[from - 1], k.levels[to - 1]))
                .Select(k => k.label).ToList();
            if (changed.Count == 0) return "Level " + to + " looks the same as " + from + " with your hand-set settings.";
            return "Level " + to + " vs " + from + " changes " + changed.Count + " setting" + (changed.Count == 1 ? "" : "s") + ": " +
                   string.Join(", ", changed.Take(4).ToArray()) + (changed.Count > 4 ? " and " + (changed.Count - 4) + " more" : "") + ".";
        }

        void OnPhotoLevel(int level)
        {
            if (_bindingLevels || _cfg.GraphicsPhoto == null || level == _cfg.GraphicsPhoto.Level) return;
            _cfg.GraphicsPhoto.Level = level;
            _photoDirty = true;
            if (_advPhoto) BuildAdvanced();
            _gfxCommit.Stop(); _gfxCommit.Start();
        }

        // Composes the changed preset(s), saves them, and applies the active one (pending while the sim runs).
        void CommitGraphicsLevels()
        {
            bool apply = false; string why = null;
            try
            {
                if (_raceDirty && GraphicsPresets.Exists("Race"))
                {
                    var g = _cfg.GraphicsRace;
                    GraphicsPresets.Write("Race", GraphicsCatalog.ComposeRace(g.Level, g.Overrides, GraphicsPresets.Load("Race")), "Race quality " + g.Level);
                    _mon.Event("Race quality set to " + g.Level + " (" + GraphicsCatalog.LevelNames[g.Level] + ")" + (g.Overrides.Count > 0 ? " with " + g.Overrides.Count + " settings set by hand" : ""));
                    _committedRace = g.Level;
                    // Personal (non-slider) settings are now part of the preset itself, so they aren't kept as pins.
                    foreach (var key in g.Overrides.Keys.ToList()) { var ck = GraphicsCatalog.Find(key); if (ck == null || !ck.IsSlider) g.Overrides.Remove(key); }
                    if (_cfg.GraphicsMode == "Race") { apply = true; why = "Race quality " + g.Level; }
                    else Tick("Race quality " + g.Level + " saved. Used when Photo mode is off.");
                }
                if (_photoDirty && (GraphicsPresets.Exists("Photo") || GraphicsPresets.EnsurePhotoSeeded(IniPath(), _cfg.GraphicsPhoto.Level)))
                {
                    var g = _cfg.GraphicsPhoto;
                    GraphicsPresets.Write("Photo", GraphicsCatalog.ComposePhoto(g.Level, g.Overrides, GraphicsPresets.Load("Photo")), "Replay quality " + GraphicsCatalog.PhotoNames[g.Level]);
                    _mon.Event("Replay quality set to " + GraphicsCatalog.PhotoNames[g.Level] + (g.Overrides.Count > 0 ? " with " + g.Overrides.Count + " settings set by hand" : ""));
                    if (_cfg.GraphicsMode == "Photo") { apply = true; why = "Replay quality " + GraphicsCatalog.PhotoNames[g.Level]; }
                    else Tick("Replay quality " + GraphicsCatalog.PhotoNames[g.Level] + " saved. Used when Photo mode is on.");
                }
            }
            catch (Exception ex) { LogCrash(ex); Tick("Couldn't save your graphics settings: " + ex.Message); }
            _raceDirty = _photoDirty = false;
            SaveConfig();
            ShowGraphicsLevels();
            UpdateAdvSummary();
            if (apply) ApplyGraphics(why);
        }

        void ShowAdvanced(bool open)
        {
            Get<StackPanel>("AdvPanel").Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            Get<StackPanel>("AdvList").Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open) { BuildAdvanced(); return; }
            // The 80 editor rows cost ~10 MB; drop them and collect once when the editor closes, so the memory
            // only exists while someone is actually editing (a rare, user-initiated moment, never a loop).
            Get<StackPanel>("AdvList").Children.Clear();
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }), DispatcherPriority.ContextIdle);
        }

        // The Advanced editor: one grouped list per catalog group. Built only while open.
        void BuildAdvanced()
        {
            var list = Get<StackPanel>("AdvList");
            if (list == null || list.Visibility != Visibility.Visible || _cfg.GraphicsRace == null) return;
            list.Children.Clear();
            var g = _advPhoto ? _cfg.GraphicsPhoto : _cfg.GraphicsRace;
            var raceBase = _advPhoto ? null : Section(GraphicsPresets.Load("Race"), GraphicsCatalog.Race);
            var keys = GraphicsCatalog.Keys.Where(k => k.scope != "machine" && k.scope != "guard" &&
                       (_advPhoto ? k.photoLevels != null : (k.IsSlider || k.scope == "user" && raceBase.ContainsKey(k.key)))).ToList();
            _advCount = keys.Count; _advSlider = keys.Count(k => !_advPhoto && k.IsSlider);
            UpdateAdvSummary();
            if (_advFilter.Length > 0)
            {
                keys = keys.Where(k => (k.label ?? "").IndexOf(_advFilter, StringComparison.OrdinalIgnoreCase) >= 0 || k.key.IndexOf(_advFilter, StringComparison.OrdinalIgnoreCase) >= 0
                                       || (k.desc ?? "").IndexOf(_advFilter, StringComparison.OrdinalIgnoreCase) >= 0 || (k.group ?? "").IndexOf(_advFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (keys.Count == 0) { list.Children.Add(new TextBlock { Text = "No setting matches \"" + _advFilter + "\".", Style = (Style)Window.Resources["Sub"], Margin = new Thickness(16, 18, 0, 0) }); return; }
            }

            foreach (var grp in keys.GroupBy(k => k.group ?? "Other"))
            {
                list.Children.Add(new TextBlock { Text = grp.Key, Style = (Style)Window.Resources["SectionHeader"], Margin = new Thickness(16, 22, 0, 7) });
                var rows = new StackPanel();
                var items = grp.ToList();
                for (int i = 0; i < items.Count; i++) rows.Children.Add(AdvRow(items[i], g, raceBase, i == items.Count - 1));
                list.Children.Add(new Border { Style = (Style)Window.Resources["Group"], Child = rows });
            }
        }

        int _advCount, _advSlider;
        string _advFilter = "";
        void UpdateAdvSummary()
        {
            if (_cfg.GraphicsRace == null || _cfg.GraphicsPhoto == null) return;
            int pins = (_advPhoto ? _cfg.GraphicsPhoto : _cfg.GraphicsRace).Overrides.Keys.Count(key => { var k = GraphicsCatalog.Find(key); return k != null && (_advPhoto || k.IsSlider); });
            SetText("AdvSummary", _advPhoto
                ? _advCount + " replay settings · " + pins + " changed by hand"
                : _advSlider + " follow the slider · " + (_advCount - _advSlider) + " personal · " + pins + " changed by hand");
        }

        string AdvDefault(CatalogKey k, GraphicsLevels g, Dictionary<string, string> raceBase)
        {
            if (_advPhoto) return k.photoLevels[g.Level - 1];
            if (k.IsSlider) return k.levels[g.Level - 1];
            string v; return raceBase != null && raceBase.TryGetValue(k.key, out v) ? v : null;
        }

        FrameworkElement AdvRow(CatalogKey k, GraphicsLevels g, Dictionary<string, string> raceBase, bool last)
        {
            string pinned; g.Overrides.TryGetValue(k.key, out pinned);
            var def = AdvDefault(k, g, raceBase);
            var current = pinned ?? def;

            var tag = new Border { BorderBrush = (Brush)Window.Resources["Line"], BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                                   Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                   Child = new TextBlock { Text = "Custom", Style = (Style)Window.Resources["Caption"], Foreground = (Brush)Window.Resources["Dim"] },
                                   Visibility = pinned != null && (k.IsSlider || _advPhoto) ? Visibility.Visible : Visibility.Collapsed };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock { Text = k.label ?? k.key });
            title.Children.Add(tag);

            var rec = k.rec == null ? null : (_advPhoto ? k.rec.photo : k.rec.race);
            var meta = (rec != null ? "Recommended: " + k.LabelFor(rec) + " · " : "") + GraphicsCatalog.CostText(k) + (_advPhoto || k.IsSlider ? "" : " · not on the slider");
            var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(title);
            if (!string.IsNullOrEmpty(k.desc)) left.Children.Add(new TextBlock { Text = k.desc, Style = (Style)Window.Resources["Sub"] });
            left.Children.Add(new TextBlock { Text = meta, Style = (Style)Window.Resources["Caption"], Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });

            var combo = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(combo, k.label ?? k.key);
            var options = new List<CatalogValue>();
            if (k.values != null && k.values.Count > 0) options.AddRange(k.values);
            else if (k.range != null && k.range.steps != null) options.AddRange(k.range.steps.Select(d => new CatalogValue { v = d.ToString(CultureInfo.InvariantCulture), label = d.ToString(CultureInfo.InvariantCulture) }));
            if (current != null && !options.Any(o => IniFile.SameValue(o.v, current))) options.Add(new CatalogValue { v = current, label = k.LabelFor(current) });
            foreach (var o in options)
            {
                var item = new ComboBoxItem { Content = o.label ?? o.v, Tag = o.v };
                combo.Items.Add(item);
                if (current != null && IniFile.SameValue(o.v, current)) combo.SelectedItem = item;
            }
            var reset = new Button { Content = "Reset", Style = (Style)Window.Resources["Btn"], Margin = new Thickness(0, 0, 8, 0),
                                     Visibility = pinned != null && (k.IsSlider || _advPhoto) ? Visibility.Visible : Visibility.Collapsed,
                                     ToolTip = _advPhoto ? "Back to the replay quality's value" : k.IsSlider ? "Back to the slider's value" : "Back to your Race settings' value" };
            AutomationProperties.SetName(reset, "Reset " + (k.label ?? k.key));
            bool quiet = false;
            combo.SelectionChanged += (s, e) =>
            {
                if (quiet) return;
                var item = combo.SelectedItem as ComboBoxItem; if (item == null) return;
                var v = (string)item.Tag;
                var d = AdvDefault(k, g, raceBase);
                if (d != null && IniFile.SameValue(v, d) && (k.IsSlider || _advPhoto)) g.Overrides.Remove(k.key); else g.Overrides[k.key] = v;
                bool on = g.Overrides.ContainsKey(k.key) && (k.IsSlider || _advPhoto);
                tag.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                reset.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (_advPhoto) _photoDirty = true; else _raceDirty = true;
                _gfxCommit.Stop(); _gfxCommit.Start();
            };
            reset.Click += (s, e) =>
            {
                g.Overrides.Remove(k.key);
                var d = AdvDefault(k, g, raceBase);
                quiet = true;
                foreach (ComboBoxItem it in combo.Items) if (d != null && IniFile.SameValue((string)it.Tag, d)) combo.SelectedItem = it;
                quiet = false;
                tag.Visibility = Visibility.Collapsed; reset.Visibility = Visibility.Collapsed;
                if (_advPhoto) _photoDirty = true; else _raceDirty = true;
                _gfxCommit.Stop(); _gfxCommit.Start();
                combo.Focus();
            };

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(reset); right.Children.Add(combo);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);
            Grid.SetColumn(right, 1); grid.Children.Add(right);
            return new Border { Style = (Style)Window.Resources[last ? "LastRow" : "Row"], Child = grid };
        }

        // ---------------- activation: Off -> Turning on -> Activated ----------------
        void SetPhase(string phase)
        {
            _phase = phase;
            SetText("Headline", phase == "off" ? "Race Mode is off" : phase == "turning" ? "Turning on…" : "Race Mode is on");
            var dot = Get<Shape>("StatusDot");
            if (dot == null) return;
            if (phase == "off")
            {
                dot.Fill = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x60));
                var hc = Get<System.Windows.Controls.Primitives.UniformGrid>("HeroChecks");
                if (hc != null) { hc.Children.Clear(); hc.Visibility = Visibility.Collapsed; }
            }
            else if (phase == "turning") dot.Fill = (Brush)Window.Resources["Dim"];
            else dot.Fill = (Brush)Window.Resources[phase == "issues" ? "Warn" : "Live"];
        }

        // Apps that should be running once Race Mode is on (enabled, installed, with a process name to check).
        List<LaunchItem> ExpectedApps()
        {
            var list = new List<LaunchItem>();
            if (_cfg.LaunchApps) list.AddRange(_launch.Where(i => i.Enabled && i.Installed && !string.IsNullOrEmpty(i.ProcessName)));
            if (_cfg.Streaming) list.AddRange(_stream.Where(i => i.Enabled && i.Installed && !string.IsNullOrEmpty(i.ProcessName)));
            return list;
        }

        // Every second after switching on: wait for Launch apps to appear and Close apps to go (the forced close
        // runs at 5 s), then confirm. Gives up after 45 s and names whatever is missing.
        void ConfirmTick()
        {
            if (!_on) { _confirmTimer.Stop(); return; }
            double secs = (DateTime.Now - _onAt).TotalSeconds;
            var waiting = ExpectedApps().Where(i => !IsRunning(i.ProcessName)).Select(i => i.Name).ToList();
            if (_cfg.StartIRacing && File.Exists(_cfg.IRacingUI) && !IsRunning("iRacingUI") && !IsRunning(Monitor.SimName)) waiting.Add("iRacing");
            var stillOpen = new List<string>();
            if (_cfg.CloseApps && secs >= 6.5)
                stillOpen = _close.Where(n => !ProtectedFromClose(n) && !_leftOpen.ContainsKey(n) && !_reopened.Contains(n) && IsRunning(n)).ToList();

            if (secs >= 6.5 && waiting.Count == 0 && stillOpen.Count == 0) { FinishActivation(new List<string>()); return; }
            if (secs >= 45)
            {
                var problems = waiting.Select(n => n + " didn't start").Concat(stillOpen.Select(n => n + " is still running")).ToList();
                FinishActivation(problems);
                return;
            }
            var w = waiting.FirstOrDefault();
            _activation = w != null
                ? "Turning on… waiting for " + w + (w.IndexOf("SimPro", StringComparison.OrdinalIgnoreCase) >= 0 ? " (click Yes if Windows asks)" : "")
                : secs < 6.5 ? "Turning on… closing " + _closed.Count + " app" + (_closed.Count == 1 ? "" : "s") : "Turning on… waiting for " + string.Join(", ", stillOpen.ToArray()) + " to close";
            UpdateStatus();
        }

        void FinishActivation(List<string> problems)
        {
            _confirmTimer.Stop();
            _issues.Clear(); _issues.AddRange(problems);
            foreach (var kv in _leftOpen) if (!ProtectedFromClose(kv.Key) && IsRunning(kv.Key)) _issues.Add(FriendlyApp(kv.Key) + " " + kv.Value);
            foreach (var n in _reopened) if (!ProtectedFromClose(n) && IsRunning(n) && !_issues.Contains(FriendlyApp(n) + " reopened itself")) _issues.Add(FriendlyApp(n) + " reopened itself");
            if (_notInstalled.Count > 0) _issues.Add(string.Join(", ", _notInstalled.ToArray()) + " not installed");
            _confirmedRunning.Clear();
            foreach (var i in ExpectedApps()) if (IsRunning(i.ProcessName)) _confirmedRunning.Add(i.ProcessName);

            int ready = _confirmedRunning.Count;
            var parts = new List<string>();
            parts.Add(ready + " app" + (ready == 1 ? "" : "s") + " ready" + (_alreadyOpen.Count > 0 ? " (" + _alreadyOpen.Count + " already open)" : ""));
            if (_cfg.CloseApps) parts.Add(_closed.Count + " closed" + (_forced > 0 ? " (" + _forced + " had to be forced)" : ""));
            if (_displayLine.Length > 0) parts.Add(_displayLine);
            parts.Add(_graphicsPending ? _cfg.GraphicsMode + " graphics change when iRacing closes" : _graphicsOk ? _cfg.GraphicsMode + " graphics set" : _cfg.GraphicsMode + " graphics not set");
            if (!_graphicsOk && !_graphicsPending) _issues.Add(_graphicsLine);
            if (_cfg.Streaming) parts.Add("streaming");

            _activation = _issues.Count == 0 ? "Ready to race" : NeedsALook();
            SetPhase(_issues.Count == 0 ? "active" : "issues");
            _mon.Event("Activated: " + _activation + " | " + string.Join(" · ", parts.ToArray()) + " | started: " + Join(_started) + " | already open: " + Join(_alreadyOpen) + " | closed: " + Join(_closed));
            Tick(_issues.Count == 0 ? "Race Mode is on and everything checked out" : "Race Mode is on, but " + _issues[0]);
            UpdateStatus();
        }

        static string Join(List<string> l) { return l.Count == 0 ? "none" : string.Join(", ", l.ToArray()); }

        // ---------------- Stream mode ----------------
        void StreamOn()
        {
            _cfg.Streaming = true;
            LaunchItems(_stream.Where(i => i.Enabled), "streaming app");
            BoostStreamApps();
            if (_phase == "active" || _phase == "issues") { _onAt = DateTime.Now.AddSeconds(-6.5); _confirmTimer.Start(); }
            _mon.Event("Streaming on");
            Tick("Streaming on");
        }

        void StreamOff()
        {
            _cfg.Streaming = false;
            int restored = 0;
            foreach (var kv in _boosted.Where(k => InList(_stream, k.Value) || string.Equals(k.Value, "obs64", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                try { using (var p = Process.GetProcessById(kv.Key)) { if (string.Equals(p.ProcessName, kv.Value, StringComparison.OrdinalIgnoreCase) && p.PriorityClass != ProcessPriorityClass.Normal) { p.PriorityClass = ProcessPriorityClass.Normal; restored++; } } }
                catch { }
                _boosted.Remove(kv.Key);
            }
            _mon.Event("Streaming off, restored " + restored + " priorities");
            Tick("Streaming off" + (restored > 0 ? " · OBS back to normal priority" : ""));
            if (_on) UpdateStatus();
        }

        // OBS (obs64) to AboveNormal so the sim at High can't starve the encoder; restored with everything else.
        void BoostStreamApps()
        {
            if (!_cfg.Streaming || !_cfg.StreamBoost) return;
            foreach (var p in Mine("obs64"))
            {
                try
                {
                    if (!_boosted.ContainsKey(p.Id) && p.PriorityClass == ProcessPriorityClass.Normal)
                    {
                        p.PriorityClass = ProcessPriorityClass.AboveNormal;
                        _boosted[p.Id] = p.ProcessName;
                        _mon.Event("OBS set to above-normal priority");
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        void LowerBackground()
        {
            foreach (var name in _cfg.Lower)
            {
                if (ProtectedFromLower(name)) continue;
                foreach (var p in Mine(name))
                {
                    try
                    {
                        if (p.Id == MyPid || _lowered.ContainsKey(p.Id)) continue;
                        if (p.PriorityClass == ProcessPriorityClass.Normal)
                        {
                            p.PriorityClass = ProcessPriorityClass.BelowNormal;
                            _lowered[p.Id] = name;
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
        }

        int _simLoggedPid = -1;                                   // last sim PID written to events.log
        readonly HashSet<int> _prioNoted = new HashSet<int>();    // sim PIDs whose priority outcome was already logged

        void WatchTick()
        {
            if (!_on) return;
            var sims = Mine(Monitor.SimName);
            try
            {
                if (sims.Length > 0)
                {
                    _goneTicks = 0;
                    _simSeen = true;
                    if (_simLoggedPid != sims[0].Id) { _simLoggedPid = sims[0].Id; _mon.Event("iRacing sim started (PID " + sims[0].Id + ")"); }
                    if (_cfg.SimHighPriority)
                    {
                        foreach (var p in sims)
                        {
                            try
                            {
                                if (p.PriorityClass != ProcessPriorityClass.High)
                                {
                                    p.PriorityClass = ProcessPriorityClass.High;
                                    _boosted[p.Id] = p.ProcessName;
                                    _simBoosted = true;
                                    _mon.Event("iRacing sim set to High priority (PID " + p.Id + ")");
                                    Tick("iRacing put first");
                                }
                                else if (_prioNoted.Add(p.Id) && !_boosted.ContainsKey(p.Id))
                                    _mon.Event("iRacing sim already at High priority (PID " + p.Id + "), set by something other than Race Mode");
                            }
                            catch (Exception ex)
                            {
                                // Logged once per sim process (this runs every 3 s), so a refusal shows up in events.log.
                                if (_prioNoted.Add(p.Id)) { _mon.Event("Couldn't raise iRacing priority (PID " + p.Id + "): " + ex.Message); Tick("Couldn't put iRacing first: Windows said no (" + ex.Message + ")"); }
                            }
                        }
                    }
                }
                else
                {
                    // Auto-off rule: stay on between races (sim closed, iRacing UI still open), and turn off
                    // once you've quit iRacing: the sim AND the UI both gone for two checks (~6 s grace for
                    // UI restarts). Counts once either the sim or the UI has been seen this session, so
                    // closing iRacing before ever joining a session also turns Race Mode off.
                    var ui = Mine("iRacingUI"); var uiOpen = ui.Length > 0; foreach (var p in ui) p.Dispose();
                    if (uiOpen) { _uiSeen = true; _goneTicks = 0; }
                    else if ((_simSeen || _uiSeen) && ++_goneTicks >= 2 && _cfg.AutoOff) { TurnOff("iRacing closed"); return; }
                }
            }
            finally { foreach (var p in sims) p.Dispose(); }

            if (_cfg.LowerBackground) LowerBackground();
            BoostStreamApps(); // OBS can be opened after Race Mode turned on
            WatchActivated();
            UpdateStatus();
        }

        // After activation: notice a Launch/streaming app that quits (e.g. a SimHub crash), and while streaming,
        // video memory that's nearly full with the sim running.
        void WatchActivated()
        {
            if (_phase != "active" && _phase != "issues") return;
            foreach (var name in _confirmedRunning.ToList())
            {
                if (IsRunning(name)) continue;
                _confirmedRunning.Remove(name);
                var item = _launch.Concat(_stream).FirstOrDefault(i => string.Equals(i.ProcessName, name, StringComparison.OrdinalIgnoreCase));
                var label = item != null ? item.Name : name;
                AddIssue(label + " stopped");
            }
            // An app left open (save prompt, admin) that has since closed, or is now protected (e.g. OBS once
            // Streaming is on), is no longer a problem: drop its issue.
            bool cleared = false;
            foreach (var kv in _leftOpen.ToList())
            {
                bool prot = ProtectedFromClose(kv.Key);
                if (!prot && IsRunning(kv.Key)) continue;
                _leftOpen.Remove(kv.Key);
                _issues.Remove(FriendlyApp(kv.Key) + " " + kv.Value);
                if (!prot && !_closed.Contains(kv.Key)) _closed.Add(kv.Key);
                _mon.Event(prot ? "Left " + kv.Key + " open: it's protected now" : kv.Key + " was closed after all");
                cleared = true;
            }
            if (cleared) { _activation = NeedsALook(); SetPhase(_issues.Count == 0 ? "active" : "issues"); }
            if (_cfg.Streaming && _cfg.VramCheck && !_vramWarned && _simSeen)
            {
                var s = _mon.Live;
                if (!double.IsNaN(s.VramUsedMB) && !double.IsNaN(s.VramTotalMB) && s.VramTotalMB > 0 && s.VramUsedMB / s.VramTotalMB >= 0.875)
                {
                    _vramWarned = true;
                    AddIssue("video memory at " + (s.VramUsedMB / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " of " + (s.VramTotalMB / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB, so your stream may stutter");
                }
            }
        }

        void AddIssue(string issue)
        {
            _issues.Add(issue);
            _mon.Event("Needs attention: " + issue);
            Tick(issue);
            _activation = NeedsALook();
            SetPhase("issues");
        }

        // "Needs a look: <first problem>" (+ how many more). Every problem is still listed in the checklist and events.log.
        string NeedsALook()
        {
            if (_issues.Count == 0) return "Ready to race";
            var first = _issues[0];
            return "Needs a look: " + first + (_issues.Count > 1 ? " (+" + (_issues.Count - 1) + " more)" : "");
        }

        // While on: one status line (ready, needs a look, or what it's waiting for), and a two-column checklist of what
        // Race Mode did, so the proof can be read at a glance without maximising the window.
        void UpdateStatus()
        {
            var first = _activation.Length > 0 ? _activation : "Turning on…";
            SetText("StatusText", first);
            var hc = Get<System.Windows.Controls.Primitives.UniformGrid>("HeroChecks");
            if (hc == null) return;
            hc.Children.Clear();
            bool appearing = _on && hc.Visibility != Visibility.Visible;
            hc.Visibility = _on ? Visibility.Visible : Visibility.Collapsed;
            if (!_on) return;
            // The one authored moment: the proof fades in once when Race Mode turns on (180 ms, ease-out; it just
            // appears when Windows' animation effects are off). Later refreshes don't animate.
            if (appearing)
            {
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new System.Windows.Media.Animation.ExponentialEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Exponent = 5 } };
                hc.BeginAnimation(UIElement.OpacityProperty, SystemParameters.ClientAreaAnimation ? fade : null);
            }
            bool turning = _phase == "turning";
            var expected = ExpectedApps();
            if (expected.Count > 0)
            {
                int running = expected.Count(i => IsRunning(i.ProcessName));
                hc.Children.Add(MiniCheck("Apps", running + " of " + expected.Count + " running", running == expected.Count ? "Live" : turning ? "Dim" : "Warn"));
            }
            if (_cfg.CloseApps && _close.Count > 0)
            {
                int open = _close.Count(n => !ProtectedFromClose(n) && IsRunning(n));
                hc.Children.Add(MiniCheck("Close list", open == 0 ? "all closed" : open + " still open", open == 0 ? "Live" : turning ? "Dim" : "Warn"));
            }
            bool simNow = IsRunning(Monitor.SimName);
            hc.Children.Add(MiniCheck("iRacing", simNow ? (_simBoosted && _cfg.SimHighPriority ? "racing, put first" : "racing") : _simSeen ? "between sessions" : "not in a session yet", simNow ? "Live" : "Off"));
            if (_displayLine.Length > 0) hc.Children.Add(MiniCheck("Screen", _displayLine == "screen OK" ? "OK" : "needs a look", _displayLine == "screen OK" ? "Live" : "Warn"));
            hc.Children.Add(MiniCheck(_cfg.GraphicsMode + " graphics", _graphicsPending ? "after iRacing closes" : _graphicsOk ? "set" : "see Graphics", _graphicsPending ? "Dim" : _graphicsOk ? "Live" : "Warn"));
            if (_lowered.Count > 0) hc.Children.Add(MiniCheck("Background", _lowered.Count + " slowed down", "Live"));
            if (_cfg.Streaming) hc.Children.Add(MiniCheck("OBS", IsRunning("obs64") ? "running" : "not running", IsRunning("obs64") ? "Live" : "Warn"));
            if (_mon.IsLogging) hc.Children.Add(MiniCheck("Log", "recording", "Live"));
        }

        // A compact checklist entry: dot + name + state. The words carry the meaning; the dot only repeats it.
        FrameworkElement MiniCheck(string name, string state, string brushKey)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 10, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0),
                                    Fill = brushKey == "Off" ? new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x60)) : (Brush)Window.Resources[brushKey] };
            var t = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            t.Inlines.Add(new Run(name + "  ") { Foreground = (Brush)Window.Resources["Ink"] });
            t.Inlines.Add(new Run(state) { Foreground = brushKey == "Warn" ? (Brush)Window.Resources["Warn"] : (Brush)Window.Resources["Dim"] });
            Grid.SetColumn(t, 1);
            g.Children.Add(dot); g.Children.Add(t);
            System.Windows.Automation.AutomationProperties.SetName(g, name + ": " + state);
            return g;
        }

        // A Remove can be undone for 10 seconds from the footer.
        Action _undo;
        DispatcherTimer _undoTimer;
        void OfferUndo(string msg, Action undo)
        {
            Tick(msg);
            _undo = undo;
            var b = Get<Button>("UndoBtn"); if (b == null) return;
            b.Visibility = Visibility.Visible;
            if (_undoTimer == null)
            {
                _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                _undoTimer.Tick += (s, e) => { _undoTimer.Stop(); _undo = null; Get<Button>("UndoBtn").Visibility = Visibility.Collapsed; };
                b.Click += (s, e) =>
                {
                    _undoTimer.Stop(); var u = _undo; _undo = null; b.Visibility = Visibility.Collapsed;
                    if (u != null) { u(); Tick("Put back"); }
                };
            }
            _undoTimer.Stop(); _undoTimer.Start();
        }

        void Tick(string msg)
        {
            var t = Window == null ? null : Get<TextBlock>("Ticker");
            if (t != null) t.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "   " + msg;
        }
    }

    // "steamwebhelper" -> "Steam (web helper)  ·  steamwebhelper" for the Close lists.
    public class FriendlyNameConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            var proc = value as string; if (proc == null) return value;
            var f = Shell.FriendlyApp(proc);
            if ("title".Equals(p)) return f;
            return f == proc ? proc : f + "  ·  " + proc;
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) { return value; }
    }

    // "steam · open now, 640 MB" / "spotify · not open". Snapshot() re-reads what's running (true if anything changed).
    public class CloseStateConverter : System.Windows.Data.IValueConverter
    {
        static Dictionary<string, double> _open = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        static string _sig = "";
        public static Func<string, string> KeptBecause;   // set by Shell: why a listed app won't be closed, or null
        public static bool Snapshot(IEnumerable<string> listed)
        {
            var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            int session; using (var me = Process.GetCurrentProcess()) session = me.SessionId;
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.SessionId == session) { double mb; d.TryGetValue(p.ProcessName, out mb); d[p.ProcessName] = mb + p.WorkingSet64 / 1048576.0; } }
                catch { }
                finally { p.Dispose(); }
            }
            // only a change that shows (open/closed, or memory moving by a visible step) refreshes the list
            var sig = string.Join("|", listed.Select(n => { double v; return n + ":" + (d.TryGetValue(n, out v) ? (int)(v / 100) : -1); }).ToArray());
            _open = d;
            if (sig == _sig) return false;
            _sig = sig; return true;
        }
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            var proc = value as string; if (proc == null) return "";
            double mb;
            // the process name only when it differs from the name shown above it ("steamwebhelper" under "Steam (web helper)")
            var lead = string.Equals(Shell.FriendlyApp(proc), proc, StringComparison.OrdinalIgnoreCase) ? "" : proc + " · ";
            var kept = KeptBecause == null ? null : KeptBecause(proc);
            if (kept != null) return lead + (lead.Length == 0 ? char.ToUpperInvariant(kept[0]) + kept.Substring(1) : kept);
            if (!_open.TryGetValue(proc, out mb)) return lead + (lead.Length == 0 ? "Not open" : "not open");
            return lead + (lead.Length == 0 ? "Open now, " : "open now, ") + (mb >= 1024 ? (mb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : Math.Max(1, (int)Math.Round(mb)) + " MB");
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) { return value; }
    }
}
