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
        const double SmallW = 560, SmallH = 460;
        const double NavNamesAt = 760;
        const double WideAt = 1280;
        const double NavWide = 208, NavNarrow = 60;
        const double SectionCol = 620;        // a section's column beside the dashboard: 580 of content and its margins
        const double PanelW = 460, PanelH = 262;   // what the Small panel button shrinks to

        bool _small, _dashOn, _compact, _layoutReady;
        int _sectionBeforeSmall = -1;

        // 20 x 20 outline icons, drawn with a 1.6 px stroke. The order is the sections' order (Ctrl+1..7).
        static readonly string[][] NavItems = {
            new[] { "Overview", "M3,3 H9 V9 H3 Z M11,3 H17 V9 H11 Z M3,11 H9 V17 H3 Z M11,11 H17 V17 H11 Z" },
            new[] { "Launch",   "M7,5 L15,10 L7,15 Z" },
            new[] { "Close",    "M3,3 H17 V17 H3 Z M7,7 L13,13 M13,7 L7,13" },
            new[] { "Stream",   "M8.5,10 A1.5,1.5 0 1,0 11.5,10 A1.5,1.5 0 1,0 8.5,10 M6,6 A5.6,5.6 0 0,0 6,14 M14,6 A5.6,5.6 0 0,1 14,14 M3,3 A9.9,9.9 0 0,0 3,17 M17,3 A9.9,9.9 0 0,1 17,17" },
            new[] { "Graphics", "M2,4 H18 V16 H2 Z M2,13 L7,8 L11,12 L13,10 L18,15" },
            new[] { "Settings", "M3,5 H17 M3,10 H17 M3,15 H17 M7,3 V7 M13,8 V12 M9,13 V17" },
            new[] { "Logs",     "M5,2 H12 L16,6 V18 H5 Z M8,9 H13 M8,12 H13 M8,15 H11" },
        };

        void BuildSideNav()
        {
            var nav = Get<ListBox>("Nav"); var tabs = Get<TabControl>("Tabs");
            if (nav == null || tabs == null) return;
            for (int i = 0; i < NavItems.Length; i++)
            {
                var it = NavItems[i];
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = new Path { Data = Geometry.Parse(it[1]), Width = 20, Height = 20, Stretch = Stretch.None, StrokeThickness = 1.6,
                                      StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                                      VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = false };
                row.Children.Add(icon);
                row.Children.Add(new TextBlock { Text = it[0], Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                var item = new ListBoxItem { Content = row, ToolTip = it[0] + "  (Ctrl+" + (i + 1) + ")" };
                // Settings and Logs are about the app, not the race: a little air sets them apart
                if (it[0] == "Settings") item.Margin = new Thickness(0, 12, 0, 2);
                AutomationProperties.SetName(item, it[0]);
                // the icon follows its row's text colour (Dim; Ink when hovered or current)
                icon.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = item });
                nav.Items.Add(item);
            }
            nav.SelectedIndex = tabs.SelectedIndex;
            nav.SelectionChanged += (s, e) => { if (nav.SelectedIndex >= 0 && tabs.SelectedIndex != nav.SelectedIndex) tabs.SelectedIndex = nav.SelectedIndex; };
            // e.Source: lists and dropdowns inside a section raise the same event on their way up
            tabs.SelectionChanged += (s, e) =>
            {
                if (e.Source != tabs) return;
                if (nav.SelectedIndex != tabs.SelectedIndex) nav.SelectedIndex = tabs.SelectedIndex;
                ApplyLayout();
            };
            var small = Get<Button>("SmallBtn"); if (small != null) small.Click += (s, e) => ToggleSmall();
            NavPhase(_phase ?? "off");
            Window.Loaded += (s, e) => { _layoutReady = true; ApplyLayout(); };
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
            var t = Get<TextBlock>("NavState"); var dot = Get<Shape>("NavDot");
            if (t == null || dot == null) return;
            string word = phase == "off" ? "Off" : phase == "turning" ? "Turning on…" : phase == "issues" ? "Needs a look" : "Ready to race";
            t.Text = word;
            t.Foreground = (Brush)Window.Resources[phase == "issues" ? "Warn" : "Dim"];
            dot.Fill = phase == "off" ? new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x60)) : (Brush)Window.Resources[phase == "turning" ? "Dim" : phase == "issues" ? "Warn" : "Live"];
            var sw = Get<ToggleButton>("NavSwitch");
            if (sw != null) sw.ToolTip = "Race Mode: " + word.ToLowerInvariant() + ". Click to turn it " + (phase == "off" ? "on" : "off") + ".";
        }

        void ApplyLayout()
        {
            double w = Window.ActualWidth, h = Window.ActualHeight;
            var tabs = Get<TabControl>("Tabs"); var rail = Get<FrameworkElement>("NavRail");
            if (!_layoutReady || tabs == null || rail == null || w <= 0 || h <= 0) return;

            // ---- which size is this ----
            bool small = w < SmallW || h < SmallH;
            if (small != _small)
            {
                _small = small;
                if (small) { _sectionBeforeSmall = tabs.SelectedIndex; tabs.SelectedIndex = 0; }
                else if (_sectionBeforeSmall >= 0) { tabs.SelectedIndex = _sectionBeforeSmall; _sectionBeforeSmall = -1; }
                var sb = Get<Button>("SmallBtn");
                if (sb != null)
                {
                    sb.ToolTip = small ? "Back to the full window" : "Small panel: just the switch and the live numbers";
                    AutomationProperties.SetName(sb, small ? "Full window" : "Small panel");
                }
            }
            if (!small && Window.WindowState == WindowState.Normal) { _cfg.FullWidth = Math.Round(w); _cfg.FullHeight = Math.Round(h); }
            bool overview = tabs.SelectedIndex == 0;
            bool names = w >= NavNamesAt;
            double navW = small ? 0 : names ? NavWide : NavNarrow;
            bool wide = !small && !overview && w >= WideAt;

            // ---- side menu ----
            rail.Visibility = small ? Visibility.Collapsed : Visibility.Visible;
            if (!small)
            {
                rail.Width = navW;
                var nav = Get<ListBox>("Nav");
                if (nav != null)
                    foreach (ListBoxItem item in nav.Items)
                        foreach (var tb in ((StackPanel)item.Content).Children.OfType<TextBlock>())
                            tb.Visibility = names ? Visibility.Visible : Visibility.Collapsed;
                // the switch is always one click away: under the menu, unless the Overview's big one is on screen
                var status = Get<Border>("NavStatus");
                if (status != null)
                {
                    status.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
                    status.Padding = names ? new Thickness(12, 10, 12, 10) : new Thickness(8, 10, 8, 10);
                    var words = Get<FrameworkElement>("NavStatusWords"); if (words != null) words.Visibility = names ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            // ---- columns: section, and the dashboard beside it when wide ----
            double edge = small ? 12 : 20;
            tabs.Margin = new Thickness(edge, 6, edge, small ? 12 : 0);
            Get<ColumnDefinition>("MainCol").Width = wide ? new GridLength(SectionCol) : new GridLength(1, GridUnitType.Star);
            Get<ColumnDefinition>("DashCol").Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Get<ScrollViewer>("Dash").Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            var footer = Get<FrameworkElement>("Footer"); if (footer != null) footer.Visibility = small ? Visibility.Collapsed : Visibility.Visible;

            double body = w - 2 - (small ? 0 : 12 + navW);            // everything right of the menu
            double page = (wide ? SectionCol : body) - 2 * edge - 4;   // the open section's own width
            double dashW = overview ? page : wide ? body - SectionCol - 24 : 0;

            // ---- overview: switch and numbers, stacked or side by side ----
            bool beside = overview && !small && page >= 1040;
            var stats = Get<Border>("StatsStrip");
            if (stats != null)
            {
                Get<ColumnDefinition>("StatsCol").Width = beside ? new GridLength(1.1, GridUnitType.Star) : new GridLength(0);
                Grid.SetRow(stats, beside ? 0 : 1); Grid.SetColumn(stats, beside ? 1 : 0);
                stats.Margin = beside ? new Thickness(12, 0, 0, 0) : new Thickness(0, 12, 0, 0);
                stats.Visibility = small && h < 256 ? Visibility.Collapsed : Visibility.Visible;   // a sliver: the switch alone
            }
            var here = Get<FrameworkElement>("DashHere"); if (here != null) here.Visibility = small ? Visibility.Collapsed : Visibility.Visible;
            var checks = Get<FrameworkElement>("HeroChecks"); if (checks != null) checks.MaxHeight = small ? 0 : double.PositiveInfinity;
            ApplyDensity(small || h < 700, small || page < 520);

            // ---- dashboard: on the Overview, or beside a section; drawn only while it's on screen ----
            bool dashOn = !small && (overview || wide);
            if (dashOn)
            {
                HostDash(Get<Border>(overview ? "DashHere" : "DashSide"));
                var cg = Get<UniformGrid>("ChartGrid"); if (cg != null) cg.Columns = dashW >= 900 ? 3 : dashW >= 500 ? 2 : 1;
                var bg = Get<UniformGrid>("DashBottom"); if (bg != null) bg.Columns = dashW >= 700 ? 2 : 1;
                var hint = Get<FrameworkElement>("DashHint"); if (hint != null) hint.Visibility = dashW >= 620 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (dashOn && (!_dashOn || !_chartsBuilt))
            {
                _dashOn = true;
                BuildDashboard(); UpdateChecklist(); ShowLastSession(); RenderDashboard();
            }
            _dashOn = dashOn;
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
                var cells = tel.Children.OfType<Border>().ToList();
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
