// Dashboard chart tile - one 60-second line chart for the wide layout.
//
// Plain WPF: a Canvas with a Path (StreamGeometry), no chart library. One series per tile, so the title names it
// and there is no legend (small multiples). The line is Ink; Amber is used only for the value when it is past
// its threshold, and a word goes with it ("hot", "below 140"), so colour is never the only signal. Values are
// recorded every second; the line is only redrawn while the tile is visible (nothing when minimised).
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RaceMode
{
    public class ChartTile
    {
        const int N = 60;
        readonly double[] _buf = new double[N];
        int _count, _head;                      // _head = index of the next write

        public string Label = "", Unit = "";
        public double Min, Max = 100;           // fixed scale (stable small multiples)
        public double Ref = double.NaN;         // dashed reference line (cap, threshold)
        public string RefLabel = "";
        public Func<double, bool> IsHot = v => false;
        public Func<double, string> HotWord = v => "hot";
        public Func<double, string> Format = v => v.ToString("0", CultureInfo.InvariantCulture);
        public Func<double, double> Show = v => v;     // display transform (°C -> °F), applied to text only
        public Func<string> Idle = () => "No reading";  // sub-line when the latest value is missing
        public Func<string> Note = () => "";             // sub-line otherwise

        // Look: Boxed = its own card (the lead chart); otherwise a cell inside a shared card, divided by hairlines
        // that the layout sets (Overview.cs). PlotHeight NaN = the plot takes whatever height the cell is given.
        public bool Boxed = true;
        public double PlotHeight = 84, ValueSize = 24;
        public Func<string> IdleWords;   // words in place of "--" when there is no reading ("Not set up")
        public Action IdleAction;        // what clicking the tile (or Enter) does while there is no reading
        public string StripWords = "No reading";   // what the folded strip says in place of the chart (SetStrip)

        Canvas _canvas; Path _line; Line _ref, _cross; Ellipse _dot;
        TextBlock _val, _unit, _sub, _refText, _empty, _action, _capNote;
        Grid _plot;
        bool _strip;
        bool _hovering;
        int _kbAge = -1;   // keyboard read-out position (seconds back), -1 = none
        public FrameworkElement Element;
        public Border Cell;
        static readonly DependencyProperty Fg = TextBlock.ForegroundProperty;

        public void SetPlotHeight(double h) { if (_canvas != null) _canvas.Height = h; PlotHeight = h; }

        public FrameworkElement Build(ResourceDictionary res)
        {
            // colours are live references to the theme tokens (Theme.cs), so an open chart follows a theme change
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = Label, Style = (Style)res["StatLabel"], VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, 3), TextTrimming = TextTrimming.CharacterEllipsis });
            _val = new TextBlock { FontFamily = (FontFamily)res["Display"], FontSize = ValueSize, Text = "--", VerticalAlignment = VerticalAlignment.Bottom };
            _val.SetResourceReference(Fg, "Ink");
            _unit = new TextBlock { Text = Unit, FontSize = 12, Margin = new Thickness(4, 0, 0, 3), VerticalAlignment = VerticalAlignment.Bottom };
            _unit.SetResourceReference(Fg, "Dim");
            var vs = new StackPanel { Orientation = Orientation.Horizontal };
            // the strip's cap, after its words ("Waiting for iRacing   cap 145"); only shown folded
            _capNote = new TextBlock { Style = (Style)res["Caption"], Margin = new Thickness(12, 0, 0, 3), VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
            vs.Children.Add(_val); vs.Children.Add(_unit); vs.Children.Add(_capNote);
            Grid.SetColumn(vs, 1); head.Children.Add(vs);

            _canvas = new Canvas { Height = PlotHeight, Margin = new Thickness(0, 10, 0, 0), Background = Brushes.Transparent, ClipToBounds = true, Cursor = Cursors.Cross };
            _empty = new TextBlock { Style = (Style)res["Caption"], HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false, Margin = new Thickness(0, 10, 0, 0) };
            // baseline: the one recessive axis mark
            var baseLine = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
            baseLine.SetResourceReference(Border.BackgroundProperty, "Hairline");
            _ref = new Line { StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
            _refText = new TextBlock { Style = (Style)res["Caption"], Text = RefLabel, Visibility = Visibility.Collapsed };
            _line = new Path { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _cross = new Line { StrokeThickness = 1, Visibility = Visibility.Collapsed };
            _dot = new Ellipse { Width = 8, Height = 8, StrokeThickness = 2, Visibility = Visibility.Collapsed };
            _ref.SetResourceReference(Shape.StrokeProperty, "Faint");          // reference line (cap, threshold)
            _line.SetResourceReference(Shape.StrokeProperty, "Ink");           // the line: Ink, never a signal colour
            _cross.SetResourceReference(Shape.StrokeProperty, "Faint");        // hover crosshair
            _dot.SetResourceReference(Shape.FillProperty, "Ink");              // hover dot, ringed in the card colour
            _dot.SetResourceReference(Shape.StrokeProperty, "Card");
            _canvas.Children.Add(_ref); _canvas.Children.Add(_refText); _canvas.Children.Add(_line); _canvas.Children.Add(_cross); _canvas.Children.Add(_dot);
            // the fix for a reading that isn't set up ("Open HWiNFO"): right under its words in the head, at any plot height
            _action = new TextBlock { FontSize = 12, TextDecorations = TextDecorations.Underline, HorizontalAlignment = HorizontalAlignment.Right,
                                      VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0), Visibility = Visibility.Collapsed, Cursor = Cursors.Hand };
            _action.SetResourceReference(Fg, "Ink");
            var plot = new Grid(); plot.Children.Add(_canvas); plot.Children.Add(baseLine); plot.Children.Add(_empty); plot.Children.Add(_action);
            _plot = plot;
            _canvas.SizeChanged += (s, e) => Render();
            _canvas.MouseMove += (s, e) => Hover(e.GetPosition(_canvas).X);
            _canvas.MouseLeave += (s, e) => { _hovering = false; _cross.Visibility = Visibility.Collapsed; _dot.Visibility = Visibility.Collapsed; UpdateText(); };

            _sub = new TextBlock { Style = (Style)res["Caption"], Margin = new Thickness(0, 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var stack = new DockPanel();
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(_sub, Dock.Bottom);
            stack.Children.Add(head); stack.Children.Add(_sub); stack.Children.Add(plot);
            var card = Boxed
                ? new Border { Style = (Style)res["Group"], Padding = new Thickness(16, 12, 16, 12), Child = stack }
                : new Border { Background = Brushes.Transparent, Padding = new Thickness(16, 12, 16, 12), Child = stack };
            if (!Boxed) card.SetResourceReference(Border.BorderBrushProperty, "Hairline");
            card.Focusable = true; card.FocusVisualStyle = (Style)res[Boxed ? "FocusRing" : "FocusRingInset"];
            Cell = card;
            System.Windows.Automation.AutomationProperties.SetName(card, ChartName());
            card.MouseLeftButtonUp += (s, e) => { if (IdleAction != null && double.IsNaN(Latest)) IdleAction(); };
            // Keyboard: arrows move the same read-out the mouse gives (Left = further back, Right = nearer now, Home/End).
            card.KeyDown += (s, e) =>
            {
                if ((e.Key == Key.Enter || e.Key == Key.Space) && IdleAction != null && double.IsNaN(Latest)) { IdleAction(); e.Handled = true; return; }
                if (_count == 0) return;
                int age = _kbAge < 0 ? 0 : _kbAge;
                if (e.Key == Key.Left) age = Math.Min(_count - 1, age + 1);
                else if (e.Key == Key.Right) age = Math.Max(0, age - 1);
                else if (e.Key == Key.Home) age = _count - 1;
                else if (e.Key == Key.End) age = 0;
                else return;
                _kbAge = age; e.Handled = true;
                double w = _canvas.ActualWidth; if (w < 10) return;
                Hover(X(age, w));
            };
            card.LostKeyboardFocus += (s, e) => { _kbAge = -1; _hovering = false; _cross.Visibility = Visibility.Collapsed; _dot.Visibility = Visibility.Collapsed; UpdateText(); };
            Element = card;
            return Element;
        }

        string ChartName() { return Label + " chart, last 60 seconds. Left and right arrows step through it."; }

        // Folded: a short strip (label, its words, the reference line's label) in place of the chart, for a reading
        // that has nothing to show yet (FPS before iRacing). Overview.cs decides when; the data keeps recording.
        public void SetStrip(bool on)
        {
            if (_strip == on || Cell == null) return;
            _strip = on;
            _plot.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            _sub.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            _val.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            Cell.Padding = on ? new Thickness(16, 11, 16, 11) : new Thickness(16, 12, 16, 12);
            if (!on) { _capNote.Visibility = Visibility.Collapsed; System.Windows.Automation.AutomationProperties.SetName(Cell, ChartName()); }
            UpdateText();
        }

        public void Push(double v)
        {
            _buf[_head] = v; _head = (_head + 1) % N; if (_count < N) _count++;
        }

        double At(int age)   // age 0 = newest
        {
            if (age < 0 || age >= _count) return double.NaN;
            return _buf[(_head - 1 - age + N * 2) % N];
        }

        public double Latest { get { return At(0); } }

        double Y(double v, double h) { var c = Math.Max(Min, Math.Min(Max, v)); return h - (c - Min) / (Max - Min) * (h - 2) - 1; }
        double X(int age, double w) { return w - age * w / (N - 1); }

        // Redraws the line from the buffer; call once per second while visible.
        public void Render()
        {
            if (_canvas != null && _strip) { UpdateText(); return; }
            if (_canvas == null || !_canvas.IsVisible) return;
            double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
            if (w < 10 || h < 10) return;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                bool inFigure = false;
                for (int age = _count - 1; age >= 0; age--)
                {
                    var v = At(age);
                    if (double.IsNaN(v)) { inFigure = false; continue; }         // gap: no data, don't bridge it
                    var p = new Point(X(age, w), Y(v, h));
                    if (!inFigure) { ctx.BeginFigure(p, false, false); inFigure = true; } else ctx.LineTo(p, true, true);
                }
            }
            g.Freeze();
            _line.Data = g;
            // a tall plot with nothing in it says why, where the line would be
            bool any = false;
            for (int age = 0; age < _count && !any; age++) if (!double.IsNaN(At(age))) any = true;
            bool emptyNote = !any && h >= 100 && IdleAction == null;   // a fix sits under the head instead (UpdateText)
            _empty.Text = emptyNote ? Idle() : "";
            _empty.Visibility = emptyNote ? Visibility.Visible : Visibility.Collapsed;
            // not set up: no reference line either, so the fix under the head has the plot to itself
            bool fixShown = IdleAction != null && double.IsNaN(Latest) && IdleWords != null && IdleWords().Length > 0;
            if (!double.IsNaN(Ref) && Ref > Min && Ref < Max && !fixShown)
            {
                var y = Math.Round(Y(Ref, h)) + 0.5;
                _ref.X1 = 0; _ref.X2 = w; _ref.Y1 = y; _ref.Y2 = y; _ref.Visibility = Visibility.Visible;
                _refText.Text = RefLabel; _refText.Visibility = string.IsNullOrEmpty(RefLabel) ? Visibility.Collapsed : Visibility.Visible;
                _refText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(_refText, 2);   // at the old end of the line: the newest reading is drawn at the right edge
                Canvas.SetTop(_refText, y - _refText.DesiredSize.Height - 1 < 0 ? y + 2 : y - _refText.DesiredSize.Height - 1);
            }
            else { _ref.Visibility = Visibility.Collapsed; _refText.Visibility = Visibility.Collapsed; }
            if (!_hovering) UpdateText();
        }

        void UpdateText()
        {
            var v = Latest;
            if (_strip)
            {
                _unit.Text = StripWords;
                _capNote.Text = RefLabel; _capNote.Visibility = string.IsNullOrEmpty(RefLabel) ? Visibility.Collapsed : Visibility.Visible;
                System.Windows.Automation.AutomationProperties.SetName(Cell, Label + ": " + StripWords + (string.IsNullOrEmpty(RefLabel) ? "" : ", " + RefLabel));
                return;
            }
            bool fix = IdleAction != null && double.IsNaN(v);
            Cell.Cursor = fix ? Cursors.Hand : null;
            _sub.TextDecorations = null;
            if (double.IsNaN(v))
            {
                var words = IdleWords == null ? "" : IdleWords();
                _val.Text = words.Length > 0 ? "" : "--"; _val.SetResourceReference(Fg, "Ink");
                _unit.Text = words.Length > 0 ? words : Unit;
                // not set up: the fix sits right under those words; the sub-line keeps its height so the plots line up
                bool action = fix && words.Length > 0;
                _action.Text = action ? Idle() : ""; _action.Visibility = action ? Visibility.Visible : Visibility.Collapsed;
                System.Windows.Automation.AutomationProperties.SetHelpText(Cell, action ? words + ". Press Enter: " + Idle() + "." : "");
                bool inPlot = _empty.Visibility == Visibility.Visible;
                _sub.Text = inPlot || action ? " " : Idle(); _sub.SetResourceReference(Fg, "Faint");
                return;
            }
            _action.Visibility = Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetHelpText(Cell, "");
            _unit.Text = Unit;
            bool hot = IsHot(v);
            _val.Text = Format(Show(v));
            _val.SetResourceReference(Fg, hot ? "Warn" : "Ink");
            var note = Note();
            _sub.Text = hot ? HotWord(v) + (string.IsNullOrEmpty(note) ? "" : " · " + note) : note;
            _sub.SetResourceReference(Fg, hot ? "Warn" : "Faint");
        }


        // Point at the chart to read one moment: crosshair, dot, and "23 s ago: 142 fps" in the sub-line.
        void Hover(double x)
        {
            double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
            if (w < 10 || _count == 0) return;
            int age = (int)Math.Round((w - x) * (N - 1) / w);
            age = Math.Max(0, Math.Min(_count - 1, age));
            var v = At(age);
            var px = X(age, w);
            _hovering = true;
            _cross.X1 = px; _cross.X2 = px; _cross.Y1 = 0; _cross.Y2 = h; _cross.Visibility = Visibility.Visible;
            if (double.IsNaN(v)) { _dot.Visibility = Visibility.Collapsed; _sub.Text = (age == 0 ? "Now" : age + " s ago") + ": no reading"; _sub.SetResourceReference(Fg, "Faint"); return; }
            Canvas.SetLeft(_dot, px - 4); Canvas.SetTop(_dot, Y(v, h) - 4); _dot.Visibility = Visibility.Visible;
            _sub.Text = (age == 0 ? "Now" : age + " s ago") + ": " + Format(Show(v)) + (Unit.Length > 0 ? " " + Unit.Trim() : "") + (IsHot(v) ? " · " + HotWord(v) : "");
            _sub.SetResourceReference(Fg, IsHot(v) ? "Warn" : "Ink");
        }
    }
}
