using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace RaceMode
{
    // RaceMode.exe --layouttest <report.txt> [on]
    //
    // Walks the window through a grid of sizes (420 to 3440 wide, 150 to 1440 tall) and, at each, through every
    // section, off-screen and without touching any process. At every stop it lays the window out and checks what
    // a still picture of one size can't show across hundreds of them:
    //   - nothing throws;
    //   - no text that matters is cut off (a TextBlock whose words need more width than it was given);
    //   - no control hangs outside the window, or outside the scrolling area it sits in sideways;
    //   - nothing visible is squeezed below a usable size (buttons and switches under 28 px tall or 24 px wide);
    //   - the Race Mode switch is on screen at every size (the big one, or the one under the menu).
    // The report lists each problem once with the range of sizes it shows at, so a breakpoint that is a few pixels
    // off is easy to see. "on" runs it with the Race Mode ON look (as --snapshot ... on does).
    public partial class Shell
    {
        static readonly int[] TestW = { 420, 440, 460, 480, 499, 500, 520, 560, 600, 640, 700, 759, 760, 800, 860, 900, 959, 960, 1024, 1100, 1200, 1280, 1366, 1439, 1440, 1600, 1920, 2560, 3440 };
        static readonly int[] TestH = { 150, 200, 255, 256, 262, 299, 300, 399, 400, 459, 460, 520, 600, 690, 699, 700, 760, 900, 1080, 1440 };

        // Every section in the side menu sits above the switch block under it (or the menu's bottom when it's hidden).
        bool NavFits()
        {
            var rail = Get<FrameworkElement>("NavRail"); var status = Get<FrameworkElement>("NavStatus");
            if (rail == null || _navRows == null || _navRows.Length == 0 || rail.Visibility != Visibility.Visible) return true;
            var last = _navRows[_navRows.Length - 1];
            double bottom = last.TranslatePoint(new Point(0, last.ActualHeight), rail).Y;
            double limit = status != null && status.Visibility == Visibility.Visible ? status.TranslatePoint(new Point(0, 0), rail).Y : rail.ActualHeight;
            NavOver = bottom - limit;
            return bottom <= limit + 1;
        }

        double NavOver;   // how far the last row ran under, for the report

        sealed class Finding { public string What; public List<string> At = new List<string>(); }

        public void RunLayoutTest(string outPath, bool on)
        {
            var found = new Dictionary<string, Finding>();
            int stops = 0, errors = 0;
            Action<string, string> note = (what, at) =>
            {
                Finding f; if (!found.TryGetValue(what, out f)) { f = new Finding { What = what }; found[what] = f; }
                if (f.At.Count < 400) f.At.Add(at);
            };
            // first-run setup covers the whole window: there is nothing to measure until a config with SetupDone exists
            var setup = Get<UIElement>("SetupOverlay");
            if (setup != null && setup.Visibility == Visibility.Visible)
            { File.WriteAllText(outPath, "Race Mode layout test: not run. First-run setup is showing; use a data folder whose config.json has SetupDone true."); return; }
            if (on) PreviewOn();
            var tabs = Get<TabControl>("Tabs");
            foreach (int w in TestW)
                foreach (int h in TestH)
                {
                    try
                    {
                        Window.Width = w; Window.Height = h;
                        Pump();
                        bool small = _small;
                        int sections = small ? 1 : tabs.Items.Count;
                        for (int s = 0; s < sections; s++)
                        {
                            if (!small) tabs.SelectedIndex = s;
                            Pump();
                            stops++;
                            string at = w + "x" + h, sec = small ? "small panel" : ((TabItem)tabs.Items[s]).Header as string;
                            Check(sec, at, note);
                            if (!small && !NavFits()) note("the side menu's last row runs under the Race Mode switch", at);
                        }
                        if (!small) tabs.SelectedIndex = 0;
                    }
                    catch (Exception ex) { errors++; note("EXCEPTION " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0], w + "x" + h); }
                }

            // A short screen (a 1280x720 laptop at 150% has 432 of room): the full window must still show, and work,
            // at every height it can have there (found on a test PC: it was stuck as the small panel)
            int shortSizes = 0;
            TestRoomH = 432;
            try
            {
                foreach (int w in new[] { 600, 853, 960, 1280 })
                    foreach (int h in new[] { 392, 400, 416, 432 })
                    {
                        shortSizes++;
                        string at0 = w + "x" + h + " short screen";
                        try
                        {
                            Window.Width = w; Window.Height = h;
                            Pump();
                            if (_small) { note("the full window doesn't show on a short screen", at0); continue; }
                            for (int s = 0; s < tabs.Items.Count; s++)
                            {
                                tabs.SelectedIndex = s; Pump(); stops++;
                                Check(((TabItem)tabs.Items[s]).Header as string, at0, note);
                                if (!NavFits()) note("the side menu's last row runs under the Race Mode switch", at0 + " by " + Math.Round(NavOver));
                            }
                            tabs.SelectedIndex = 0;
                        }
                        catch (Exception ex) { errors++; note("EXCEPTION " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0], at0); }
                    }
                // ...and the Small panel button still gives the small panel there
                Window.Width = PanelW; Window.Height = PanelH; Pump(); shortSizes++;
                if (!_small) note("the small panel doesn't show on a short screen", PanelW + "x" + PanelH + " short screen");
            }
            finally { TestRoomH = 0; }

            var sb = new StringBuilder();
            sb.AppendLine("Race Mode layout test (" + (on ? "on" : "off") + "): " + stops + " layouts at " + (TestW.Length * TestH.Length + shortSizes) + " window sizes, " + found.Count + " kinds of problem, " + errors + " exceptions");
            foreach (var f in found.Values.OrderByDescending(x => x.At.Count))
                sb.AppendLine(f.At.Count.ToString(CultureInfo.InvariantCulture).PadLeft(4) + "  " + f.What + "   [" + Ranges(f.At) + "]");
            File.WriteAllText(outPath, sb.ToString());
        }

        // Let layout, bindings and anything the layout queued (focus, re-layout) run.
        void Pump()
        {
            Window.UpdateLayout();
            Window.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            Window.UpdateLayout();
        }

        static string Ranges(List<string> at)
        {
            var d = at.Distinct().ToList();
            return d.Count <= 6 ? string.Join(" ", d) : string.Join(" ", d.Take(3)) + " ... " + string.Join(" ", d.Skip(d.Count - 2)) + " (" + d.Count + " sizes)";
        }

        static string Describe(FrameworkElement el)
        {
            for (DependencyObject p = el; p != null; p = VisualTreeHelper.GetParent(p))
            {
                var fe = p as FrameworkElement;
                if (fe != null && !string.IsNullOrEmpty(fe.Name) && fe.Name != "B" && !fe.Name.StartsWith("PART_", StringComparison.Ordinal)) return fe.Name;
            }
            return el.GetType().Name;
        }

        void Check(string section, string at, Action<string, string> note)
        {
            var root = Get<Grid>("Root"); if (root == null) return;
            double winW = root.ActualWidth, winH = root.ActualHeight;
            bool sawSwitch = false;
            var stack = new Stack<DependencyObject>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var o = stack.Pop();
                var fe = o as FrameworkElement;
                if (fe != null)
                {
                    if (fe.Visibility != Visibility.Visible || fe.ActualWidth <= 0 || fe.ActualHeight <= 0) continue;
                    Rect r;
                    try { r = fe.TransformToAncestor(root).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight)); }
                    catch { continue; }

                    var tb = fe as TextBlock;
                    if (tb != null && tb.Text.Length > 0 && tb.Opacity > 0)
                    {
                        // cut off: the words need more width than the block has, and it can't wrap onto more lines
                        // (a file path in a list row is shortened on purpose and has the whole path as its tooltip)
                        if (tb.TextWrapping == TextWrapping.NoWrap && tb.ToolTip == null)
                        {
#pragma warning disable 618   // the per-monitor DPI overload needs .NET 4.6.2 references; this build uses the 4.0 ones
                            var probe = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                                          new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black,
                                                          null, TextFormattingMode.Display);
#pragma warning restore 618
                            if (probe.WidthIncludingTrailingWhitespace > tb.ActualWidth + 1.5 && tb.Inlines.Count <= 1)
                                note(section + ": text cut off in " + Describe(tb) + " (\"" + Short(tb.Text) + "\")", at);
                        }
                    }
                    bool control = fe is ButtonBase || fe is TextBox || fe is ComboBox || fe is Slider;
                    if (control && fe.IsEnabled)
                    {
                        // inside a scrolling page, being below the fold is fine; hanging off the side is not
                        if (r.Right > winW + 1 || r.Left < -1) note(section + ": " + Describe(fe) + " hangs off the side of the window", at);
                        var sv = Ancestor<ScrollViewer>(fe);
                        if (sv == null && (r.Bottom > winH + 1)) note(section + ": " + Describe(fe) + " is cut off at the bottom, and the page doesn't scroll", at);
                        if ((fe is ButtonBase) && !(fe is RepeatButton) && !(Ancestor<ScrollBar>(fe) != null) && !(Ancestor<ComboBox>(fe) != null) && !(Ancestor<Slider>(fe) != null)
                            && (fe.ActualHeight < 27.5 || fe.ActualWidth < 24))
                            note(section + ": " + Describe(fe) + " is smaller than 28 px (" + (fe.ActualHeight < 27.5 ? Math.Round(fe.ActualHeight) + " tall" : Math.Round(fe.ActualWidth) + " wide") + ")", at);
                        if ((fe.Name == "Switch" || fe.Name == "NavSwitch") && r.Left >= -1 && r.Top >= -1 && r.Right <= winW + 1 && r.Bottom <= winH + 1) sawSwitch = true;
                    }
                }
                int n = VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(o, i));
            }
            if (!sawSwitch) note(section + ": the Race Mode switch is not fully on screen", at);
        }

        static string Short(string s) { s = s.Replace("\r", " ").Replace("\n", " "); return s.Length <= 36 ? s : s.Substring(0, 34) + "…"; }

        static T Ancestor<T>(DependencyObject o) where T : class
        {
            for (var p = VisualTreeHelper.GetParent(o); p != null; p = VisualTreeHelper.GetParent(p)) { var t = p as T; if (t != null) return t; }
            return null;
        }
    }
}
