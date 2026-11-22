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

        Canvas _canvas; Path _line; Line _ref, _cross; Ellipse _dot;
        TextBlock _val, _unit, _sub, _refText;
        bool _hovering;
        int _kbAge = -1;   // keyboard read-out position (seconds back), -1 = none
        public FrameworkElement Element;

        public FrameworkElement Build(ResourceDictionary res)
        {
            var ink = (Brush)res["Ink"]; var faint = (Brush)res["Faint"];
            _warnB = (Brush)res["Warn"]; _faintB = faint;
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = Label, Style = (Style)res["StatLabel"], VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) });
            var vp = new TextBlock { VerticalAlignment = VerticalAlignment.Bottom };
            _val = new TextBlock { FontFamily = (FontFamily)res["Display"], FontSize = 24, Foreground = ink, Text = "--" };
            _unit = new TextBlock { Text = Unit, FontSize = 12, Foreground = (Brush)res["Dim"], Margin = new Thickness(4, 0, 0, 3), VerticalAlignment = VerticalAlignment.Bottom };
            var vs = new StackPanel { Orientation = Orientation.Horizontal };
            vs.Children.Add(_val); vs.Children.Add(_unit);
            Grid.SetColumn(vs, 1); head.Children.Add(vs);

            _canvas = new Canvas { Height = 84, Margin = new Thickness(0, 10, 0, 0), Background = Brushes.Transparent, ClipToBounds = true, Cursor = Cursors.Cross };
            // baseline: the one recessive axis mark
            var baseLine = new Border { Height = 1, Background = (Brush)res["Hairline"], VerticalAlignment = VerticalAlignment.Bottom };
            _ref = new Line { Stroke = faint, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
            _refText = new TextBlock { Style = (Style)res["Caption"], Text = RefLabel, Visibility = Visibility.Collapsed };
            _line = new Path { Stroke = ink, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _cross = new Line { Stroke = faint, StrokeThickness = 1, Visibility = Visibility.Collapsed };
            _dot = new Ellipse { Width = 8, Height = 8, Fill = ink, Stroke = (Brush)res["Card"], StrokeThickness = 2, Visibility = Visibility.Collapsed };
            _canvas.Children.Add(_ref); _canvas.Children.Add(_refText); _canvas.Children.Add(_line); _canvas.Children.Add(_cross); _canvas.Children.Add(_dot);
            var plot = new Grid(); plot.Children.Add(_canvas); plot.Children.Add(baseLine);
            _canvas.SizeChanged += (s, e) => Render();
            _canvas.MouseMove += (s, e) => Hover(e.GetPosition(_canvas).X);
            _canvas.MouseLeave += (s, e) => { _hovering = false; _cross.Visibility = Visibility.Collapsed; _dot.Visibility = Visibility.Collapsed; UpdateText(); };

            _sub = new TextBlock { Style = (Style)res["Caption"], Margin = new Thickness(0, 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var stack = new StackPanel();
            stack.Children.Add(head); stack.Children.Add(plot); stack.Children.Add(_sub);
            var card = new Border { Style = (Style)res["Group"], Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(7), Child = stack,
                                    Focusable = true, FocusVisualStyle = (Style)res["FocusRing"] };
            System.Windows.Automation.AutomationProperties.SetName(card, Label + " chart, last 60 seconds. Left and right arrows step through it.");
            // Keyboard: arrows move the same read-out the mouse gives (Left = further back, Right = nearer now, Home/End).
            card.KeyDown += (s, e) =>
            {
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
            if (!double.IsNaN(Ref) && Ref > Min && Ref < Max)
            {
                var y = Math.Round(Y(Ref, h)) + 0.5;
                _ref.X1 = 0; _ref.X2 = w; _ref.Y1 = y; _ref.Y2 = y; _ref.Visibility = Visibility.Visible;
                _refText.Text = RefLabel; _refText.Visibility = string.IsNullOrEmpty(RefLabel) ? Visibility.Collapsed : Visibility.Visible;
                _refText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(_refText, Math.Max(0, w - _refText.DesiredSize.Width - 2));
                Canvas.SetTop(_refText, y - _refText.DesiredSize.Height - 1 < 0 ? y + 2 : y - _refText.DesiredSize.Height - 1);
            }
            else { _ref.Visibility = Visibility.Collapsed; _refText.Visibility = Visibility.Collapsed; }
            if (!_hovering) UpdateText();
        }

        void UpdateText()
        {
            var v = Latest;
            if (double.IsNaN(v)) { _val.Text = "--"; _val.Foreground = (Brush)_line.Stroke; _sub.Text = Idle(); _sub.Foreground = DimOf(); return; }
            bool hot = IsHot(v);
            _val.Text = Format(Show(v));
            _val.Foreground = hot ? WarnOf() : (Brush)_line.Stroke;
            var note = Note();
            _sub.Text = hot ? HotWord(v) + (string.IsNullOrEmpty(note) ? "" : " · " + note) : note;
            _sub.Foreground = hot ? WarnOf() : DimOf();
        }

        Brush _warnB, _faintB;
        Brush WarnOf() { return _warnB; }
        Brush DimOf() { return _faintB; }

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
            if (double.IsNaN(v)) { _dot.Visibility = Visibility.Collapsed; _sub.Text = (age == 0 ? "Now" : age + " s ago") + ": no reading"; _sub.Foreground = DimOf(); return; }
            Canvas.SetLeft(_dot, px - 4); Canvas.SetTop(_dot, Y(v, h) - 4); _dot.Visibility = Visibility.Visible;
            _sub.Text = (age == 0 ? "Now" : age + " s ago") + ": " + Format(Show(v)) + (Unit.Length > 0 ? " " + Unit.Trim() : "") + (IsHot(v) ? " · " + HotWord(v) : "");
            _sub.Foreground = IsHot(v) ? WarnOf() : (Brush)_line.Stroke;
        }
    }
}
