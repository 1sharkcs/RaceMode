using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace RaceMode
{
    // The first-run questions (shown once, on a new config, or with --setup). Every page can be skipped;
    // everything here can be changed later on the normal tabs.
    public partial class Shell
    {
        bool _freshConfig;
        SetupFlow _firstRun;
        DispatcherTimer _helperTimer;

        // The latest scan for apps to close (null until one has finished), shared by setup and the Close tab.
        List<AppSuggestion> _closeScan;
        bool _closeScanStreaming, _closeScanning;
        DateTime _closeScanAt;
        readonly List<Action> _closeScanWaiters = new List<Action>();

        // A scan is reused for a minute, and only for the same Streaming choice.
        bool CloseScanFresh(bool streamingOn) { return _closeScan != null && _closeScanStreaming == streamingOn && (DateTime.Now - _closeScanAt).TotalSeconds < 60; }

        // Measures for 3 s on a background thread, then calls every waiting caller on the UI thread. What's protected
        // is copied here first, so the scan never reads the live Launch and Stream lists from its own thread.
        void StartCloseScan(bool streamingOn, Action done)
        {
            if (done != null) _closeScanWaiters.Add(done);
            if (_closeScanning) return;
            _closeScanning = true;
            var already = _close.ToList();
            var prot = new HashSet<string>(AlwaysProtected, StringComparer.OrdinalIgnoreCase);
            foreach (var l in _launch) if (!string.IsNullOrEmpty(l.ProcessName)) prot.Add(l.ProcessName);
            if (_cfg.Streaming || streamingOn) foreach (var l in _stream) if (!string.IsNullOrEmpty(l.ProcessName)) prot.Add(l.ProcessName);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                List<AppSuggestion> found;
                try { found = AppScan.Scan(TimeSpan.FromSeconds(3), n => prot.Contains(n), already, streamingOn); }
                catch (Exception ex) { LogCrash(ex); found = new List<AppSuggestion>(); }
                Window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _closeScan = found; _closeScanStreaming = streamingOn; _closeScanAt = DateTime.Now; _closeScanning = false;
                    var waiters = _closeScanWaiters.ToList(); _closeScanWaiters.Clear();
                    foreach (var w in waiters) try { w(); } catch (Exception ex) { LogCrash(ex); }
                }));
            });
        }

        // Every Close list name for a picked row (an app can run as several processes).
        string[] NamesFor(string process)
        {
            var s = _closeScan == null ? null : _closeScan.FirstOrDefault(a => string.Equals(a.Process, process, StringComparison.OrdinalIgnoreCase));
            return s != null && s.Names != null && s.Names.Length > 0 ? s.Names : new[] { process };
        }

        static string MemText(double mb)
        {
            return mb >= 1024 ? (mb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : Math.Max(1, (int)Math.Round(mb)) + " MB";
        }

        // The line under an app's name: why it's suggested, plus processor use and "starts with Windows" when they apply.
        static bool IsBrowserApp(AppSuggestion a) { return IsBrowser(a.Process) || (a.Known && (a.Why ?? "").IndexOf("web browser", StringComparison.OrdinalIgnoreCase) >= 0); }
        static string SuggestionWhy(AppSuggestion a) { return SuggestionWhy(a, "tick"); }
        static string SuggestionWhy(AppSuggestion a, string verb)
        {
            var why = a.Known ? Cap(a.Why) : "Check what it is before you " + verb + " it";
            if (IsBrowserApp(a)) why = "A browser: closing it closes every tab";
            var bits = new List<string>();
            if (!string.IsNullOrEmpty(why)) bits.Add(why);
            if (a.Open && a.CpuPct >= 1) bits.Add(a.CpuPct.ToString("0", CultureInfo.InvariantCulture) + "% processor");
            if (a.StartsWithWindows) bits.Add("starts with Windows");
            return string.Join(" · ", bits.ToArray());
        }
        // What a Launch app is for ("Dashboards, bass shakers, LEDs"), when Race Mode knows it.
        static string LaunchWhy(LaunchItem it)
        {
            var name = !string.IsNullOrEmpty(it.ProcessName) ? it.ProcessName : SafeFileName(it.Path);
            var app = AppScan.Apps.FirstOrDefault(a => a.process.Contains(name, StringComparer.OrdinalIgnoreCase) || (a.launch != null && a.launch.Contains(name, StringComparer.OrdinalIgnoreCase)));
            return app == null ? null : Cap(app.why);
        }
        string LaunchPageIntro()
        {
            if (_launch.Count > 0)
            {
                // "found" only for what the scan of this PC turned up, not for apps the user added themselves
                int fromScan = _launchFound == null ? 0 : _launch.Count(l => _launchFound.Any(f => string.Equals(f.Path, l.Path, StringComparison.OrdinalIgnoreCase)));
                return "When you turn Race Mode on, it opens the ticked apps for you." +
                    (fromScan == 0 ? "" : fromScan == _launch.Count ? " These race apps were found on your PC." : " Some of them were found on your PC.");
            }
            return LaunchSuggestions().Count > 0 ? "When you turn Race Mode on, it opens the apps you add here. Add the race apps you use from the ones found on your PC."
                 : "When you turn Race Mode on, it opens the apps you add here. Race Mode didn't find any race apps it knows; add the ones you use, or skip this for now.";
        }

        // ---------------- race apps found on this PC (for the Launch list) ----------------
        List<LaunchItem> _launchFound;   // null until the background scan has finished
        bool _launchScanning;

        // Looks for installed race apps (Start menu shortcuts, Settings > Apps) on a background thread. On a fresh
        // config, before setup is finished, the ones it finds in Program Files, %LOCALAPPDATA%\Programs or the app's
        // own default folder are added to the list ticked (copies, so the found list stays as found). Everything
        // else, and everything found later, is only offered ("Found on your PC"), showing where it is.
        void StartLaunchScan()
        {
            if (_launchScanning) return;
            _launchScanning = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                List<LaunchItem> found;
                try { found = LaunchScan.Find(); }
                catch (Exception ex) { LogCrash(ex); found = new List<LaunchItem>(); }
                Window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _launchScanning = false;
                    _launchFound = found;
                    if (_freshConfig && _cfg.SetupDone == false)
                    {
                        var add = LaunchSuggestions().Where(it => LaunchScan.AutoTick(AppScan.Apps, it)).ToList();
                        foreach (var it in add) _launch.Add(CopyFound(it));
                        if (add.Count > 0) SaveConfig();
                    }
                    RefreshLaunchSuggest();
                    if (_firstRun != null && _firstRun.Current == 1) _firstRun.Refresh();
                    if (_whatsNew != null && _whatsNew.Current == 1) _whatsNew.Refresh();
                }));
            });
        }

        // Found race apps that aren't on the Launch list.
        List<LaunchItem> LaunchSuggestions()
        {
            return _launchFound == null ? new List<LaunchItem>() : LaunchScan.NotListed(AppScan.Apps, _launchFound, _launch);
        }

        void AddFoundLaunch(LaunchItem f, bool say)
        {
            if (f == null || LaunchScan.NotListed(AppScan.Apps, new[] { f }, _launch).Count == 0) return;
            _launch.Add(CopyFound(f));
            SaveConfig();
            RefreshLaunchSuggest();
            if (say) Tick("Added " + f.Name + " to the Launch list");
        }

        // A ticked Launch item made from a found one (never the same object as in _launchFound).
        static LaunchItem CopyFound(LaunchItem f)
        {
            return new LaunchItem { Name = f.Name, Path = f.Path, Args = f.Args ?? "", ProcessName = f.ProcessName, Enabled = true };
        }

        // "Dashboards, haptics and telemetry · C:\Program Files (x86)\SimHub": what a found app is for and where it is.
        static string FoundSubtitle(LaunchItem it)
        {
            var why = LaunchWhy(it);
            string dir; try { dir = System.IO.Path.GetDirectoryName(it.Path ?? ""); } catch { dir = null; }
            if (string.IsNullOrEmpty(dir)) dir = it.Path;
            return string.IsNullOrEmpty(why) ? dir : string.IsNullOrEmpty(dir) ? why : why + " · " + dir;
        }

        // A setup row: the app's name, what it's for and its folder, with an Add button on the right.
        UIElement SuggestRow(LaunchItem it, Action add)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = (FrameworkElement)SetupFlow.Text(it.Name, FoundSubtitle(it));
            t.VerticalAlignment = VerticalAlignment.Center; t.ToolTip = it.Path;
            g.Children.Add(t);
            var b = new Button { Style = SetupFlow.Res<Style>("Btn"), Content = "Add", FontSize = 12, Padding = new Thickness(12, 5, 12, 5), MinHeight = 28, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(b, "Add " + it.Name);
            b.Click += (s, e) => add();
            Grid.SetColumn(b, 1); g.Children.Add(b);
            return g;
        }

        // Launch tab: one button per found app above Add App… ("Found on your PC: + SimHub").
        void RefreshLaunchSuggest()
        {
            var panel = Window.FindName("LaunchSuggest") as WrapPanel;
            if (panel == null) return;
            panel.Children.Clear();
            var found = LaunchSuggestions();
            panel.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (found.Count == 0) return;
            panel.Children.Add(new TextBlock { Text = "Found on your PC:", Style = SetupFlow.Res<Style>("Sub"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
            foreach (var f in found)
            {
                var item = f;
                var b = new Button { Style = SetupFlow.Res<Style>("Btn"), Content = "+ " + item.Name, FontSize = 12, Padding = new Thickness(12, 5, 12, 5), MinHeight = 28, Margin = new Thickness(0, 0, 8, 6), ToolTip = item.Path };
                System.Windows.Automation.AutomationProperties.SetName(b, "Add " + item.Name + " to the Launch list");
                b.Click += (s, e) => AddFoundLaunch(item, true);
                panel.Children.Add(b);
            }
        }

        static string SafeFileName(string p) { try { return System.IO.Path.GetFileNameWithoutExtension(p ?? ""); } catch { return ""; } }

        // "dashboards" -> "Dashboards", but names that start lower-case stay as they are ("iRacing overlays").
        static string Cap(string s) { return string.IsNullOrEmpty(s) || (s.Length > 1 && char.IsUpper(s[1])) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }

        void MaybeStartFirstRun()
        {
            var args = Environment.GetCommandLineArgs();
            bool again = args.Length >= 2 && args[1] == "--setup";
            if (!_freshConfig && _cfg.SetupDone != false && !again) return;   // null: a config from before this flag (setup was done)
            Window.Loaded += (s, e) => StartFirstRun();
        }

        public void StartFirstRun() { StartFirstRun(0); }
        void StartFirstRun(int startAt)
        {
            if (_firstRun != null) { _firstRun.Start(Math.Max(0, startAt)); return; }
            var pages = new List<SetupPage>();

            // 1. racing monitor: the same picture and rules as Settings; the choice is kept until Next.
            int draft = 0;
            bool suggested = false;
            pages.Add(new SetupPage
            {
                Title = "Which screen do you race on?",
                Intro = "Before each race, Race Mode checks that iRacing will open on this screen, or across your three screens, and fill it as a borderless window. Any fix is made while iRacing is closed. Your FPS cap is left alone.",
                Build = () =>
                {
                    FillMonitorPick();
                    var src = Get<ComboBox>("MonitorPick");
                    suggested = false;
                    if (src.SelectedIndex > 0) draft = src.SelectedIndex;
                    else if (_pickMonitors.Count > 0) { draft = 1 + SuggestRacingMonitor(); suggested = true; }
                    else draft = 0;
                    // iRacing is already set up for three screens and three sit side by side: those are the pick.
                    bool tripleSuggested = suggested && _pickTriples.Count == 1 && DisplayGuard.IniNumMonitors(IniPath()) == 3;
                    if (tripleSuggested) draft = 1 + _pickMonitors.Count;
                    string why = !suggested ? "" : tripleSuggested ? "Your three screens are picked, because iRacing is set up for three."
                        : _pickMonitors.Count == 1 ? "Your only screen is picked." : "Your biggest screen is picked.";
                    return SetupFlow.Group(BuildScreenPicker(() => draft, i => draft = i, why));
                },
                OnNext = () =>
                {
                    var src = Get<ComboBox>("MonitorPick");
                    if (src != null && draft != src.SelectedIndex) src.SelectedIndex = draft;
                    return true;
                }
            });

            // 2. apps to start
            SetupPage launchPage = null;
            launchPage = new SetupPage
            {
                Title = "Apps to start before a race",
                Build = () =>
                {
                    launchPage.Intro = LaunchPageIntro();
                    var rows = new List<UIElement>();
                    foreach (var it in _launch.ToList())
                    {
                        var item = it; CheckBox cb;
                        rows.Add(SetupFlow.CheckRow(item.Name, item.Installed ? LaunchWhy(item) : "Not found at " + item.Path, item.Enabled, out cb));
                        cb.Checked += (s, e) => { item.Enabled = true; SaveConfig(); };
                        cb.Unchecked += (s, e) => { item.Enabled = false; SaveConfig(); };
                    }
                    if (rows.Count == 0) rows.Add(SetupFlow.Text(_launchFound == null ? "Looking for race apps on this PC…" : "No race apps found",
                        _launchFound == null ? "This takes a few seconds." : "Add the ones you use, like SimHub or Crew Chief."));
                    // installed race apps that aren't on the list: one tap adds one (none are ticked for you here)
                    var found = LaunchSuggestions();
                    UIElement foundHead = null, foundGroup = null;
                    if (found.Count > 0)
                    {
                        foundHead = SetupFlow.HeadingWithAction("Found on your PC", found.Count > 1 ? "Add All" : null,
                            () => { foreach (var f in LaunchSuggestions()) AddFoundLaunch(f, false); _firstRun.Refresh(); });
                        foundGroup = SetupFlow.Group(found.Select(f => { var item = f; return SuggestRow(item, () => { AddFoundLaunch(item, true); _firstRun.Refresh(); }); }).ToArray());
                    }
                    return SetupFlow.Stack(SetupFlow.Group(rows.ToArray()), foundHead, foundGroup,
                        SetupFlow.Button("Add App…", () => { AddLaunchApp(); _firstRun.Refresh(); }));
                }
            };
            pages.Add(launchPage);

            // 3. apps to close: open apps (measured for a few seconds) with their memory in a column, heavy apps it
            // doesn't recognise on their own, then installed-but-closed apps folded away. A running total says what
            // the ticked ones would free. Nothing is ticked for you.
            var closePick = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool foldOpen = false;
            pages.Add(new SetupPage
            {
                Title = "Apps to close before a race",
                Intro = "Closing heavy apps frees your processor and memory for iRacing. Save your work first: apps still open 5 seconds later are closed for you, unless they're asking to save. None are ticked for you.",
                Build = () =>
                {
                    if (!CloseScanFresh(true))
                    {
                        StartCloseScan(true, () => { if (_firstRun != null && _firstRun.Current == 2) _firstRun.Refresh(); });
                        return SetupFlow.Group(SetupFlow.Text("Checking what's open on this PC…", "This takes a few seconds."));
                    }
                    var known = _closeScan.Where(a => a.Open && a.Known).ToList();
                    var other = _closeScan.Where(a => a.Open && !a.Known).ToList();
                    var notOpen = _closeScan.Where(a => !a.Open).ToList();
                    var total = new TextBlock { Style = SetupFlow.Res<Style>("Sub"), Margin = new Thickness(2, 0, 0, 14) };
                    Action updateTotal = () =>
                    {
                        var picked = _closeScan.Where(a => { bool on; return closePick.TryGetValue(a.Process, out on) && on; }).ToList();
                        double mb = picked.Where(a => a.Open).Sum(a => a.RamMB);
                        total.Text = picked.Count == 0 ? "Tick the apps you don't need while you race."
                            : picked.Count + (picked.Count == 1 ? " app" : " apps") + " ticked" + (mb >= 50 ? ", freeing about " + MemText(mb) + " of memory" : "") + ".";
                    };
                    var knownBoxes = new List<CheckBox>();
                    Func<AppSuggestion, bool, UIElement> row = (a, showMem) =>
                    {
                        var name = a.Process; bool on; closePick.TryGetValue(name, out on); CheckBox cb;
                        var el = SetupFlow.CheckRow(a.Label, SuggestionWhy(a), showMem ? MemText(a.RamMB) : null, on, out cb);
                        cb.Checked += (s, e) => { closePick[name] = true; updateTotal(); };
                        cb.Unchecked += (s, e) => { closePick[name] = false; updateTotal(); };
                        if (a.Known && a.Open && !IsBrowserApp(a)) knownBoxes.Add(cb);   // Tick All leaves browsers alone
                        return el;
                    };
                    var parts = new List<UIElement>();
                    if (known.Count > 0)
                    {
                        var rows = known.Select(a => row(a, true)).ToArray();
                        parts.Add(SetupFlow.HeadingWithAction("Open now", known.Count > 1 ? "Tick All" : null, () => { foreach (var cb in knownBoxes) cb.IsChecked = true; }));
                        parts.Add(SetupFlow.Group(rows));
                    }
                    if (other.Count > 0)
                    {
                        parts.Add(SetupFlow.Heading("Other heavy apps that are open"));
                        parts.Add(SetupFlow.Group(other.Select(a => row(a, true)).ToArray()));
                    }
                    if (known.Count == 0 && other.Count == 0)
                        parts.Add(SetupFlow.Group(SetupFlow.Text("Nothing heavy is open right now", "You can add apps any time under Close.")));
                    parts.Add(total);
                    if (notOpen.Count > 0)
                        parts.Add(SetupFlow.Fold(notOpen.Count + (notOpen.Count == 1 ? " more app" : " more apps") + " on this PC, not open now",
                            "Tick any you might open before a race.", foldOpen, v => foldOpen = v, notOpen.Select(a => row(a, false)).ToArray()));
                    if (_close.Count > 0) parts.Add(SetupFlow.Group(SetupFlow.Text("Already on your Close list", string.Join(", ", _close.Select(FriendlyApp).ToArray()))));
                    updateTotal();
                    return SetupFlow.Stack(parts.ToArray());
                },
                OnNext = () =>
                {
                    foreach (var kv in closePick.Where(k => k.Value))
                        foreach (var n in NamesFor(kv.Key))
                            if (!_close.Contains(n, StringComparer.OrdinalIgnoreCase) && !ProtectedFromClose(n)) _close.Add(n);
                    SaveConfig();
                    return true;
                }
            });

            // 4. helpers: HWiNFO (CPU temperature) and OBS (streaming)
            pages.Add(new SetupPage
            {
                Title = "Helpers",
                Intro = "These are optional. Race Mode works without them, but they let it show more.",
                Build = BuildHelpersPage,
                OnLeave = () => { if (_helperTimer != null) _helperTimer.Stop(); }
            });

            // 5. done: end on the one action that matters
            pages.Add(new SetupPage
            {
                Title = "You're set",
                Intro = "Turn Race Mode on when you're about to race. Your Race graphics start as a copy of your current iRacing graphics; change the quality any time under Graphics.",
                NextText = "Done", CanSkip = false, QuietNext = true,
                Build = () =>
                {
                    bool screen = _cfg.DisplayGuard != null && _cfg.DisplayGuard.Enabled;
                    int starts = _launch.Count(l => l.Enabled), closes = _close.Count;
                    return SetupFlow.Stack(SetupFlow.Group(
                        SetupFlow.Status(screen ? "Screen check is on" : "Screen check is off", "Settings, Racing monitor", screen ? (bool?)true : null),
                        SetupFlow.Status(starts == 0 ? "No apps start with Race Mode" : starts + (starts == 1 ? " app starts" : " apps start") + " with Race Mode", "Launch apps", starts > 0 ? (bool?)true : null),
                        SetupFlow.Status(closes == 0 ? "No apps close before a race" : closes + (closes == 1 ? " app closes" : " apps close") + " before a race", "Close list", closes > 0 ? (bool?)true : null),
                        SetupFlow.Status(Installer.StartsWithWindows ? "Starts with Windows" : "Starts from its shortcut", "Settings, Windows", null)),
                        SetupFlow.Button("Turn It On Now", () => { _firstRun.Finish(); var sw = Get<ToggleButton>("Switch"); if (sw != null && sw.IsChecked != true) sw.IsChecked = true; }, true));
                }
            });

            _firstRun = new SetupFlow(Window, pages, FirstRunFinished);
            if (startAt >= 0) _firstRun.Start(startAt);
        }

        // After setup: point at the switch (keyboard focus shows its ring) and say what to do next.
        void FirstRunFinished()
        {
            if (_cfg.SetupDone != true) { _cfg.SetupDone = true; SaveConfig(); }
            // setup covered this version's features: What's new waits for the next stable series
            var series = VersionSeries(Updater.Current);
            if (series != null && (string.IsNullOrEmpty(_cfg.WhatsNewSeen) || Updater.Compare(_cfg.WhatsNewSeen + ".0", series + ".0") < 0)) { _cfg.WhatsNewSeen = series; SaveConfig(); }
            ApplyLayout();   // the window may have been resized while setup hid everything
            if (_on) UpdateStatus();
            else SetText("StatusText", "All set. Flip the switch when you're about to race.");
            Tick("Setup done");
            var sw = Get<ToggleButton>("Switch");
            if (sw != null) Window.Dispatcher.BeginInvoke(new Action(() => sw.Focus()), DispatcherPriority.Input);
        }

        // Most pixels, then the fastest refresh rate: usually the screen people race on (a fast 1080p side screen
        // loses to the 1440p or ultrawide one in front).
        int SuggestRacingMonitor()
        {
            int best = 0;
            for (int k = 1; k < _pickMonitors.Count; k++)
            {
                var a = _pickMonitors[k]; var b = _pickMonitors[best];
                long pa = (long)a.Width * a.Height, pb = (long)b.Width * b.Height;
                if (pa > pb || (pa == pb && a.Hz > b.Hz)) best = k;
            }
            return best;
        }

        static bool IsBrowser(string n) { return new[] { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi" }.Contains(n, StringComparer.OrdinalIgnoreCase); }

        // Friendly names for common process names (the Close list stores what Task Manager calls them).
        static readonly Dictionary<string, string> Friendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "chrome", "Google Chrome" }, { "msedge", "Microsoft Edge" }, { "firefox", "Firefox" }, { "opera", "Opera" }, { "brave", "Brave" }, { "vivaldi", "Vivaldi" },
            { "steam", "Steam" }, { "steamwebhelper", "Steam (web helper)" }, { "EpicGamesLauncher", "Epic Games Launcher" }, { "Battle.net", "Battle.net" },
            { "EADesktop", "EA app" }, { "upc", "Ubisoft Connect" }, { "GalaxyClient", "GOG Galaxy" }, { "Spotify", "Spotify" }, { "OneDrive", "OneDrive" },
            { "Dropbox", "Dropbox" }, { "Teams", "Microsoft Teams" }, { "ms-teams", "Microsoft Teams" }, { "slack", "Slack" }, { "WhatsApp", "WhatsApp" },
            { "Discord", "Discord" }, { "obs64", "OBS Studio" } };
        public static string FriendlyApp(string process) { string f; if (process != null && Friendly.TryGetValue(process, out f)) return f; return AppScan.LabelFor(process) ?? process; }

        static UIElement Pad(FrameworkElement el) { el.Margin = new Thickness(0, 10, 0, 0); return el; }

        UIElement BuildHelpersPage()
        {
            var hw = new StackPanel();
            var obs = new StackPanel();
            Action render = null;
            render = () =>
            {
                RenderHwinfo(hw, render);
                obs.Children.Clear();
                var cb = SetupFlow.Switch("I stream", "Turns on Streaming: OBS starts with Race Mode and gets a little more CPU.", _cfg.Streaming);
                cb.Checked += (s, e) => { Get<CheckBox>("OptStream").IsChecked = true; };
                cb.Unchecked += (s, e) => { Get<CheckBox>("OptStream").IsChecked = false; };
                obs.Children.Add(cb);
                var o = _cfg.Streaming ? Obs.SetupStatus() : null;   // OBS details only matter to people who stream
                if (o != null) obs.Children.Add(Pad((FrameworkElement)o(this)));
            };
            render();
            if (_helperTimer == null) { _helperTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; }
            _helperTimer.Tick -= HelperTick; _helperTimer.Tick += HelperTick;
            _helperRender = render;
            _helperTimer.Start();
            return SetupFlow.Stack(SetupFlow.Group(hw), SetupFlow.Group(obs));
        }

        // HWiNFO's state and the one button that moves it on. Shared by setup's Helpers page and the Settings tab
        // (CPU temperature), so fixing it never means going back into setup. rerender: called after a button press
        // (null: the caller's timer refreshes it).
        int _hwStage;          // 0 nothing running, 1 waiting for the user to quit HWiNFO, 2 started with sharing on
        DateTime _hwStageAt;
        void RenderHwinfo(StackPanel hw, Action rerender)
        {
            if (hw == null) return;
            Action again = () => { if (rerender != null) rerender(); else RenderHwinfo(hw, null); };
            hw.Children.Clear();
            bool installed = File.Exists(HwinfoExe);
            var live = _mon.Live; double temp = live == null ? double.NaN : live.CpuTemp;   // NaN until HWiNFO shares a CPU temperature
            var hps = Process.GetProcessesByName("HWiNFO64"); bool hwRunning = hps.Length > 0; foreach (var hp in hps) hp.Dispose();
            if (!double.IsNaN(temp))
            {
                _hwStage = 0;
                hw.Children.Add(SetupFlow.Status("CPU temperature works", "HWiNFO is sharing it: " + Math.Round(Monitor.HwinfoFahrenheit ? temp * 9 / 5 + 32 : temp) + (Monitor.HwinfoFahrenheit ? " °F" : " °C") + " right now.", true));
                return;
            }
            if (!installed)
            {
                if (_helperError != null) hw.Children.Add(SetupFlow.Status("HWiNFO didn't install", _helperError, false));
                hw.Children.Add(SetupFlow.Status("CPU temperature needs HWiNFO", "HWiNFO is a free sensor app. Race Mode reads the CPU temperature it shares.", null));
                hw.Children.Add(SetupFlow.Button(_helperBusy ? "Installing HWiNFO…" : "Install HWiNFO", InstallHwinfo, true));
                return;
            }
            if (_hwStage == 1 && !hwRunning) SetUpAndStartHwinfo();   // the user quit it: switch sharing on, start it again
            if (_hwStage == 1)
            {
                hw.Children.Add(SetupFlow.Status("Quit HWiNFO so Race Mode can switch on its sharing",
                    "Right-click HWiNFO's icon in the system tray (next to the clock) and choose Exit. Race Mode carries on by itself once it has closed.", null));
                hw.Children.Add(SetupFlow.Button("Cancel", () => { _hwStage = 0; again(); }));
                return;
            }
            if (_hwStage == 2 && (DateTime.Now - _hwStageAt).TotalSeconds < 30)
            {
                hw.Children.Add(SetupFlow.Status("HWiNFO is starting with sharing switched on…",
                    "Allow it if Windows asks for permission. If HWiNFO asks how to start, choose Sensors-only. This turns green by itself.", null));
                return;
            }
            if (!hwRunning)
            {
                bool on = HwinfoSetup.SharingOn();
                hw.Children.Add(SetupFlow.Status("HWiNFO is installed but not running",
                    on ? "Open it, and Race Mode reads the CPU temperature it shares." : "Race Mode can switch on its sharing and open it for you.", false));
                hw.Children.Add(SetupFlow.Button(on ? "Open HWiNFO" : "Set Up and Open HWiNFO", () => { SetUpAndStartHwinfo(); again(); }, true));
                return;
            }
            bool afterSetup = _hwStage == 2;
            hw.Children.Add(SetupFlow.Status(afterSetup ? "One step left in HWiNFO" : "HWiNFO is running but isn't sharing the CPU temperature yet",
                afterSetup ? "Sharing is on, but HWiNFO didn't pick a CPU temperature for this processor. Do the step below; this turns green by itself."
                           : "Race Mode can switch its sharing on for you. HWiNFO closes for a moment while it does.", false));
            if (!afterSetup) hw.Children.Add(SetupFlow.Button("Set Up HWiNFO for Me", () => { _hwStage = 1; Tick("Quit HWiNFO, and Race Mode switches on its sharing"); again(); }, true));
            var steps = (FrameworkElement)SetupFlow.Text("In HWiNFO", afterSetup
                ? "In the sensor window, right-click your CPU's package temperature (CPU Package, or CPU (Tctl/Tdie) on AMD) and choose Report value in Gadget."
                : "1. Choose Sensors-only when it opens.\n2. Settings > HWiNFO Gadget: tick Enable reporting to Gadget.\n3. Right-click CPU Package in the sensor list > Report value in Gadget.\nTip: in HWiNFO's settings, tick Auto Start so it's always running.");
            steps.Margin = new Thickness(0, 10, 0, 0);
            steps.Visibility = _helperStepsShown || afterSetup ? Visibility.Visible : Visibility.Collapsed;
            if (!afterSetup) hw.Children.Add(SetupFlow.Button(_helperStepsShown ? "Hide Steps" : "Do It Myself", () => { _helperStepsShown = !_helperStepsShown; again(); }));
            hw.Children.Add(steps);
        }

        // HWiNFO is closed: switch its sharing on (its own per-user settings), then start it (Windows asks first).
        void SetUpAndStartHwinfo()
        {
            try
            {
                int n = HwinfoSetup.Apply();
                _mon.Event("Switched on HWiNFO's sharing (" + n + " CPU readings added)");
                Installer.Log("hwinfo", new[] { "sharing switched on, " + n + " CPU readings added" }, Shell.Data);
            }
            catch (Exception ex) { LogCrash(ex); Tick("Couldn't change HWiNFO's settings: " + ex.Message); _hwStage = 0; return; }
            _hwStage = 2; _hwStageAt = DateTime.Now;
            try { Process.Start(new ProcessStartInfo(HwinfoExe) { UseShellExecute = true }); _hwinfoCheckedAt = DateTime.MinValue; Tick("Starting HWiNFO with sharing switched on…"); }
            catch (Exception ex) { Tick("Couldn't start HWiNFO: " + ex.Message); }
        }

        Action _helperRender;
        bool _helperBusy, _helperStepsShown;
        string _helperError;   // why the HWiNFO install failed, shown on the Helpers page
        void HelperTick(object s, EventArgs e) { if (_helperRender != null && !_helperBusy) _helperRender(); }

        // "Ask, then install": the button press is the yes. winget shows Windows' own admin prompt.
        void InstallHwinfo()
        {
            if (_helperBusy) return;
            _helperBusy = true; if (_helperRender != null) _helperRender();
            Tick("Installing HWiNFO with Windows' app installer (winget)…");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string msg;
                try
                {
                    var psi = new ProcessStartInfo("winget", "install --id REALiX.HWiNFO -e --silent --accept-package-agreements --accept-source-agreements")
                    { UseShellExecute = false, CreateNoWindow = true };
                    using (var p = Process.Start(psi)) { p.WaitForExit(10 * 60 * 1000); msg = File.Exists(HwinfoExe) ? "HWiNFO is installed. Now switch on its sharing (steps below)." : "HWiNFO didn't install (winget code " + (p.HasExited ? p.ExitCode.ToString() : "timeout") + "). You can get it from hwinfo.com."; }
                }
                catch (Exception ex) { msg = "Couldn't run winget (" + ex.Message + "). You can get HWiNFO from hwinfo.com."; }
                Window.Dispatcher.BeginInvoke(new Action(() => { _helperBusy = false; _helperError = File.Exists(HwinfoExe) ? null : msg; Tick(msg); Installer.Log("hwinfo", new[] { msg }, Shell.Data); if (_helperRender != null) _helperRender(); }));
            });
        }
    }
}
