using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RaceMode
{
    // The side menu's rows: an icon, the section's name and, at the right edge, what is already known about it
    // (how many apps start or close, streaming on, the Race quality level, an update ready, what needs a look).
    // Icon-only, the names and states are tooltips, and a state that wants attention is a small dot on the icon.
    //
    // Groups are made by spacing only (the order is Ctrl+1..7 and never changes):
    //   Overview  ·  Launch, Close, Stream, Graphics (what Race Mode does before a race)  ·  Settings, Logs (the app)
    public partial class Shell
    {
        // the same words as each page's title
        static readonly string[] NavNames = { "Overview", "Launch apps", "Close list", "Streaming", "Graphics", "Settings", "Logs" };

        // One family on a 20 x 20 grid: 1.6 px stroke, round caps and joins, about 14 px of live area,
        // round shapes a little larger than square ones so they look the same size.
        static readonly string[] NavIcons = {
            // Overview: a gauge, the dashboard
            "M4.7,15.9 A7.5,7.5 0 1 1 15.3,15.9 M8.9,10.6 A1.1,1.1 0 1 0 11.1,10.6 A1.1,1.1 0 1 0 8.9,10.6 M10.9,9.7 L13.9,6.7",
            // Launch: start (a play mark, set right of centre so it looks centred)
            "M6.6,4 L16,10 L6.6,16 Z",
            // Close: an app closed
            RoundRect(3.5, 3.5, 13, 13, 3) + " M7.4,7.4 L12.6,12.6 M12.6,7.4 L7.4,12.6",
            // Stream: on air
            "M8.7,10 A1.3,1.3 0 1 0 11.3,10 A1.3,1.3 0 1 0 8.7,10 M6.75,6.75 A4.6,4.6 0 0 0 6.75,13.25 M13.25,6.75 A4.6,4.6 0 0 1 13.25,13.25 " +
            "M4.3,4.3 A8.06,8.06 0 0 0 4.3,15.7 M15.7,4.3 A8.06,8.06 0 0 1 15.7,15.7",
            // Graphics: a picture
            RoundRect(2.5, 3.5, 15, 13, 2.5) + " M5.8,7.9 A1.4,1.4 0 1 0 8.6,7.9 A1.4,1.4 0 1 0 5.8,7.9 M17.5,12.6 L13.6,8.7 L5.6,16.5",
            // Settings: three sliders
            "M2.5,4.5 H11.8 M15.2,4.5 H17.5 M2.5,10 H4.8 M8.2,10 H17.5 M2.5,15.5 H9.8 M13.2,15.5 H17.5 " +
            "M11.8,4.5 A1.7,1.7 0 1 0 15.2,4.5 A1.7,1.7 0 1 0 11.8,4.5 M4.8,10 A1.7,1.7 0 1 0 8.2,10 A1.7,1.7 0 1 0 4.8,10 M9.8,15.5 A1.7,1.7 0 1 0 13.2,15.5 A1.7,1.7 0 1 0 9.8,15.5",
            // Logs: the session record, a page of lines
            "M11.5,2.5 H5.5 A1.5,1.5 0 0 0 4,4 V16 A1.5,1.5 0 0 0 5.5,17.5 H14.5 A1.5,1.5 0 0 0 16,16 V7 Z M11.5,2.5 V7 H16 M7,10.5 H13 M7,13.5 H11",
        };

        static string RoundRect(double x, double y, double w, double h, double r)
        {
            Func<double, string> f = v => v.ToString("0.##", CultureInfo.InvariantCulture);
            string a = " A" + f(r) + "," + f(r) + " 0 0 1 ";
            return "M" + f(x + r) + "," + f(y) + " H" + f(x + w - r) + a + f(x + w) + "," + f(y + r) + " V" + f(y + h - r) + a + f(x + w - r) + "," + f(y + h) +
                   " H" + f(x + r) + a + f(x) + "," + f(y + h - r) + " V" + f(y + r) + a + f(x + r) + "," + f(y) + " Z";
        }

        ListBoxItem[] _navRows; TextBlock[] _navName, _navValue; Ellipse[] _navValueDot, _navPip;
        bool _navWithNames = true;

        void BuildNavRows(ListBox nav)
        {
            int n = NavNames.Length;
            _navRows = new ListBoxItem[n]; _navName = new TextBlock[n]; _navValue = new TextBlock[n]; _navValueDot = new Ellipse[n]; _navPip = new Ellipse[n];
            for (int i = 0; i < n; i++)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var item = new ListBoxItem { Content = row };
                var icon = new Path { Data = Geometry.Parse(NavIcons[i]), Width = 20, Height = 20, Stretch = Stretch.None, StrokeThickness = 1.6,
                                      StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                                      SnapsToDevicePixels = false };
                // the icon follows its row's text colour (Dim; Ink when hovered or current)
                icon.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = item });
                // icon-only: a state that wants attention is a dot at the icon's top right
                var pip = new Ellipse { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                                        Margin = new Thickness(0, -3, -5, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
                var iconBox = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
                iconBox.Children.Add(icon); iconBox.Children.Add(pip);
                row.Children.Add(iconBox);

                var name = new TextBlock { Text = NavNames[i], Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(name, 1); row.Children.Add(name);

                // the value: Faint, regular weight even on the current row, so the name stays the loudest thing
                var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
                var value = new TextBlock { FontSize = 12, FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
                value.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                var valueBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                valueBox.Children.Add(dot); valueBox.Children.Add(value);
                Grid.SetColumn(valueBox, 2); row.Children.Add(valueBox);

                // spacing makes the groups: the race sections after the Overview, then the app's own
                if (i == 1 || i == 5) item.Margin = new Thickness(0, 14, 0, 2);
                AutomationProperties.SetName(item, NavNames[i]);
                nav.Items.Add(item);
                _navRows[i] = item; _navName[i] = name; _navValue[i] = value; _navValueDot[i] = dot; _navPip[i] = pip;
            }
            // counts follow the lists as they change (adding, removing, undo); ticking an app saves the config, which refreshes too
            _launch.CollectionChanged += (s, e) => RefreshNav();
            _close.CollectionChanged += (s, e) => RefreshNav();
            // when something needs a look, the words at the top of the menu open the Overview, where the checklist is
            var words = Get<FrameworkElement>("NavStatusWords");
            if (words != null) words.MouseLeftButtonUp += (s, e) => { if (_phase == "issues") GoToSection(0); };
            // names or icons only by hand, remembered between runs (the window's width still rules under 760 px)
            var fold = Get<Button>("NavCollapse");
            if (fold != null) fold.Click += (s, e) => { _cfg.NavCollapsed = !_cfg.NavCollapsed; SaveConfig(); ApplyLayout(); };
            RefreshNav();
        }

        // Names or icons only, and the switch block and the collapse button to match.
        // canFold: the window is wide enough for names, so the button can fold or unfold them.
        void LayoutNav(bool names, bool canFold)
        {
            var fold = Get<Button>("NavCollapse"); var chevron = Get<Path>("NavCollapseIcon");
            if (fold != null)
            {
                fold.Visibility = canFold ? Visibility.Visible : Visibility.Collapsed;
                // with names it sits under the icon column; icon-only, centred like the icons
                fold.HorizontalAlignment = names ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                fold.Margin = names ? new Thickness(5, 8, 0, 0) : new Thickness(0, 8, 0, 0);
                string what = names ? "Collapse menu" : "Expand menu";
                AutomationProperties.SetName(fold, what);
                fold.ToolTip = names ? "Collapse menu: icons only" : "Expand menu: show the names";
                if (chevron != null) chevron.Data = Geometry.Parse(names ? "M12,6 L8,10 L12,14" : "M8,6 L12,10 L8,14");
            }
            _navWithNames = names;
            if (_navRows != null)
                for (int i = 0; i < _navRows.Length; i++)
                {
                    // icon-only rows centre the icon in the 60 px menu: 1 + 19 + 20 + 19 + 1. (20 a side left 18 px for
                    // the 20 px icon, so WPF clipped the row and the attention dot on the icon's corner never showed.)
                    _navRows[i].Padding = names ? new Thickness(12, 8, 12, 8) : new Thickness(19, 8, 19, 8);
                    _navName[i].Visibility = names ? Visibility.Visible : Visibility.Collapsed;
                    ((FrameworkElement)_navValue[i].Parent).Visibility = names ? Visibility.Visible : Visibility.Collapsed;
                }
            var status = Get<Border>("NavStatus"); var words = Get<FrameworkElement>("NavStatusWords");
            var dot = Get<FrameworkElement>("NavDot"); var sw = Get<FrameworkElement>("NavSwitch");
            if (status == null || words == null || dot == null || sw == null) return;
            // with names: light, words, switch on one row, lined up with the icons and names above.
            // icon-only: the switch centred, its light under it.
            status.Padding = names ? new Thickness(12, 9, 9, 9) : new Thickness(0, 10, 0, 10);
            words.Visibility = names ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(dot, names ? 0 : 1); Grid.SetColumn(dot, 0); Grid.SetColumnSpan(dot, names ? 1 : 3);
            dot.Margin = names ? new Thickness(0) : new Thickness(0, 8, 0, 0);
            Grid.SetColumn(sw, names ? 2 : 0); Grid.SetColumnSpan(sw, names ? 1 : 3);
            sw.HorizontalAlignment = names ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            var grid = Get<Grid>("NavStatusGrid");
            if (grid != null) grid.ColumnDefinitions[0].Width = names ? new GridLength(20) : new GridLength(0, GridUnitType.Auto);
            RefreshNav();
        }

        // What each row says. Only values the app already has; nothing is shown when there's nothing to say.
        void RefreshNav()
        {
            if (_navRows == null || _cfg == null) return;
            // icon-only, a grey light under an off switch only repeats the switch; it keeps its place so nothing jumps
            var light = Get<FrameworkElement>("NavDot");
            if (light != null) light.Visibility = !_navWithNames && _phase == "off" ? Visibility.Hidden : Visibility.Visible;            // Overview: what needs a look (count, amber)
            bool issues = _phase == "issues" && _issues.Count > 0;
            SetNavValue(0, issues ? _issues.Count.ToString(CultureInfo.InvariantCulture) : null, issues ? "Warn" : null, true,
                        issues ? NeedsALook() : null);
            // Launch: how many apps start; Close: how many apps are closed (or Off when Settings turns that step off)
            int launch = _launch.Count(a => a.Enabled);
            SetNavValue(1, !_cfg.LaunchApps ? "Off" : launch > 0 ? launch + (launch == 1 ? " app" : " apps") : null, null, false,
                        !_cfg.LaunchApps ? "Starting apps is switched off" : launch > 0 ? launch + " app" + (launch == 1 ? "" : "s") + " start when Race Mode turns on" : "No apps to start");
            int close = _close.Count;
            SetNavValue(2, !_cfg.CloseApps ? "Off" : close > 0 ? close + (close == 1 ? " app" : " apps") : null, null, false,
                        !_cfg.CloseApps ? "Closing apps is switched off" : close > 0 ? close + " app" + (close == 1 ? "" : "s") + " closed when Race Mode turns on" : "No apps to close");
            // Stream: on (an Ink dot, like the update: in the menu, red means only that Race Mode is on)
            SetNavValue(3, _cfg.Streaming ? "On" : null, _cfg.Streaming ? "Ink" : null, true, _cfg.Streaming ? "Streaming is on" : null);
            // Graphics: the quality Race Mode applies
            string gfx = null, gfxTip = null;
            if (_cfg.GraphicsMode == "Photo") { gfx = "Photo"; gfxTip = "Photo mode is on"; }
            else if (_cfg.GraphicsRace != null && _cfg.GraphicsRace.Level > 0) { gfx = "Level " + _cfg.GraphicsRace.Level; gfxTip = "Race quality " + _cfg.GraphicsRace.Level; }
            SetNavValue(4, gfx, null, false, gfxTip);
            // Settings: an update is ready to install
            bool update = _update != null && !_updateInstalled;
            SetNavValue(5, update ? "Update" : null, update ? "Ink" : null, true, update ? "Version " + _update.Version + " is ready to install" : null);
            // Logs: a session is being recorded
            bool rec = _mon != null && _mon.IsLogging;
            SetNavValue(6, rec ? "Recording" : null, null, false, rec ? "Recording this session" : null);
        }

        // value: the words at the right edge (null = none). dotKey: a brush for a dot before them (null = none).
        // pip: icon-only, the dot also shows on the icon. tip: the words for the tooltip and screen readers.
        void SetNavValue(int i, string value, string dotKey, bool pip, string tip)
        {
            _navValue[i].Text = value ?? "";
            _navValue[i].SetResourceReference(TextBlock.ForegroundProperty, dotKey == "Warn" ? "Warn" : "Dim");   // Dim: Faint fell to 4.3:1 on the current row
            // 12 px Segoe like a caption: these are read at rest, not glanced at mid-race (Bahnschrift sat high on the row)
            _navValue[i].Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
            bool dot = dotKey != null;
            if (dot) { _navValueDot[i].SetResourceReference(Shape.FillProperty, dotKey); _navPip[i].SetResourceReference(Shape.FillProperty, dotKey); }
            else { _navValueDot[i].Fill = null; _navPip[i].Fill = null; }
            _navValueDot[i].Visibility = dot ? Visibility.Visible : Visibility.Collapsed;
            _navPip[i].Visibility = pip && dot && !_navWithNames ? Visibility.Visible : Visibility.Collapsed;
            _navRows[i].ToolTip = NavNames[i] + (tip != null ? ": " + tip : "") + "  (Ctrl+" + (i + 1) + ")";
            AutomationProperties.SetHelpText(_navRows[i], tip ?? "");
        }
    }
}
