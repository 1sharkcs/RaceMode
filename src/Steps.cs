using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    // The Overview's steps: what Race Mode does, one row per step, in the order TurnOn() does it.
    //
    //   Log -> Close list -> Launch apps -> Streaming apps -> Screen check -> Race/Photo graphics -> Open iRacing
    //   -> Background apps -> Put iRacing first (the last one happens once the sim is running, WatchTick).
    //
    // Each row: a state dot, the step's name, and one line: what it will do (Race Mode off) or what it did (on).
    // A step switched off in Settings stays in its place and says "Off". Launch and streaming apps get one compact
    // line per app with its running state, since which app didn't start is the most useful fact on this page.
    // Rows are read-only; each is a button that opens the page where that step is set up.
    // Dots: green = done and confirmed, amber = needs a look (always with words), dim = under way,
    // ring = will happen, small grey = off. The words carry the meaning; the dot repeats it.
    public partial class Shell
    {
        const string StDone = "Live", StLook = "Warn", StBusy = "Dim", StTodo = "Todo", StOff = "Off";
        static readonly string[] SectionNames = { "Overview", "Launch apps", "Close list", "Streaming", "Graphics", "Settings", "Logs" };

        sealed class StepApp { public string Name, Words, State; }
        sealed class StepView
        {
            public string Key, Name, Line, State; public int Section;
            public List<StepApp> Apps = new List<StepApp>();
            public StepView Set(string state, string line) { State = state; Line = line; return this; }
        }
        sealed class StepRow
        {
            public string Key; public int Section; public Button Btn; public Ellipse Dot; public Border Up, Down;
            public TextBlock Title, Line; public StackPanel Apps, Text; public RowDefinition RailTop; public double Weight;
        }

        List<StepRow> _stepRows;
        StackPanel _stepColA, _stepColB;
        int _stepCols = 1; string _stepSplit = "";

        static string Count(int n, string one) { return n.ToString(CultureInfo.InvariantCulture) + " " + one + (n == 1 ? "" : "s"); }

        // What each step will do or did, from the same state the switch card and events.log use.
        List<StepView> StepViews()
        {
            bool pv = _previewOn;                       // --snapshot ... on: the same made-up "all good" state as the switch card
            bool on = _on || pv;
            bool turning = !pv && _phase == "turning";
            var list = new List<StepView>();

            // 1. the log starts first, so everything after it is recorded
            var log = new StepView { Key = "Log", Name = "Log", Section = 6 };
            string every = "every " + _cfg.LogIntervalSec.ToString(CultureInfo.InvariantCulture) + " s";
            if (!_cfg.Logging) log.Set(StOff, "Off");
            else if (!on) log.Set(StTodo, "Records FPS and temperatures " + every);
            else if (pv || _mon.IsLogging) log.Set(StDone, "Recording " + every);
            else log.Set(StBusy, "Not recording this time");
            list.Add(log);

            // 2. Close list
            var cl = new StepView { Key = "Close", Name = "Close list", Section = 2 };
            var closable = _close.Where(n => !ProtectedFromClose(n)).ToList();
            // while on, apps left open on purpose (OBS live, other streaming apps, a save prompt, admin) aren't "still running":
            // their reason is in the issue list
            int openNow = pv ? 0 : closable.Count(n => IsRunning(n) && !(on && LeftOnPurpose(n)));
            if (!_cfg.CloseApps) cl.Set(StOff, "Off");
            else if (!on) cl.Set(StTodo, closable.Count == 0 ? "Nothing on the list" : "Closes " + Count(closable.Count, "app") + (openNow > 0 ? ", " + openNow + " open now" : ""));
            else if (pv) cl.Set(StDone, "Closed " + Count(closable.Count, "app"));
            else if (openNow > 0) cl.Set(turning ? StBusy : StLook, turning ? "Closing " + Count(openNow, "app") + "…" : openNow + " still running");
            else
            {
                int leftOpen = closable.Count(n => IsRunning(n) && LeftOnPurpose(n) && !(ObsGuard.IsObs(n) && !ObsGuard.IsObsMain(n)));
                cl.Set(StDone, _closed.Count > 0 ? "Closed " + Count(_closed.Count, "app") + (leftOpen > 0 ? ", left " + leftOpen + " open" : "")
                    : leftOpen > 0 ? "Left " + Count(leftOpen, "app") + " open" : "None were open");
            }
            list.Add(cl);

            // 3. Launch apps, with a line per app
            list.Add(AppsStep("Launch", "Launch apps", 1, _cfg.LaunchApps, _launch, on, turning, pv, "app"));

            // 4. streaming apps, only while Streaming is on
            var st = AppsStep("Stream", "Streaming apps", 3, _cfg.Streaming, _stream, on, turning, pv, "streaming app");
            if (_cfg.Streaming && on && st.State != StLook)
            {
                var o = Obs.Latest;
                if (pv) st.Set(StDone, "Connected to OBS");
                else if (!o.Connected) st.Set(turning ? StBusy : StDone, st.Line + (o.Status.StartsWith("OBS isn't open") ? ", OBS isn't open" : ", OBS not connected"));
                else if (o.Reconnecting) st.Set(StLook, "OBS is reconnecting");
                else st.Set(StDone, !o.Streaming ? "Connected to OBS" : double.IsNaN(o.Kbps) ? "Streaming" : "Streaming at " + N0(o.Kbps) + " kbps");
            }
            list.Add(st);

            // 5. screen check: iRacing on the racing monitor, filling it
            var sc = new StepView { Key = "Screen", Name = "Screen check", Section = 5 };
            bool guard = _cfg.DisplayGuard != null && _cfg.DisplayGuard.Enabled;
            string dl = pv ? "screen OK" : _displayLine;
            bool three = guard && _cfg.DisplayGuard.IsTriple;
            if (!guard) sc.Set(StOff, "Off");
            else if (dl == "screen needs a look") sc.Set(StLook, "Needs a look, see Settings");
            else if (!on) sc.Set(StTodo, three ? "Checks iRacing spans your three screens" : "Checks iRacing opens on your racing monitor");
            else if (dl == "screen OK") sc.Set(StDone, three ? "Three screens OK" : "Racing monitor OK");
            else if (dl == "screen not checked") sc.Set(StBusy, "Not checked this time");
            else sc.Set(StBusy, "Checking…");
            list.Add(sc);

            // 6. Race or Photo graphics
            string mode = _cfg.GraphicsMode;
            var gx = new StepView { Key = "Graphics", Name = mode + " graphics", Section = 4 };
            if (!pv && _graphicsPending) gx.Set(StBusy, "Changes when iRacing closes");
            else if (!on) gx.Set(StTodo, "Applies your " + mode + " settings");
            else if (pv || _graphicsOk) gx.Set(StDone, "Your " + mode + " settings are set");
            else gx.Set(StLook, "Not set, see Graphics");
            list.Add(gx);

            // 7. open iRacing
            var ir = new StepView { Key = "IRacing", Name = "Open iRacing", Section = 5 };
            bool sim = pv || IsRunning(Monitor.SimName), ui = IsRunning("iRacingUI");
            bool found = pv || File.Exists(_cfg.IRacingUI);
            if (!_cfg.StartIRacing) ir.Set(StOff, "Off");
            else if (!found && !sim && !ui) ir.Set(StLook, "iRacing app not found");
            else if (!on) ir.Set(StTodo, ui || sim ? "Already open" : "Opens the iRacing app");
            else if (sim) ir.Set(StDone, "In a session");
            else if (ui) ir.Set(StDone, "iRacing is open");
            else ir.Set(StBusy, turning ? "Opening…" : "Not open");
            list.Add(ir);

            // 8. background apps slowed down
            var bg = new StepView { Key = "Background", Name = "Background apps", Section = 5 };
            if (!_cfg.LowerBackground) bg.Set(StOff, "Off");
            else if (!on) bg.Set(StTodo, _cfg.Lower.Count == 0 ? "Nothing on the list" : "Slows down " + Count(_cfg.Lower.Count, "background app"));
            else if (pv) bg.Set(StDone, "5 slowed down");
            else bg.Set(_lowered.Count > 0 ? StDone : StBusy, _lowered.Count > 0 ? _lowered.Count + " slowed down" : "None running");
            list.Add(bg);

            // 9. iRacing first, once the sim is running
            var pf = new StepView { Key = "First", Name = "Put iRacing first", Section = 5 };
            if (!_cfg.SimHighPriority) pf.Set(StOff, "Off");
            else if (!on) pf.Set(StTodo, "Runs iRacing ahead of other apps in a session");
            else if (pv || _simBoosted) pf.Set(StDone, "iRacing is first");
            else if (IsRunning(Monitor.SimName)) pf.Set(StBusy, "Not changed, see Logs");
            else pf.Set(StBusy, _simSeen ? "Between sessions" : "Waiting for a session");
            list.Add(pf);
            return list;
        }

        // A step that starts apps: its own line, and one line per app with whether it's running.
        StepView AppsStep(string key, string name, int section, bool enabled, IEnumerable<LaunchItem> items, bool on, bool turning, bool pv, string noun)
        {
            var v = new StepView { Key = key, Name = name, Section = section };
            if (!enabled) return v.Set(StOff, "Off");
            var apps = items.Where(i => i.Enabled).ToList();
            if (apps.Count == 0) return v.Set(on ? StDone : StTodo, "No apps on the list");
            int running = 0, missing = 0;
            foreach (var i in apps)
            {
                bool checkable = !string.IsNullOrEmpty(i.ProcessName);
                bool run = pv || (checkable && IsRunning(i.ProcessName));
                if (run) running++;
                var a = new StepApp { Name = i.Name };
                if (!pv && !i.Installed) { a.Words = "not installed"; a.State = StLook; missing++; }
                else if (run) { a.Words = "running"; a.State = StDone; }
                else if (!checkable) { a.Words = on ? "started" : "will start"; a.State = on ? StBusy : StTodo; running++; }
                else if (!on) { a.Words = "not running"; a.State = StOff; }
                else if (turning) { a.Words = "starting…"; a.State = StBusy; }
                else { a.Words = "not running"; a.State = StLook; missing++; }
                v.Apps.Add(a);
            }
            if (!on) return missing > 0 ? v.Set(StLook, missing + " not installed, see " + SectionNames[section]) : v.Set(StTodo, "Starts " + Count(apps.Count, noun) + ", skips any already open");
            string line = running + " of " + apps.Count + " running";
            return v.Set(missing > 0 ? StLook : running == apps.Count ? StDone : StBusy, line);
        }

        // Builds the rows once and updates them in place (so a row with the keyboard keeps it every refresh).
        void UpdateSteps(UniformGrid list)
        {
            var views = StepViews();
            if (_stepRows == null)
            {
                _stepColA = new StackPanel(); _stepColB = new StackPanel();
                list.Children.Clear(); list.Children.Add(_stepColA); list.Children.Add(_stepColB);
                _stepRows = views.Select(MakeStepRow).ToList();
            }
            for (int i = 0; i < views.Count && i < _stepRows.Count; i++) PaintStep(_stepRows[i], views[i]);
            LayoutSteps();
        }

        Style _stepStyle;
        StepRow MakeStepRow(StepView v)
        {
            var res = Window.Resources;
            if (_stepStyle == null)
            {
                var tabs = Get<TabControl>("Tabs");
                var over = tabs == null ? null : tabs.Items[0] as TabItem;
                _stepStyle = over == null ? null : over.Resources["StepRow"] as Style;
            }
            var r = new StepRow { Key = v.Key, Section = v.Section };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            // the rail: a hairline from the step before, the dot level with the name, a hairline to the next step
            var rail = new Grid();
            r.RailTop = new RowDefinition { Height = new GridLength(9) }; rail.RowDefinitions.Add(r.RailTop);
            rail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            rail.RowDefinitions.Add(new RowDefinition());
            r.Up = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
            r.Up.SetResourceReference(Border.BackgroundProperty, "Line");
            r.Dot = new Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            r.Down = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            r.Down.SetResourceReference(Border.BackgroundProperty, "Line");
            Grid.SetRow(r.Dot, 1); Grid.SetRow(r.Down, 2);
            rail.Children.Add(r.Up); rail.Children.Add(r.Dot); rail.Children.Add(r.Down);
            grid.Children.Add(rail);

            var text = new StackPanel { Margin = new Thickness(10, 5, 0, 6) }; r.Text = text;
            r.Title = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
            r.Line = new TextBlock { Style = (Style)res["Sub"], Margin = new Thickness(0, 1, 0, 0) };
            r.Apps = new StackPanel { Margin = new Thickness(0, 3, 0, 0) };
            text.Children.Add(r.Title); text.Children.Add(r.Line); text.Children.Add(r.Apps);
            Grid.SetColumn(text, 1); grid.Children.Add(text);

            string page = SectionNames[v.Section];
            r.Btn = new Button { Content = grid, Name = "Step" + v.Key, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = "Open " + page };
            if (_stepStyle != null) r.Btn.Style = _stepStyle;
            r.Btn.Click += (s, e) => GoToSection(r.Section);
            return r;
        }

        void PaintStep(StepRow r, StepView v)
        {
            var res = Window.Resources;
            bool off = v.State == StOff;
            r.Title.Text = v.Name;
            r.Title.SetResourceReference(TextBlock.ForegroundProperty, off ? "Dim" : "Ink");
            r.Line.Text = v.Line;
            r.Line.SetResourceReference(TextBlock.ForegroundProperty, v.State == StLook ? "Warn" : off ? "Faint" : "Dim");
            StyleDot(r.Dot, v.State, 10);

            // one compact line per app: a small dot, the name, and its state in words
            while (r.Apps.Children.Count > v.Apps.Count) r.Apps.Children.RemoveAt(r.Apps.Children.Count - 1);
            for (int i = 0; i < v.Apps.Count; i++)
            {
                var a = v.Apps[i];
                Grid row;
                if (i < r.Apps.Children.Count) row = (Grid)r.Apps.Children[i];
                else
                {
                    row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    var d = new Ellipse { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) };
                    var t = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(t, 1); row.Children.Add(d); row.Children.Add(t);
                    r.Apps.Children.Add(row);
                }
                StyleDot((Ellipse)row.Children[0], a.State, 6);
                var tb = (TextBlock)row.Children[1];
                tb.Inlines.Clear();
                tb.Inlines.Add(Palette.Run(a.Name + "  ", "Ink"));
                tb.Inlines.Add(Palette.Run(a.Words, a.State == StLook ? "Warn" : "Dim"));
            }
            r.Apps.Visibility = v.Apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            r.Weight = 1 + 0.4 * v.Apps.Count;

            string name = v.Name + ": " + v.Line;
            if (v.Apps.Count > 0) name += ". " + string.Join(", ", v.Apps.Select(a => a.Name + " " + a.Words).ToArray());
            AutomationProperties.SetName(r.Btn, name + ". Opens " + SectionNames[v.Section] + ".");
        }

        void StyleDot(Ellipse dot, string state, double size)
        {
            var res = Window.Resources;
            dot.StrokeThickness = 0; dot.Stroke = null;
            if (state == StTodo)
            {
                dot.Width = size; dot.Height = size;
                dot.Fill = Brushes.Transparent; dot.SetResourceReference(Shape.StrokeProperty, "Dim"); dot.StrokeThickness = size >= 10 ? 1.5 : 1.2;
            }
            else if (state == StOff)
            {
                dot.Width = size * 0.6; dot.Height = size * 0.6;
                dot.SetResourceReference(Shape.FillProperty, "DotOff");
            }
            else
            {
                dot.Width = size; dot.Height = size;
                dot.SetResourceReference(Shape.FillProperty, state);
            }
        }

        // One or two columns: the list reads down the first column, then the second. The split keeps the columns
        // about as tall as each other (a step with app lines weighs more).
        void SetStepColumns(int cols)
        {
            _stepCols = cols; LayoutSteps();   // cheap when nothing moved; also picks up the compact rows
        }

        void LayoutSteps()
        {
            var list = Get<UniformGrid>("Checklist");
            if (list == null || _stepRows == null) return;
            int n = _stepRows.Count, split = n;
            if (_stepCols >= 2)
            {
                double total = _stepRows.Sum(r => r.Weight), best = double.MaxValue, run = 0;
                for (int k = 1; k < n; k++)
                {
                    run += _stepRows[k - 1].Weight;
                    double tallest = Math.Max(run, total - run);
                    if (tallest < best - 0.01) { best = tallest; split = k; }
                }
            }
            list.Columns = _stepCols >= 2 ? 2 : 1;
            string key = _stepCols + ":" + split;
            if (key != _stepSplit)
            {
                _stepSplit = key;
                for (int i = 0; i < n; i++)
                {
                    var b = _stepRows[i].Btn; var want = i < split ? _stepColA : _stepColB;
                    if (b.Parent == want) continue;
                    var old = b.Parent as Panel; if (old != null) old.Children.Remove(b);
                    // keep the order: insert after the rows already in this column
                    want.Children.Add(b);
                }
                // re-adding can leave a column out of order after a split moved: sort both by step order
                foreach (var col in new[] { _stepColA, _stepColB })
                {
                    var ordered = _stepRows.Where(r => r.Btn.Parent == col).Select(r => r.Btn).ToList();
                    bool same = ordered.Count == col.Children.Count;
                    for (int i = 0; i < ordered.Count && same; i++) if (col.Children[i] != ordered[i]) same = false;
                    if (!same) { col.Children.Clear(); foreach (var b in ordered) col.Children.Add(b); }
                }
            }
            // short windows (the same compact size the switch card uses): tighter rows, the dot still level with the name
            foreach (var r in _stepRows)
            {
                r.Text.Margin = _compact ? new Thickness(10, 3, 0, 3) : new Thickness(10, 5, 0, 6);
                r.RailTop.Height = new GridLength(_compact ? 7 : 9);
            }
            bool two = _stepCols >= 2 && split < n;
            _stepColB.Visibility = two ? Visibility.Visible : Visibility.Collapsed;
            // the gutter between the columns; one column runs the full width
            _stepColA.Margin = new Thickness(0, 0, two ? 10 : 0, 0);
            _stepColB.Margin = new Thickness(two ? 10 : 0, 0, 0, 0);
            // the rail joins the steps within a column, and stops at its ends
            foreach (var col in new[] { _stepColA, _stepColB })
                for (int i = 0; i < col.Children.Count; i++)
                {
                    var r = _stepRows.First(x => x.Btn == col.Children[i]);
                    r.Up.Visibility = i > 0 ? Visibility.Visible : Visibility.Hidden;
                    r.Down.Visibility = i < col.Children.Count - 1 ? Visibility.Visible : Visibility.Hidden;
                }
        }
    }
}
