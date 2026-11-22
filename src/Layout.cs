using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RaceMode
{
    // How the window is laid out, and how it fits whatever size it is given.
    //
    // A side menu picks the section. The first section, Overview, is the switch, the live numbers and the
    // dashboard (charts, checklist, last session); the others are the lists and options, each with the whole
    // height to itself. The menu drives the same TabControl as before, so the sections' content, Ctrl+number
    // and screen readers work as they did.
    //
    // Sizes, smallest first:
    //   small panel   under 560 wide or 460 tall: only the switch and the live numbers (no menu, no activity line)
    //   icon menu     under 760 wide: the menu is icons only, their names are tooltips
    //   full menu     icons and names
    //   wide          from 1280 wide: the dashboard also shows beside whichever section is open
    public partial class Shell
    {
        const double SmallW = 500, SmallH = 460;
        const double NavNamesAt = 760;
        const double WideAt = 1440;
        const double NavWide = 208, NavNarrow = 60;
        const double SectionCol = 764;        // a list section's column beside the dashboard: its 720 of content and its margins
        const double PanelW = 460, PanelH = 262;   // what the Small panel button shrinks to

        bool _small, _dashOn, _compact, _layoutReady;
        int _sectionBeforeSmall = -1;

        // The icons (one family: 20 x 20, 1.6 px stroke, round caps and joins) and the rows are built in SideMenu.cs.
        // The order is the sections' order (Ctrl+1..7) and never changes; groups are made by spacing only.

        void BuildSideNav()
        {
            var nav = Get<ListBox>("Nav"); var tabs = Get<TabControl>("Tabs");
            if (nav == null || tabs == null) return;
            BuildNavRows(nav);
            nav.SelectedIndex = tabs.SelectedIndex;
            nav.SelectionChanged += (s, e) => { if (nav.SelectedIndex >= 0 && tabs.SelectedIndex != nav.SelectedIndex) tabs.SelectedIndex = nav.SelectedIndex; };
            // e.Source: lists and dropdowns inside a section raise the same event on their way up
            tabs.SelectionChanged += (s, e) =>
            {
                if (e.Source != tabs) return;
                if (_small && tabs.SelectedIndex != 0) { tabs.SelectedIndex = 0; return; }   // the small panel has no sections
                if (nav.SelectedIndex != tabs.SelectedIndex) nav.SelectedIndex = tabs.SelectedIndex;
                var open = tabs.SelectedItem as TabItem;
                if (open != null) AutomationProperties.SetName(tabs, (open.Tag ?? open.Header) as string ?? "");
                // Screen readers: the hidden tab strip never lays out, so Windows was never told that the open
                // section's content changed. Coming back to a section left its page invisible to them (found by
                // driving the real window through UI Automation). Tell it once the new page is in place.
                Window.Dispatcher.BeginInvoke(new Action(RefreshAutomationTree), System.Windows.Threading.DispatcherPriority.Loaded);
                ApplyLayout();
            };
            var small = Get<Button>("SmallBtn"); if (small != null) small.Click += (s, e) => ToggleSmall();
            NameControls();
            NavPhase(_phase ?? "off");
            Window.Loaded += (s, e) => { _layoutReady = true; ApplyLayout(); };
        }

        // Screen readers: a switch row's label is two text blocks, not a string, so the switch had no name. Its title
        // becomes the name and its description the help text. The app lists get names too.
        void NameControls()
        {
            var tabs = Get<TabControl>("Tabs"); if (tabs == null) return;
            var stack = new System.Collections.Generic.Stack<DependencyObject>(); stack.Push(tabs);
            while (stack.Count > 0)
            {
                var o = stack.Pop();
                var box = o as CheckBox;
                if (box != null && string.IsNullOrEmpty(AutomationProperties.GetName(box)))
                {
                    var panel = box.Content as Panel;
                    var lines = panel == null ? null : panel.Children.OfType<TextBlock>().ToList();
                    if (lines != null && lines.Count > 0)
                    {
                        AutomationProperties.SetName(box, lines[0].Text);
                        if (lines.Count > 1 && string.IsNullOrEmpty(AutomationProperties.GetHelpText(box))) AutomationProperties.SetHelpText(box, lines[1].Text);
                    }
                }
                foreach (var child in LogicalTreeHelper.GetChildren(o)) { var d = child as DependencyObject; if (d != null) stack.Push(d); }
            }
            var names = new[] { "LaunchList", "Apps that start with Race Mode", "CloseList", "Apps that are closed", "StreamList", "Streaming apps", "RunningList", "Apps that are open now" };
            for (int i = 0; i < names.Length; i += 2)
            { var list = Get<ListBox>(names[i]); if (list != null && string.IsNullOrEmpty(AutomationProperties.GetName(list))) AutomationProperties.SetName(list, names[i + 1]); }
        }

        void RefreshAutomationTree()
        {
            var tabs = Get<TabControl>("Tabs"); if (tabs == null) return;
            // FromElement: only when something is listening (a screen reader, UI Automation); otherwise there is no tree to refresh
            var top = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(tabs);
            if (top == null) return;
            top.ResetChildrenCache();
            // each section's entry keeps its own list of what is on its page: the one just left must drop its
            // page, and the one just opened must pick its page up
            var sections = top.GetChildren();
            if (sections != null) foreach (var section in sections) section.ResetChildrenCache();
            top.InvalidatePeer();
        }

        // Ctrl+1..7 and anything else that wants a section by number. The small panel has no sections.
        void GoToSection(int n)
        {
            var tabs = Get<TabControl>("Tabs");
            if (tabs == null || n < 0 || n >= tabs.Items.Count) return;
            if (_small) { _sectionBeforeSmall = n; ToggleSmall(); return; }
            tabs.SelectedIndex = n;
        }

        // The menu's own copy of the status: a dot and a word, next to the small switch.
        void NavPhase(string phase)
        {
            SmallPhase(phase);
            var t = Get<TextBlock>("NavState"); var dot = Get<Shape>("NavDot");
            if (t == null || dot == null) return;
            string word = phase == "off" ? "Off" : phase == "turning" ? "Turning on…" : phase == "issues" ? "Needs a look" : "Ready to race";
            t.Text = word;
            t.Foreground = (Brush)Window.Resources[phase == "issues" ? "Warn" : "Dim"];
            dot.Fill = phase == "off" ? new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x60)) : (Brush)Window.Resources[phase == "turning" ? "Dim" : phase == "issues" ? "Warn" : "Live"];
            // what needs a look is spelled out in the tooltip, and the words open the Overview, where the checklist is
            string detail = phase == "issues" && _issues.Count > 0 ? NeedsALook() + ". Click to see the checklist." : "Race Mode: " + word.ToLowerInvariant() + ".";
            var sw = Get<ToggleButton>("NavSwitch");
            if (sw != null) sw.ToolTip = detail.Replace(". Click to see the checklist.", ".") + " Click to turn it " + (phase == "off" ? "on" : "off") + ".";
            var words = Get<FrameworkElement>("NavStatusWords");
            if (words != null) { words.ToolTip = detail; words.Cursor = phase == "issues" ? System.Windows.Input.Cursors.Hand : null; }
            RefreshNav();
        }

        void ApplyLayout()
        {
            double w = Window.ActualWidth, h = Window.ActualHeight;
            var tabs = Get<TabControl>("Tabs"); var rail = Get<FrameworkElement>("NavRail");
            if (!_layoutReady || tabs == null || rail == null || w <= 0 || h <= 0) return;
            var bodyEl = Get<UIElement>("Body");
            bool hadFocus = bodyEl != null && bodyEl.IsKeyboardFocusWithin;

            // ---- which size is this ----
            bool small = w < SmallW || h < SmallH;
            var shape = small ? ShapeFor(w, h) : SmallShape.None;
            if (small != _small)
            {
                _small = small;
                if (small) { _sectionBeforeSmall = tabs.SelectedIndex; tabs.SelectedIndex = 0; }
                else if (_sectionBeforeSmall >= 0) { tabs.SelectedIndex = _sectionBeforeSmall; _sectionBeforeSmall = -1; }
                var sb = Get<Button>("SmallBtn");
                if (sb != null)
                {
                    sb.ToolTip = small ? "Back to the full window" : "Small panel: the switch, FPS and the live numbers";
                    AutomationProperties.SetName(sb, small ? "Full window" : "Small panel");
                }
            }
            if (!small && Window.WindowState == WindowState.Normal) { _cfg.FullWidth = Math.Round(w); _cfg.FullHeight = Math.Round(h); }
            bool overview = tabs.SelectedIndex == 0;
            bool names = w >= NavNamesAt;
            double navW = small ? 0 : names ? NavWide : NavNarrow;
            // beside the dashboard only for the list sections: Settings and Logs use a wide page themselves (Pages.cs)
            int section = tabs.SelectedIndex;
            bool wide = !small && section >= 1 && section <= 4 && w >= WideAt;

            // ---- side menu ----
            rail.Visibility = small ? Visibility.Collapsed : Visibility.Visible;
            if (!small)
            {
                rail.Width = navW;
                LayoutNav(names);
                // the switch is always one click away: under the menu, unless the Overview's big one is on screen
                var status = Get<Border>("NavStatus");
                if (status != null) status.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
            }

            // ---- columns: section, and the dashboard beside it when wide ----
            double edge = small ? 12 : 20;
            tabs.Margin = new Thickness(edge, small ? 4 : 6, edge, 0);
            Get<ColumnDefinition>("MainCol").Width = wide ? new GridLength(SectionCol) : new GridLength(1, GridUnitType.Star);
            Get<ColumnDefinition>("DashCol").Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Get<ScrollViewer>("Dash").Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            var footer = Get<FrameworkElement>("FooterHost"); if (footer != null) footer.Visibility = small ? Visibility.Collapsed : Visibility.Visible;
            var bodyBox = Get<FrameworkElement>("Body"); if (bodyBox != null) bodyBox.Margin = new Thickness(0, 0, 0, small ? 8 : 12);

            double body = w - 2 - (small ? 0 : 12 + navW);            // everything right of the menu
            double page = (wide ? SectionCol : body) - 2 * edge - 4;   // the open section's own width
            double dashW = overview ? Math.Min(page, OverMax) : wide ? body - SectionCol - 24 : 0;

            // ---- overview: the small panel is the switch and the numbers strip; the full page is composed by
            //      moment (Overview.cs), and its live numbers are the charts, so the strip isn't shown twice ----
            var stats = Get<Border>("StatsStrip");
            if (stats != null && !small) stats.Visibility = Visibility.Collapsed;   // in the small panel its shape decides (SmallPanel.cs)
            int colCharts = shape == SmallShape.Column ? ColumnCharts(h) : 0;   // a tall column also has room for a chart or two
            var here = Get<FrameworkElement>("DashHere"); if (here != null) here.Visibility = !small || colCharts > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (shape != _shape) _densitySet = false;     // a new shape starts from the plain density, then SmallPanel.cs shapes it
            ApplyDensity(small || h < 700, small || page < 520);
            // order matters: leaving a shape puts things back first, then the page is composed; a shape is applied last
            if (shape == SmallShape.None) ApplySmallShape(shape, w, h, 0);
            ArrangeOverview(overview, small, page);
            if (shape != SmallShape.None) ApplySmallShape(shape, w, h, colCharts);
            // the switch card's own two-column proof gave way to the full checklist beside it; only the column still uses it
            var checks = Get<FrameworkElement>("HeroChecks"); if (checks != null) checks.MaxHeight = shape == SmallShape.Column ? double.PositiveInfinity : 0;

            // ---- dashboard: on the Overview, or beside a section; drawn only while it's on screen ----
            bool dashOn = (!small && (overview || wide)) || colCharts > 0;
            if (dashOn)
            {
                HostDash(Get<Border>(overview ? "DashHere" : "DashSide"));
                var bg = Get<UniformGrid>("DashBottom"); if (bg != null) bg.Columns = dashW >= 700 && bg.Children.Count > 1 ? 2 : 1;   // one block alone takes the row
                var hint = Get<FrameworkElement>("DashHint"); if (hint != null) hint.Visibility = dashW >= 620 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (dashOn && (!_dashOn || !_chartsBuilt))
            {
                _dashOn = true;
                BuildDashboard(); UpdateChecklist(); ShowLastSession(); RenderDashboard();
            }
            _dashOn = dashOn;
            if (dashOn) ArrangeLive(overview, small ? w - 28 : dashW, h);
            FitCharts(shape == SmallShape.Column ? colCharts : 6);
            ApplyPages(page);
            if (hadFocus) Window.Dispatcher.BeginInvoke(new Action(RestoreFocus), System.Windows.Threading.DispatcherPriority.Input);
        }

        // The layout may have hidden the control that had the keyboard (the menu, the switch under it, a section).
        // Put it somewhere sensible: the big switch in the small panel, the current menu row otherwise.
        void RestoreFocus()
        {
            var body = Get<UIElement>("Body");
            var now = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            if (body == null || (now != null && now.IsVisible && body.IsKeyboardFocusWithin)) return;
            var nav = Get<ListBox>("Nav");
            if (_small || nav == null) { var sw = Get<UIElement>("Switch"); if (sw != null && sw.IsVisible) sw.Focus(); return; }
            var row = nav.SelectedItem as UIElement; if (row != null && row.IsVisible) row.Focus();
        }

        // The dashboard is one piece that sits in one of two places.
        void HostDash(Border host)
        {
            var body = Get<StackPanel>("DashBody");
            if (body == null || host == null || body.Parent == host) return;
            var old = body.Parent as Border; if (old != null) old.Child = null;
            host.Child = body;
        }

        // compact: short windows (a 1080p laptop at 150% scaling has about 690 px) get a tighter switch card and
        // numbers strip. tight: narrow pages get smaller numerals and tile padding so five tiles still fit.
        // Nothing that carries meaning is hidden; only padding, size and the meter bars go.
        bool _tight; bool _densitySet;
        void ApplyDensity(bool compact, bool tight)
        {
            if (_densitySet && compact == _compact && tight == _tight) return;
            _densitySet = true; _compact = compact; _tight = tight;
            var hero = Get<Border>("Hero");
            if (hero != null) hero.Padding = compact ? new Thickness(16, 12, 18, 12) : new Thickness(16, 22, 22, 22);
            var head = Get<TextBlock>("Headline"); if (head != null) head.FontSize = compact ? 22 : 28;
            var tel = Get<UniformGrid>("Telemetry");
            if (tel != null)
            {
                var cells = tel.Children.OfType<Border>().Where(c => c.Visibility != Visibility.Collapsed).ToList();   // not the FPS tile, which only the small panel shows
                double v = compact ? 8 : 14;
                for (int i = 0; i < cells.Count; i++)
                    cells[i].Padding = tight ? new Thickness(10, v, 8, v) : new Thickness(i == 0 ? 16 : 14, v, i == cells.Count - 1 ? 14 : 12, v);
            }
            foreach (var n in new[] { "CpuVal", "CpuTempVal", "GpuVal", "GpuTempVal", "RamVal" })
            { var r = Get<Run>(n); if (r != null) r.FontSize = tight ? 22 : 28; }
            foreach (var n in new[] { "CpuBar", "CpuTempBar", "GpuBar", "GpuTempBar", "RamBar" })
            { var b = Get<ProgressBar>(n); if (b != null && b.Visibility != Visibility.Hidden) b.Visibility = compact ? Visibility.Collapsed : Visibility.Visible; }
        }

        // The Small panel button: down to the switch and the numbers, and back to the size the window had.
        void ToggleSmall()
        {
            if (Window.WindowState == WindowState.Maximized)
            {
                var rb = Window.RestoreBounds;
                if (rb.Width >= SmallW && rb.Height >= SmallH) { _cfg.FullWidth = Math.Round(rb.Width); _cfg.FullHeight = Math.Round(rb.Height); }
                Window.WindowState = WindowState.Normal;
            }
            var work = SystemParameters.WorkArea;
            if (!_small) { Window.Width = PanelW; Window.Height = PanelH; return; }
            double fw = _cfg.FullWidth >= SmallW ? _cfg.FullWidth : 960, fh = _cfg.FullHeight >= SmallH ? _cfg.FullHeight : 720;
            fw = Math.Min(fw, work.Width); fh = Math.Min(fh, work.Height - 16);
            Window.Width = Math.Max(fw, SmallW); Window.Height = Math.Max(fh, SmallH);
            // growing back must not push the window off the screens
            double right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth, bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (Window.Left + Window.Width > right) Window.Left = Math.Max(SystemParameters.VirtualScreenLeft, right - Window.Width);
            if (Window.Top + Window.Height > bottom) Window.Top = Math.Max(SystemParameters.VirtualScreenTop, bottom - Window.Height);
        }
    }
}
