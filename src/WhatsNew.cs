using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace RaceMode
{
    // "What's new": a short walk through a release's new features, in the same overlay as first-run setup, with
    // each feature's setting right on its page. Shown once, on the first start after updating to any version
    // (test versions too) whose series ("1.1") has pages here; never after first-run setup (that already covers
    // everything) and never for local builds. Settings > Updates opens it again.
    public partial class Shell
    {
        SetupFlow _whatsNew;
        string _whatsNewSeries;

        // Series that have pages, oldest first. Add one per stable release that has something worth showing.
        public static readonly string[] WhatsNewSeriesWithPages = { "1.1" };

        // "1.1.0-rc.3" -> "1.1"; null for local builds and anything that isn't a version.
        public static string VersionSeries(string version)
        {
            var p = Updater.Parse(version);
            return p == null ? null : p[0] + "." + p[1];
        }

        // Whether to open What's new on this start: the first start after the version went up (an update, test
        // versions too). lastRun: the version that ran before this start (empty in configs from before 1.1, which
        // only an update can bring here). Never after first-run setup, on a local build, or after going back.
        public static bool ShouldShowWhatsNew(string current, string lastRun, bool firstRun) { return ShouldShowWhatsNew(current, lastRun, firstRun, null); }

        // seen: the series it was last shown for (or set up on). A normal patch within a series already seen
        // (1.1.0 -> 1.1.1) doesn't repeat the same pages; test versions, and the step from a test version to the
        // release, always show them.
        public static bool ShouldShowWhatsNew(string current, string lastRun, bool firstRun, string seen)
        {
            if (firstRun) return false;
            var p = Updater.Parse(current);
            if (p == null) return false;                                         // a local build
            var series = VersionSeries(current);
            if (!WhatsNewSeriesWithPages.Contains(series)) return false;
            if (!string.IsNullOrEmpty(lastRun) && (Updater.Parse(lastRun) == null || Updater.Compare(lastRun, current) >= 0)) return false;
            bool testNow = p[3] != long.MaxValue, testBefore = !string.IsNullOrEmpty(lastRun) && Updater.Parse(lastRun)[3] != long.MaxValue;
            if (testNow || testBefore) return true;
            return string.IsNullOrEmpty(seen) || Updater.Compare(seen + ".0", series + ".0") < 0;
        }

        void MaybeStartWhatsNew()
        {
            var args = Environment.GetCommandLineArgs();
            bool forced = args.Length >= 2 && args[1] == "--whatsnew";
            bool firstRun = _freshConfig || _cfg.SetupDone == false;
            string lastRun = _cfg.LastRunVersion;
            bool show = forced || (!_cfgBroken && ShouldShowWhatsNew(Updater.Current, lastRun, firstRun, _cfg.WhatsNewSeen));
            // this version has run: saved now when nothing will show, but only once the card actually opens when it
            // will (a start to the tray that never opens the window keeps it for the next start)
            if (!show) { MarkVersionRun(); return; }
            // after the window is up. A start with Windows loads the window and hides it to the tray straight away, so
            // wait until it is really on screen (it only counts as shown once it is: MarkVersionRun in StartWhatsNew).
            Window.Loaded += (s, e) => Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Window.IsVisible) { StartWhatsNew(); return; }
                DependencyPropertyChangedEventHandler shown = null;
                shown = (s2, e2) =>
                {
                    if (!Window.IsVisible) return;
                    Window.IsVisibleChanged -= shown;
                    Window.Dispatcher.BeginInvoke(new Action(() => StartWhatsNew()), DispatcherPriority.Background);
                };
                Window.IsVisibleChanged += shown;
            }), DispatcherPriority.Background);
        }

        IInputElement _focusBeforeWhatsNew;

        void MarkVersionRun()
        {
            if (!_cfgBroken && Updater.Parse(Updater.Current) != null && _cfg.LastRunVersion != Updater.Current) { _cfg.LastRunVersion = Updater.Current; SaveConfig(); }
        }

        public void StartWhatsNew()
        {
            if (SetupFlow.AnyShowing) return;   // setup is up: never stack one on the other
            MarkVersionRun();
            // the card needs room (the small panel has none, and hides its own way back)
            if (_small) ToggleSmall();
            _focusBeforeWhatsNew = Keyboard.FocusedElement;
            if (_whatsNew == null)
            {
                _whatsNewSeries = VersionSeries(Updater.Current);
                if (!WhatsNewSeriesWithPages.Contains(_whatsNewSeries)) _whatsNewSeries = WhatsNewSeriesWithPages.Last();   // local builds: the newest pages
                var pages = WhatsNewPages(_whatsNewSeries);
                if (pages.Count == 0) return;
                string series = _whatsNewSeries;
                _whatsNew = new SetupFlow(Window, pages, WhatsNewFinished)
                {
                    StepText = (i, n) => "What's new in " + series + " · " + (i + 1) + " of " + n,
                    SkipEnds = true,
                    Modal = true,   // a card over the dimmed app, not a page of its own
                };
            }
            _whatsNew.Start(0);
        }

        void WhatsNewFinished()
        {
            var series = _whatsNewSeries;
            if (!string.IsNullOrEmpty(series) && (string.IsNullOrEmpty(_cfg.WhatsNewSeen) || Updater.Compare(_cfg.WhatsNewSeen + ".0", series + ".0") < 0))
            {
                _cfg.WhatsNewSeen = series; SaveConfig();
            }
            ApplyLayout();   // the window may have been resized while the overlay hid everything
            // back where the keyboard was (Show Me in Settings); on start-up, the switch
            var back = _focusBeforeWhatsNew as UIElement;
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (back != null && back != Window && back.IsVisible && back.Focus()) return;
                var sw = Get<ToggleButton>("Switch");
                if (sw != null && sw.IsVisible) sw.Focus();
                else { var nav = Get<ListBox>("Nav"); if (nav != null) nav.Focus(); }
            }), DispatcherPriority.Input);
        }

        List<SetupPage> WhatsNewPages(string series)
        {
            var pages = new List<SetupPage>();
            if (series != "1.1") return pages;

            // 1. the screen picture and three screens
            pages.Add(new SetupPage
            {
                Title = "Your screens, as Windows sees them",
                Intro = "Pick the screen you race on from a picture of your desk. Racing on three screens? Tick the box and iRacing opens across all three as one window. Changes are saved as you make them.",
                Build = () =>
                {
                    FillMonitorPick();
                    var box = Get<ComboBox>("MonitorPick");
                    return SetupFlow.Group(BuildScreenPicker(() => box.SelectedIndex, i => { if (box.SelectedIndex != i) box.SelectedIndex = i; }, null));
                }
            });

            // 2. race apps found on this PC
            pages.Add(new SetupPage
            {
                Title = "Race apps found on your PC",
                Intro = "Race Mode now looks through your Start menu and installed apps for race apps like SimHub and Crew Chief. Add the ones you want started before a race.",
                Build = () =>
                {
                    if (_launchFound == null)
                    {
                        StartLaunchScan();
                        return SetupFlow.Group(SetupFlow.Text("Looking for race apps on this PC…", "This takes a few seconds."));
                    }
                    var found = LaunchSuggestions();
                    if (found.Count == 0)
                        return SetupFlow.Group(SetupFlow.Status("Nothing new to add", _launch.Count > 0
                            ? "Every race app Race Mode found is already on your Launch list."
                            : "Race Mode didn't find any race apps it knows. You can add any app on the Launch page.", true));
                    return SetupFlow.Stack(
                        SetupFlow.HeadingWithAction("Found on your PC", found.Count > 1 ? "Add All" : null,
                            () => { foreach (var f in LaunchSuggestions()) AddFoundLaunch(f, false); _whatsNew.Refresh(); }),
                        SetupFlow.Group(found.Select(f => { var item = f; return SuggestRow(item, () => { AddFoundLaunch(item, true); _whatsNew.Refresh(); }); }).ToArray()));
                }
            });

            // 3. the new look: side menu, small panel, light or dark
            pages.Add(new SetupPage
            {
                Title = "A new look",
                Intro = "Sections now sit in a menu on the left, and Race Mode can be light or dark.",
                NextText = "Done",
                CanSkip = false,   // Done already ends it
                Build = () =>
                {
                    var seg = new StackPanel { Orientation = Orientation.Horizontal };
                    AutomationProperties.SetName(seg, "Appearance");
                    var names = new[] { "System", "Dark", "Light" };
                    var cur = Palette.Normalize(_cfg.Theme);
                    foreach (var n in names)
                    {
                        var name = n;
                        var rb = new RadioButton { Style = SetupFlow.Res<Style>("Seg"), GroupName = "WhatsNewTheme", Content = name, IsChecked = name == cur };
                        AutomationProperties.SetName(rb, "Appearance: " + name);
                        // the Settings choice does the work (saves and repaints), so both always agree
                        rb.Checked += (s, e) => { var real = Get<RadioButton>("Theme" + name); if (real != null && real.IsChecked != true) real.IsChecked = true; };
                        seg.Children.Add(rb);
                    }
                    var track = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = seg, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
                    track.SetResourceReference(Border.BackgroundProperty, "Field");
                    return SetupFlow.Group(
                        SetupFlow.Stack(SetupFlow.Text("Appearance", "System follows the light or dark mode set in Windows, and changes with it."), track),
                        SetupFlow.Text("The menu on the left", "Every section is one click away, with what each one needs at a glance. The arrow at the bottom folds it to icons."),
                        SetupFlow.Text("Small panel", "The button in the title bar shrinks Race Mode to the switch, FPS and the live numbers, for a corner of a side screen."));
                }
            });
            return pages;
        }
    }
}
