using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace RaceMode
{
    // The small panel: what a racer parks in a corner of a side monitor. It has three shapes, picked from the
    // window's size (Layout.cs ApplyLayout calls ApplySmallShape on every size change):
    //
    //   Mini    under 960 wide and under 256 tall   the switch alone (420 x 150 is the smallest window)
    //   Card    the default, 460 x 262              switch, then FPS and the numbers: 3 x 2 tiles (one row from 700 wide)
    //   Strip   960+ wide and under 300 tall        one row: state and switch at the left, the numbers across
    //   Column  under 500 wide and 400+ tall        switch, the numbers as rows (label left, value right), the checklist,
    //                                               and as many 60 s charts as the height has room for
    //
    // Everything here reuses the Overview's own elements (Hero, StatsStrip, Telemetry, HeroChecks, the dashboard);
    // the code only arranges them and, for the Column, mirrors the tiles' texts into rows. Going back to a full
    // size undoes all of it (ResetSmallShape).
    public partial class Shell
    {
        enum SmallShape { None, Mini, Card, Strip, Column }
        SmallShape _shape = SmallShape.None;

        const double StripW = 960;        // from here a short window is a strip
        const double ColumnH = 400;       // a narrow window this tall is a column

        static SmallShape ShapeFor(double w, double h)
        {
            if (h < 256) return w >= StripW ? SmallShape.Strip : SmallShape.Mini;
            if (w >= StripW && h < 300) return SmallShape.Strip;
            if (w < SmallW && h >= ColumnH) return SmallShape.Column;
            return SmallShape.Card;
        }

        // How many charts a column has room for under its switch, rows and checklist (about 176 px each).
        // While Race Mode is off there is no checklist, so there is room for one more.
        int ColumnCharts(double h)
        {
            double used = (_phase == "off" ? 160 : 330) + (h >= 640 ? 290 : 206);
            return (int)Math.Max(0, Math.Min(6, Math.Floor((h - used) / 176)));
        }

        // ---- the Column's rows ----
        sealed class ColRow
        {
            public Border Box; public TextBlock Sub; public Run Val, Unit;
            public string ValName, UnitName, SubName, CellName, FixedUnit;
        }
        List<ColRow> _colRows;
        StackPanel _colPanel;          // the rows card
        StackPanel _colChecks;         // header + card around HeroChecks
        Border _colChecksCard;
        Panel _checksHome;
        bool _colRoomy; string _colPhase = "off";
        string _fpsWait = "Waiting";    // "Waiting", and "Waiting for iRacing" when the tile has no room for its sub-line

        void BuildColumn()
        {
            if (_colPanel != null) return;
            var top = Get<Grid>("OverTop"); var over = top == null ? null : top.Parent as Panel;
            var hc = Get<UniformGrid>("HeroChecks");
            if (over == null || hc == null) return;
            _checksHome = hc.Parent as Panel;

            var defs = new[] {
                new ColRow { Sub = null, ValName = "FpsVal",     UnitName = "FpsUnit",     SubName = "FpsSub",     CellName = "FpsCell" },
                new ColRow { ValName = "CpuVal",     FixedUnit = " %",  SubName = "CpuSub",     CellName = null },
                new ColRow { ValName = "CpuTempVal", UnitName = "CpuTempUnit", SubName = "CpuTempSub", CellName = "CpuTempCell" },
                new ColRow { ValName = "GpuVal",     FixedUnit = " %",  SubName = "GpuSub",     CellName = null },
                new ColRow { ValName = "GpuTempVal", UnitName = "GpuTempUnit", SubName = "GpuTempSub", CellName = null },
                new ColRow { ValName = "RamVal",     FixedUnit = " GB", SubName = "RamSub",     CellName = null },
            };
            var labels = new[] { "FPS", "CPU", "CPU temp", "GPU", "GPU temp", "Free RAM" };
            var card = new Border { Style = (Style)Window.Resources["Group"], CornerRadius = new CornerRadius(14) };
            var rows = new StackPanel();
            card.Child = rows;
            _colRows = new List<ColRow>();
            for (int i = 0; i < defs.Length; i++)
            {
                var d = defs[i];
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var lab = new TextBlock { Text = labels[i], FontSize = 13 }; lab.SetResourceReference(TextBlock.ForegroundProperty, "Dim"); left.Children.Add(lab);
                d.Sub = new TextBlock { Style = (Style)Window.Resources["Caption"], Margin = new Thickness(0, 2, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                left.Children.Add(d.Sub);
                g.Children.Add(left);
                d.Val = new Run { FontFamily = (FontFamily)Window.Resources["Display"], FontSize = 24 }; d.Val.SetResourceReference(TextElement.ForegroundProperty, "Ink");
                d.Unit = new Run { FontSize = 12 }; d.Unit.SetResourceReference(TextElement.ForegroundProperty, "Dim");
                var num = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
                Typography.SetNumeralAlignment(num, FontNumeralAlignment.Tabular);
                num.Inlines.Add(d.Val); num.Inlines.Add(d.Unit);
                Grid.SetColumn(num, 1); g.Children.Add(num);
                d.Box = new Border { Child = g, Padding = new Thickness(16, 8, 16, 8), Background = Brushes.Transparent,
                                     BorderThickness = new Thickness(0, 0, 0, i < defs.Length - 1 ? 1 : 0) };
                d.Box.SetResourceReference(Border.BorderBrushProperty, "Hairline");
                AutomationProperties.SetName(d.Box, labels[i]);
                if (d.CellName == "CpuTempCell")
                {
                    d.Box.Focusable = true; d.Box.FocusVisualStyle = (Style)Window.Resources["FocusRingInset"];
                    d.Box.MouseLeftButtonUp += (s, e) => CpuTempAction();
                    d.Box.KeyDown += (s, e) => { if (e.Key == Key.Enter || e.Key == Key.Space) { CpuTempAction(); e.Handled = true; } };
                }
                rows.Children.Add(d.Box);
                _colRows.Add(d);
            }
            _colPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
            _colPanel.Children.Add(card);

            // the checklist, under the rows, while Race Mode is on (HeroChecks moves here and back)
            _colChecksCard = new Border { Style = (Style)Window.Resources["Group"], Padding = new Thickness(16, 6, 12, 6) };
            _colChecks = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
            _colChecks.Children.Add(new TextBlock { Style = (Style)Window.Resources["SectionHeader"], Text = "Race Mode checklist" });
            _colChecks.Children.Add(_colChecksCard);

            int at = over.Children.IndexOf(top);
            over.Children.Insert(at + 1, _colPanel);
            over.Children.Insert(at + 2, _colChecks);
        }

        // Called every second after the tiles are updated, and when the shape changes: copy the tiles' texts into the rows.
        void SyncSmall()
        {
            if (_shape != SmallShape.Column || _colRows == null) return;
            foreach (var r in _colRows)
            {
                var v = Window.FindName(r.ValName) as Run;
                r.Val.Text = v == null ? "" : v.Text;
                if (v != null) r.Val.Foreground = v.Foreground;
                if (r.UnitName != null) { var u = Window.FindName(r.UnitName) as Run; r.Unit.Text = u == null ? "" : u.Text; }
                else r.Unit.Text = r.FixedUnit;
                var s = Get<TextBlock>(r.SubName);
                bool noFps = r.ValName == "FpsVal" && r.Val.Text.Length == 0;
                if (noFps) { r.Unit.Text = "Waiting for iRacing"; r.Sub.Text = ""; r.Sub.Visibility = Visibility.Collapsed; }
                else if (s != null)
                {
                    r.Sub.Text = s.Text; r.Sub.Foreground = s.Foreground; r.Sub.TextDecorations = s.TextDecorations;
                    r.Sub.Visibility = _colRoomy ? Visibility.Visible : Visibility.Collapsed;
                }
                if (r.CellName != null)
                {
                    var c = Get<Border>(r.CellName);
                    if (c != null) { r.Box.Cursor = c.Cursor; r.Box.ToolTip = c.ToolTip; }
                }
                r.Box.Padding = _colRoomy ? new Thickness(16, 9, 16, 9) : new Thickness(16, 5, 16, 5);
            }
        }

        // Needs a look: the status line says what, in amber (with the words; amber is never the only signal), and
        // the full text is a tooltip when the line has to be cut.
        void SmallPhase(string phase)
        {
            var st = Get<TextBlock>("StatusText"); if (st == null) return;
            st.SetResourceReference(TextBlock.ForegroundProperty, phase == "issues" && _shape != SmallShape.None ? "Warn" : "Dim");
            if ((_shape == SmallShape.Column || _shape == SmallShape.Card) && phase != _colPhase) { _colPhase = phase; ApplyLayout(); }   // on or off changes how many charts fit
        }

        // ---- fitting the shape ----
        static readonly string[] TileSubs = { "FpsSub", "CpuSub", "CpuTempSub", "GpuSub", "GpuTempSub", "RamSub" };
        static readonly string[] TileVals = { "FpsVal", "CpuVal", "CpuTempVal", "GpuVal", "GpuTempVal", "RamVal" };

        void ApplySmallShape(SmallShape shape, double w, double h, int colCharts)
        {
            var prev = _shape;
            if (shape == SmallShape.None)
            {
                if (prev != SmallShape.None) { ResetSmallShape(); _shape = SmallShape.None; SmallPhase(_phase); }
                return;
            }
            if (prev != SmallShape.None && prev != shape) ResetSmallShape();
            _shape = shape;
            if (shape == SmallShape.Column) BuildColumn();

            var hero = Get<Border>("Hero"); var stats = Get<Border>("StatsStrip"); var tel = Get<UniformGrid>("Telemetry");
            var top = Get<Grid>("OverTop"); var st = Get<TextBlock>("StatusText");
            var fps = Get<Border>("FpsCell");

            // title bar: 28 px tall buttons (the label and all four buttons stay)
            var bar = Get<Grid>("TitleBar");
            if (bar != null) foreach (var b in bar.Children.OfType<Button>()) b.Height = 28;

            // the switch card: tighter, and the status line may run to two lines (full text in the tooltip)
            if (hero != null) hero.Padding = new Thickness(16, 8, 18, 8);
            if (st != null)
            {
                st.MaxHeight = 36; st.TextTrimming = TextTrimming.CharacterEllipsis;
                st.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Text") { Source = st });
                var row = st.Parent as Grid; if (row != null) row.Margin = new Thickness(1, 3, 0, 0);
            }
            SmallPhase(_phase);

            bool strip = shape == SmallShape.Strip, column = shape == SmallShape.Column, card = shape == SmallShape.Card, mini = shape == SmallShape.Mini;
            bool oneRow = strip || (card && w >= 700);

            // the numbers
            if (stats != null && top != null && tel != null)
            {
                var col0 = top.ColumnDefinitions[0];
                col0.Width = strip ? new GridLength(Math.Max(316, Math.Min(460, Math.Round(w * 0.28)))) : new GridLength(1, GridUnitType.Star);
                Get<ColumnDefinition>("StatsCol").Width = strip ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                Grid.SetRow(stats, strip ? 0 : 1); Grid.SetColumn(stats, strip ? 1 : 0);
                stats.Margin = strip ? new Thickness(10, 0, 0, 0) : new Thickness(0, 8, 0, 0);
                stats.Visibility = mini || column ? Visibility.Collapsed : Visibility.Visible;
                if (fps != null) fps.Visibility = mini || column ? Visibility.Collapsed : Visibility.Visible;
                int cols = oneRow ? 6 : 3;
                tel.Columns = cols;
                var cells = tel.Children.OfType<Border>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
                int rows = (cells.Count + cols - 1) / cols;
                double v = strip ? (h >= 165 ? 10 : 6) : 4;
                for (int i = 0; i < cells.Count; i++)
                {
                    int cx = i % cols, cy = i / cols;
                    cells[i].BorderThickness = new Thickness(0, 0, cx < cols - 1 && i < cells.Count - 1 ? 1 : 0, cy < rows - 1 ? 1 : 0);
                    cells[i].SetResourceReference(Border.BorderBrushProperty, "Hairline");
                    cells[i].Padding = new Thickness(cx == 0 ? 14 : 10, v, 8, v);
                }
                double size = strip ? (h >= 165 ? (w >= 1500 ? 34 : 28) : 24) : 22;
                foreach (var n in TileVals)
                {
                    var r = Window.FindName(n) as Run; if (r == null) continue;
                    r.FontSize = size;
                    var tb = r.Parent as TextBlock; if (tb != null) tb.Margin = new Thickness(0, card ? 0 : 3, 0, 0);
                }
                // a card whose status line needs two lines has no room for the tiles' sub-lines: the numbers stay
                bool noSubs = card && h < 290 && _phase == "issues";
                _fpsWait = noSubs ? "Waiting for iRacing" : "Waiting";
                var fu = Window.FindName("FpsUnit") as Run; var fvr = Window.FindName("FpsVal") as Run;
                if (fu != null && fvr != null && fvr.Text.Length == 0) fu.Text = _fpsWait;
                foreach (var n in TileSubs)
                {
                    var t = Get<TextBlock>(n); if (t == null) continue;
                    t.Margin = new Thickness(0, card ? 1 : 3, 0, 0);
                    t.Visibility = noSubs ? Visibility.Collapsed : Visibility.Visible;
                }
                var op = Get<FrameworkElement>("OverPanel"); if (op != null) op.VerticalAlignment = strip ? VerticalAlignment.Center : VerticalAlignment.Stretch;
            }

            // the column: rows instead of tiles, the checklist, charts if there is room
            if (_colPanel != null)
            {
                _colPanel.Visibility = column ? Visibility.Visible : Visibility.Collapsed;
                var hc = Get<UniformGrid>("HeroChecks");
                if (column && hc != null) _colChecks.SetBinding(UIElement.VisibilityProperty, new Binding("Visibility") { Source = hc });
                else { BindingOperations.ClearBinding(_colChecks, UIElement.VisibilityProperty); _colChecks.Visibility = Visibility.Collapsed; }
                if (column && hc != null && _colChecksCard != null)
                {
                    if (hc.Parent != _colChecksCard)
                    {
                        var p = hc.Parent as Panel; if (p != null) p.Children.Remove(hc);
                        _colChecksCard.Child = hc;
                    }
                    hc.Margin = new Thickness(0); hc.Columns = 1; hc.MaxHeight = double.PositiveInfinity;
                }
                _colRoomy = h >= 640;
                var db = Get<UniformGrid>("DashBottom"); if (db != null) db.Visibility = column ? Visibility.Collapsed : Visibility.Visible;
                var here = Get<FrameworkElement>("DashHere"); if (here != null) here.Margin = new Thickness(0, column ? 12 : 22, 0, 0);
                if (column) SyncSmall();
            }
        }

        // Back to the full window (or to another shape): put everything the shapes moved where the Overview wants it.
        void ResetSmallShape()
        {
            var bar = Get<Grid>("TitleBar");
            if (bar != null) foreach (var b in bar.Children.OfType<Button>()) b.ClearValue(FrameworkElement.HeightProperty);
            var st = Get<TextBlock>("StatusText");
            if (st != null)
            {
                st.ClearValue(FrameworkElement.MaxHeightProperty); st.ClearValue(TextBlock.TextTrimmingProperty); st.ClearValue(FrameworkElement.ToolTipProperty);
                var row = st.Parent as Grid; if (row != null) row.Margin = new Thickness(1, 8, 0, 0);
            }
            var top = Get<Grid>("OverTop"); var tel = Get<UniformGrid>("Telemetry"); var fps = Get<Border>("FpsCell");
            if (top != null) top.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            if (fps != null) fps.Visibility = Visibility.Collapsed;
            if (tel != null)
            {
                tel.Columns = 5;
                var cells = tel.Children.OfType<Border>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
                for (int i = 0; i < cells.Count; i++) cells[i].BorderThickness = new Thickness(0, 0, i < cells.Count - 1 ? 1 : 0, 0);
                if (fps != null) fps.BorderThickness = new Thickness(0, 0, 1, 0);
            }
            _fpsWait = "Waiting";
            foreach (var n in TileSubs) { var t = Get<TextBlock>(n); if (t != null) { t.ClearValue(FrameworkElement.MarginProperty); t.Visibility = Visibility.Visible; } }
            foreach (var n in TileVals) { var r = Window.FindName(n) as Run; var tb = r == null ? null : r.Parent as TextBlock; if (tb != null) tb.Margin = new Thickness(0, 3, 0, 0); }
            var op = Get<FrameworkElement>("OverPanel"); if (op != null) op.VerticalAlignment = VerticalAlignment.Stretch;
            if (_colPanel != null) { _colPanel.Visibility = Visibility.Collapsed; BindingOperations.ClearBinding(_colChecks, UIElement.VisibilityProperty); _colChecks.Visibility = Visibility.Collapsed; }
            var hc = Get<UniformGrid>("HeroChecks");
            if (hc != null && _colChecksCard != null && hc.Parent == _colChecksCard)
            {
                _colChecksCard.Child = null;
                hc.Margin = new Thickness(18, 10, 0, 0); hc.Columns = 2;
            }
            var db = Get<UniformGrid>("DashBottom"); if (db != null) db.Visibility = Visibility.Visible;
            var here = Get<FrameworkElement>("DashHere"); if (here != null) here.Margin = new Thickness(0, 22, 0, 0);
            FitCharts(6);
        }

        // How much of the live band shows: all of it at full size; in a column, the FPS chart from one chart's
        // worth of room and the other readings' card from three. Hidden charts keep recording.
        void FitCharts(int n)
        {
            var fps = Get<FrameworkElement>("FpsHost"); var rest = Get<FrameworkElement>("SmallCard");
            if (fps != null) fps.Visibility = n >= 1 ? Visibility.Visible : Visibility.Collapsed;
            if (rest != null) rest.Visibility = n >= 3 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
