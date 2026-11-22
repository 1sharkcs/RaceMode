using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace RaceMode
{
    // Install, uninstall and "start with Windows". Per-user only: everything lives under
    // %LOCALAPPDATA%\RaceMode and HKCU, so no admin rights are ever needed.
    public static class Installer
    {
        // Tests (tests\SetupTests.cs) point every folder and registry name somewhere harmless with RACEMODE_TEST_ROOT.
        static string TestRoot { get { var r = Environment.GetEnvironmentVariable("RACEMODE_TEST_ROOT"); return string.IsNullOrEmpty(r) ? null : r; } }
        static string Under(Environment.SpecialFolder f, string test) { return TestRoot == null ? Environment.GetFolderPath(f) : Path.Combine(TestRoot, test); }

        public static string UninstallKey { get { return @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + (TestRoot == null ? "RaceMode" : "RaceModeTest"); } }
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static string RunName { get { return TestRoot == null ? "RaceMode" : "RaceModeTest"; } }

        public static string InstallDir { get { return Path.Combine(Under(Environment.SpecialFolder.LocalApplicationData, "LocalAppData"), "RaceMode"); } }
        public static string InstalledExe { get { return Path.Combine(InstallDir, "RaceMode.exe"); } }
        public static string DataDir { get { return Path.Combine(InstallDir, "data"); } }
        public static string StartMenuLnk { get { return Path.Combine(Under(Environment.SpecialFolder.Programs, "Programs"), "Race Mode.lnk"); } }
        public static string DesktopLnk { get { return Path.Combine(Under(Environment.SpecialFolder.DesktopDirectory, "Desktop"), "Race Mode.lnk"); } }
        static string ThisExe { get { return typeof(Installer).Assembly.Location; } }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static bool IsInstalledCopy { get { return SamePath(ThisExe, InstalledExe); } }

        // Another Race Mode (installed or not) is open right now.
        public static bool RaceModeRunning()
        {
            System.Threading.Mutex m;
            if (!System.Threading.Mutex.TryOpenExisting(TestRoot == null ? @"Local\RaceMode.Instance" : @"Local\RaceMode.Instance.Test", out m)) return false;
            m.Dispose(); return true;
        }

        public static string FindIRacingUi()
        {
            foreach (var p in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"iRacing\ui\iRacingUI.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"iRacing\ui\iRacingUI.exe") })
                if (File.Exists(p)) return p;
            return null;
        }

        // Copies this exe into %LOCALAPPDATA%\RaceMode, adds the shortcuts and the Settings > Apps entry.
        // Returns plain lines saying what was done. Throws if the exe itself can't be put in place.
        public static List<string> Install(bool desktop, bool startWithWindows)
        {
            var done = new List<string>();
            Directory.CreateDirectory(InstallDir);
            // leftovers from an earlier uninstall's cleanup that never finished
            try { foreach (var f in Directory.GetFiles(InstallDir, "RaceMode.exe.uninstall-*")) { try { File.Delete(f); } catch { } } } catch { }
            var target = InstalledExe;
            if (!SamePath(ThisExe, target))
            {
                var old = target + ".old";
                if (File.Exists(old)) File.Delete(old);
                bool had = File.Exists(target);
                var was = had ? InstalledVersion() : null;
                if (had) File.Move(target, old);
                try { File.Copy(ThisExe, target, false); }
                catch { if (had && !File.Exists(target)) File.Move(old, target); throw; }
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                done.Add(had ? DoneLine(was, Updater.Current) : "Installed Race Mode " + Updater.Current + " in " + InstallDir);
            }
            else done.Add("Race Mode is already in " + InstallDir);
            Directory.CreateDirectory(DataDir);
            done.Add("Your settings and logs go in " + DataDir);

            DataDirs.CreateShortcut(StartMenuLnk, target, InstallDir);
            done.Add("Added Race Mode to the Start menu");
            if (desktop) { DataDirs.CreateShortcut(DesktopLnk, target, InstallDir); done.Add("Added a desktop shortcut"); }

            WriteUninstallEntry(target);
            done.Add("Added Race Mode to Settings > Apps, so you can uninstall it there");

            SetStartWithWindows(startWithWindows, target);
            done.Add(startWithWindows ? "Race Mode will start with Windows, in the system tray" : "Race Mode won't start with Windows (you can change this in Settings)");
            Log("install", done);
            return done;
        }

        // The installed copy's version ("1.0.1", "1.0.2-rc.1"), or null if there's none or it can't be read.
        public static string InstalledVersion()
        {
            try
            {
                if (!File.Exists(InstalledExe)) return null;
                var v = (FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion ?? "").Trim();
                return Updater.Parse(v) == null ? null : v;
            }
            catch { return null; }
        }

        // Re-running setup can go back to an older version (README: it swaps a test version for the latest normal
        // release). Says so before Install is pressed; null when it doesn't go back or a version is unknown ("local").
        public static string GoingBackNote(string installed, string setup)
        {
            if (Updater.Parse(installed) == null || Updater.Parse(setup) == null || Updater.Compare(installed, setup) <= 0) return null;
            bool test = installed.IndexOf('-') >= 0, normal = setup.IndexOf('-') < 0;
            return "Race Mode " + installed + (test ? " (a test version)" : "") + " is installed. Installing goes back to " + setup +
                   (test && normal ? ", the latest normal release" : "") + "; your settings are kept.";
        }

        // The finished page's first line when setup replaced an installed copy.
        public static string DoneLine(string installed, string setup)
        {
            if (Updater.Parse(installed) == null || Updater.Parse(setup) == null) return "Updated Race Mode " + setup + " in " + InstallDir;
            int c = Updater.Compare(installed, setup);
            if (c > 0) return "Went back to Race Mode " + setup + " (from " + installed + ")";
            if (c < 0) return "Updated Race Mode from " + installed + " to " + setup;
            return "Reinstalled Race Mode " + setup;
        }

        public static void WriteUninstallEntry(string exe)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Race Mode");
                k.SetValue("DisplayVersion", Updater.Current);
                var owner = (Updater.Repo ?? "").Split('/')[0];
                k.SetValue("Publisher", owner.Length > 0 ? owner : "Race Mode");
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", Path.GetDirectoryName(exe));
                k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(Updater.Repo)) k.SetValue("URLInfoAbout", "https://github.com/" + Updater.Repo);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                try { k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024) + 1, RegistryValueKind.DWord); } catch { }
            }
        }

        // After an in-app update: keep the version in Settings > Apps current (installed copy only).
        public static void RefreshUninstallVersion()
        {
            try
            {
                if (!IsInstalledCopy) return;
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey, true)) if (k != null) k.SetValue("DisplayVersion", Updater.Current);
            }
            catch { }
        }

        // ---------------- start with Windows ----------------
        public static bool StartsWithWindows { get { return RunsAtStartup(ThisExe); } }

        public static bool RunsAtStartup(string exe)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) { var v = k == null ? null : k.GetValue(RunName) as string; return v != null && v.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0; } }
            catch { return false; }
        }

        public static void SetStartWithWindows(bool on, string exe)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + exe + "\" --minimized");
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName, false);
            }
        }

        public static void SetStartWithWindows(bool on) { SetStartWithWindows(on, ThisExe); }

        // ---------------- uninstall ----------------
        // Run from the installed exe (--uninstall). Removes what Install added. The exe can't delete
        // itself while running, so a hidden cmd waits a moment and then removes the files.
        public static List<string> Uninstall(bool keepData)
        {
            var done = new List<string>();
            var dir = InstallDir;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    var v = k == null ? null : k.GetValue(RunName) as string;
                    if (v != null && v.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0) { k.DeleteValue(RunName, false); done.Add("Race Mode won't start with Windows any more"); }
                }
            }
            catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); done.Add("Removed Race Mode from Settings > Apps"); } catch { }
            foreach (var lnk in new[] { StartMenuLnk, DesktopLnk })
                try { if (File.Exists(lnk) && SamePath(DataDirs.ShortcutTarget(lnk) ?? "", InstalledExe)) { File.Delete(lnk); done.Add("Removed " + Path.GetFileName(lnk) + (lnk == DesktopLnk ? " from the desktop" : " from the Start menu")); } }
                catch { }

            // Only ever delete inside %LOCALAPPDATA%\RaceMode, and never a path cmd would rewrite (quotes, %, !).
            if (!SamePath(Path.GetDirectoryName(ThisExe), dir) || !dir.EndsWith(@"\RaceMode", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This copy of Race Mode isn't the installed one, so its files were left alone");
            if (dir.IndexOfAny(new[] { '"', '%', '!' }) >= 0)
                throw new InvalidOperationException("its folder's path has a \", % or ! in it, which Windows' cleanup can't handle safely. Everything else is removed; delete " + dir + " yourself once Race Mode is closed");
            // The exe stays locked until this process exits (the user may still be reading the last message),
            // so keep trying (once a second, for up to ten minutes) instead of one delete after a fixed wait.
            // A running exe can be renamed: move it to a name only this uninstall uses, so a reinstall made before
            // the last message is closed can't be deleted by the cleanup below.
            var exe = dir + "\\RaceMode.exe";
            string renamedFrom = null;
            try { var gone = exe + ".uninstall-" + Process.GetCurrentProcess().Id; File.Move(exe, gone); renamedFrom = exe; exe = gone; } catch { }
            var script = "(for /l %i in (1,1,600) do if exist \"" + exe + "\" (del /f /q \"" + exe + "\" >nul 2>&1 & if exist \"" + exe + "\" ping -n 2 127.0.0.1 >nul))" +
                         " & del /f /q \"" + dir + "\\RaceMode.exe.old\" \"" + dir + "\\RaceMode.exe.failed\" >nul 2>&1";
            if (!keepData) script += " & if not exist \"" + dir + "\\RaceMode.exe\" rmdir /s /q \"" + dir + "\\data\"";
            script += " & rmdir \"" + dir + "\" >nul 2>&1";
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"" + script + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() }); }
            catch
            {
                if (renamedFrom != null) { try { File.Move(exe, renamedFrom); } catch { } }
                throw;
            }
            done.Add(keepData ? "Removed Race Mode. Your settings and logs are still in " + DataDir : "Removed Race Mode and its settings and logs");
            if (keepData) Log("uninstall", done);
            return done;
        }

        // data\setup.log: what install/uninstall did (never secrets), for diagnosing problems.
        // The helpers (HWiNFO, OBS) pass the running copy's own data folder, so a portable copy logs beside itself.
        public static void Log(string what, IEnumerable<string> lines, string dir = null)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) dir = DataDir;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "setup.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + what + " (" + Updater.Current + ")" + Environment.NewLine +
                    string.Concat(lines.Select(l => "  " + l + Environment.NewLine)));
            }
            catch { }
        }
    }

    // A page in the setup overlay (SetupOverlay in RaceMode.xaml).
    public class SetupPage
    {
        public string Title = "", Intro = "", NextText = "Next";
        public Func<UIElement> Build;
        public Func<bool> OnNext;          // false = stay on this page
        public bool CanSkip = true, CanGoBack = true;
        public bool QuietNext;             // the page has its own main button, so the footer's Next/Done is secondary
        public Action OnLeave;
    }

    // Shows setup pages one after another in the main window's SetupOverlay.
    public class SetupFlow
    {
        readonly Window _w;
        readonly List<SetupPage> _pages;
        readonly Action _finished;
        int _i;

        static Window _host;   // where the shared styles (Group, Row, Btn...) come from

        public SetupFlow(Window w, List<SetupPage> pages, Action finished)
        {
            _w = w; _pages = pages; _finished = finished; _host = w;
            Btn("SetupNext").Click += (s, e) => Next(true);
            Btn("SetupSkip").Click += (s, e) => Next(false);
            Btn("SetupBack").Click += (s, e) => { if (_i > 0) { Leave(); _i--; Render(); } };
        }

        Button Btn(string n) { return (Button)_w.FindName(n); }

        public void Start() { Start(0); }

        // Opens setup at one page (e.g. the Helpers page from the CPU temperature tile).
        public void Start(int page)
        {
            ShowOverlay(true);
            _i = Math.Max(0, Math.Min(page, _pages.Count - 1)); Render();
        }

        // Ends setup from inside a page (e.g. a "Turn It On Now" button on the last page).
        public void Finish()
        {
            Leave();
            ShowOverlay(false);
            if (_finished != null) _finished();
        }

        // While setup shows, everything behind it is hidden too, so the keyboard and screen readers only reach setup.
        readonly Dictionary<UIElement, Visibility> _behind = new Dictionary<UIElement, Visibility>();
        void ShowOverlay(bool on)
        {
            var overlay = (UIElement)_w.FindName("SetupOverlay");
            var root = (Panel)_w.FindName("Root");
            if (on)
            {
                if (overlay.Visibility == Visibility.Visible) return;   // already showing: keep the first snapshot
                _behind.Clear();
                foreach (UIElement el in root.Children)
                    if (el != overlay && !(el is FrameworkElement && ((FrameworkElement)el).Name == "TitleBar")) { _behind[el] = el.Visibility; el.Visibility = Visibility.Collapsed; }
                overlay.Visibility = Visibility.Visible;
            }
            else
            {
                overlay.Visibility = Visibility.Collapsed;
                foreach (var kv in _behind) kv.Key.Visibility = kv.Value;
                _behind.Clear();
            }
        }

        public void Refresh() { Render(); }
        public int Current { get { return _i; } }

        void Leave() { var p = _pages[_i]; if (p.OnLeave != null) p.OnLeave(); }

        void Next(bool next)
        {
            var p = _pages[_i];
            if (next && p.OnNext != null && !p.OnNext()) { Render(); return; }
            Leave();
            if (_i + 1 >= _pages.Count) { ShowOverlay(false); if (_finished != null) _finished(); return; }
            _i++; Render();
        }

        void Render()
        {
            var p = _pages[_i];
            ((TextBlock)_w.FindName("SetupStep")).Text = _pages.Count > 1 ? "Step " + (_i + 1) + " of " + _pages.Count : "";
            ((TextBlock)_w.FindName("SetupTitle")).Text = p.Title;
            var intro = (TextBlock)_w.FindName("SetupIntro");
            intro.Text = p.Intro; intro.Visibility = string.IsNullOrEmpty(p.Intro) ? Visibility.Collapsed : Visibility.Visible;
            var host = (StackPanel)_w.FindName("SetupPage");
            host.Children.Clear();
            if (p.Build != null) { var el = p.Build(); if (el != null) host.Children.Add(el); }
            Btn("SetupNext").Content = p.NextText;
            Btn("SetupNext").Style = Res<Style>(p.QuietNext ? "Btn" : "PrimaryBtn");
            Btn("SetupSkip").Visibility = p.CanSkip ? Visibility.Visible : Visibility.Collapsed;
            Btn("SetupBack").Visibility = p.CanGoBack && _i > 0 ? Visibility.Visible : Visibility.Hidden;
            Btn("SetupNext").Focus();
        }

        // ---------------- building blocks (same look as the rest of the app) ----------------
        public static Border Group(params UIElement[] rows)
        {
            var sp = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                var b = new Border { Style = Res<Style>(i == rows.Length - 1 ? "LastRow" : "Row"), Child = rows[i] };
                sp.Children.Add(b);
            }
            return new Border { Style = Res<Style>("Group"), Child = sp, Margin = new Thickness(0, 0, 0, 14) };
        }

        // A small heading between groups ("Also on this PC, not open now").
        public static UIElement Heading(string text)
        {
            return new TextBlock { Text = text, Style = Res<Style>("Sub"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        }

        public static UIElement Text(string title, string sub)
        {
            var sp = new StackPanel();
            var head = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            sp.Children.Add(head);
            if (!string.IsNullOrEmpty(sub)) sp.Children.Add(new TextBlock { Text = sub, Style = Res<Style>("Sub"), TextWrapping = TextWrapping.Wrap });
            return sp;
        }

        public static CheckBox Switch(string title, string sub, bool on)
        {
            var cb = new CheckBox { Style = Res<Style>("SwitchRow"), IsChecked = on, Content = Text(title, sub) };
            AutomationAttach(cb, title);
            return cb;
        }

        // A status line: coloured dot + text. ok: true = green, false = amber, null = grey. The words carry the meaning too.
        // A list row with the same checkbox as the app's own lists (Launch, Close); clicking the words ticks it too.
        public static UIElement CheckRow(string title, string sub, bool on, out CheckBox box)
        {
            var g = new Grid { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var cb = new CheckBox { Style = Res<Style>("Check"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
            AutomationAttach(cb, title);
            g.Children.Add(cb);
            var t = (FrameworkElement)Text(title, sub); t.Margin = new Thickness(12, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(t, 1); g.Children.Add(t);
            t.MouseLeftButtonUp += (s, e) => { cb.IsChecked = cb.IsChecked != true; };
            box = cb;
            return g;
        }

        // The same row with a value on the right (e.g. "1.2 GB"), in the numeral face, so values line up in a column.
        public static UIElement CheckRow(string title, string sub, string value, bool on, out CheckBox box)
        {
            var g = (Grid)CheckRow(title, sub, on, out box);
            if (string.IsNullOrEmpty(value)) return g;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var v = new TextBlock { Text = value, FontFamily = Res<FontFamily>("Display"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            v.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            Grid.SetColumn(v, 2); g.Children.Add(v);
            var cb = box; v.MouseLeftButtonUp += (s, e) => { cb.IsChecked = cb.IsChecked != true; };
            System.Windows.Automation.AutomationProperties.SetHelpText(cb, value + (string.IsNullOrEmpty(sub) ? "" : ". " + sub));
            return g;
        }

        // A heading with a small action on the right ("Open now ... Tick All").
        public static UIElement HeadingWithAction(string text, string action, Action click)
        {
            var g = new Grid { Margin = new Thickness(2, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = text, Style = Res<Style>("Sub"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
            if (!string.IsNullOrEmpty(action))
            {
                var b = new Button { Style = Res<Style>("Btn"), Content = action, FontSize = 12, Padding = new Thickness(10, 3, 10, 3), MinHeight = 0 };
                b.Click += (s, e) => click();
                Grid.SetColumn(b, 1); g.Children.Add(b);
            }
            return g;
        }

        // A group that opens and closes (the same disclosure arrow as Advanced settings). changed: remembers the state
        // across re-renders of the page.
        public static Border Fold(string title, string sub, bool open, Action<bool> changed, params UIElement[] rows)
        {
            var tb = new System.Windows.Controls.Primitives.ToggleButton { Style = Res<Style>("Disclosure"), Content = Text(title, sub), IsChecked = open };
            AutomationAttach(tb, title);
            var inner = new StackPanel();
            for (int i = 0; i < rows.Length; i++) inner.Children.Add(new Border { Style = Res<Style>(i == rows.Length - 1 ? "LastRow" : "Row"), Child = rows[i] });
            var panel = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = inner, Visibility = open ? Visibility.Visible : Visibility.Collapsed };
            panel.SetResourceReference(Border.BorderBrushProperty, "Hairline");
            tb.Checked += (s, e) => { panel.Visibility = Visibility.Visible; if (changed != null) changed(true); };
            tb.Unchecked += (s, e) => { panel.Visibility = Visibility.Collapsed; if (changed != null) changed(false); };
            var sp = new StackPanel(); sp.Children.Add(tb); sp.Children.Add(panel);
            return new Border { Style = Res<Style>("Group"), Child = sp, Margin = new Thickness(0, 0, 0, 14) };
        }

        public static UIElement Status(string title, string sub, bool? ok)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(0, 5, 10, 0), VerticalAlignment = VerticalAlignment.Top
            };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ok == true ? "Live" : ok == false ? "Warn" : "DotOff");
            g.Children.Add(dot);
            var t = Text(title, sub); Grid.SetColumn(t, 1); g.Children.Add(t);
            return g;
        }

        public static Button Button(string text, Action click) { return Button(text, click, false); }

        public static Button Button(string text, Action click, bool primary)
        {
            var b = new Button { Style = Res<Style>(primary ? "PrimaryBtn" : "Btn"), Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            b.Click += (s, e) => click();
            return b;
        }

        public static StackPanel Stack(params UIElement[] items)
        {
            var sp = new StackPanel();
            foreach (var i in items) if (i != null) sp.Children.Add(i);
            return sp;
        }

        static void AutomationAttach(FrameworkElement el, string name) { System.Windows.Automation.AutomationProperties.SetName(el, name); }

        public static T Res<T>(string key) where T : class { var w = _host ?? Application.Current.MainWindow; return w != null ? w.TryFindResource(key) as T : null; }
    }

    // The --install window: the main window's layout with only the setup overlay showing.
    public static class InstallerWindow
    {
        public static void Run(Application app)
        {
            Window w;
            using (var fs = typeof(Installer).Assembly.GetManifestResourceStream("RaceMode.xaml")) w = (Window)XamlReader.Load(fs);
            // setup is one column of questions: narrower and taller than the app's own default
            w.MinWidth = 560; w.MinHeight = 620; w.Width = 600;
            w.Height = Math.Min(820, SystemParameters.WorkArea.Height - 16);
            app.MainWindow = w;
            Palette.Apply(w, Palette.IsLight("System"));   // no settings yet: follow Windows' app mode (Theme.cs)
            Shell.ApplyIcon(w);
            ((UIElement)w.FindName("MaxBtn")).Visibility = Visibility.Collapsed;
            ((UIElement)w.FindName("SmallBtn")).Visibility = Visibility.Collapsed;
            ((Button)w.FindName("CloseBtn")).Click += (s, e) => w.Close();
            ((Button)w.FindName("MinBtn")).Click += (s, e) => w.WindowState = WindowState.Minimized;
            ((Grid)w.FindName("TitleBar")).MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch { } };

            SetupFlow flow = null;
            // A repair keeps what the user chose last time: start with Windows stays on if it was on, and a desktop
            // shortcut they deleted isn't brought back. A first install starts with: shortcut yes, start with Windows no.
            bool reinstall = File.Exists(Installer.InstalledExe);
            bool desktop = !reinstall || File.Exists(Installer.DesktopLnk), autostart = reinstall && Installer.RunsAtStartup(Installer.InstalledExe);
            var goingBack = reinstall ? Installer.GoingBackNote(Installer.InstalledVersion(), Updater.Current) : null;
            List<string> result = null; string error = null;
            var pages = new List<SetupPage>();
            pages.Add(new SetupPage
            {
                Title = "Install Race Mode",
                Intro = "Race Mode goes in your own user folder, so it doesn't need admin rights. Your settings are kept apart from the app, so updates never touch them.",
                NextText = "Install", CanSkip = false, CanGoBack = false,
                Build = () =>
                {
                    bool running = Installer.RaceModeRunning();
                    var iracing = Installer.FindIRacingUi();
                    var ini = DisplayGuard.DefaultIniPath;
                    var checks = SetupFlow.Group(
                        SetupFlow.Status(iracing != null ? "iRacing is installed" : "iRacing wasn't found",
                            iracing != null ? null : "Race Mode is made for iRacing and looks for it in Program Files. You can still install it now.", iracing != null),
                        SetupFlow.Status(File.Exists(ini) ? "iRacing's settings file is there" : "iRacing hasn't been run on this PC yet",
                            File.Exists(ini) ? null : "Start iRacing once, so Race Mode has graphics settings to work with.", File.Exists(ini)),
                        running ? SetupFlow.Stack(SetupFlow.Status("Race Mode is running", "Quit it first: close its window, then right-click its icon in the system tray (next to the clock) and choose Quit. Then try again.", false),
                                                  SetupFlow.Button("Try Again", () => flow.Refresh())) : SetupFlow.Status("Race Mode is closed, ready to install", null, true));
                    var dcb = SetupFlow.Switch("Add a desktop shortcut", null, desktop);
                    dcb.Checked += (s, e) => desktop = true; dcb.Unchecked += (s, e) => desktop = false;
                    var acb = SetupFlow.Switch("Start Race Mode with Windows", "Starts in the system tray, next to the clock, when you sign in, so it's ready before a race. You can change this later in Settings.", autostart);
                    acb.Checked += (s, e) => autostart = true; acb.Unchecked += (s, e) => autostart = false;
                    var errEl = error == null ? null : SetupFlow.Group(SetupFlow.Status("Install didn't finish", error, false));
                    var backEl = goingBack == null ? null : SetupFlow.Group(SetupFlow.Status("This installs an older version", goingBack, null));
                    return SetupFlow.Stack(errEl, backEl, checks, SetupFlow.Group(dcb, acb));
                },
                OnNext = () =>
                {
                    if (Installer.RaceModeRunning()) { error = "Race Mode is still running. Quit it (right-click its icon in the system tray, then Quit), then click Install again."; return false; }
                    try { result = Installer.Install(desktop, autostart); error = null; return true; }
                    catch (Exception ex) { error = ex.Message; Shell.LogCrash(ex); return false; }
                }
            });
            pages.Add(new SetupPage
            {
                Title = goingBack != null ? "Went back to Race Mode " + Updater.Current : reinstall ? "Race Mode is updated" : "Race Mode is installed",
                Intro = reinstall ? "Your settings were kept. Open it to carry on where you left off." : "The next screens help you set it up. It takes about a minute.",
                NextText = "Open Race Mode", CanSkip = false, CanGoBack = false,
                Build = () => SetupFlow.Group((result ?? new List<string>()).Select(l => SetupFlow.Status(l, null, true)).ToArray()),
                OnNext = () =>
                {
                    try { Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = false, WorkingDirectory = Installer.InstallDir }); }
                    catch (Exception ex) { error = "Couldn't open Race Mode: " + ex.Message; }
                    return true;
                }
            });
            flow = new SetupFlow(w, pages, () => w.Close());
            w.Loaded += (s, e) => flow.Start();
            app.Run(w);
            // 0 only if the install itself went through; closing the window early (or a failed install) is 1
            Environment.ExitCode = result != null ? 0 : 1;
        }
    }
}
