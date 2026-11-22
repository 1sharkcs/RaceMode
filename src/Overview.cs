using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace RaceMode
{
    // The Overview page, composed by moment.
    //
    //   Before a race (off): "am I ready?"  ->  switch card, and beside it the last session (how the last one went);
    //                                           the checklist reads "Before you race" further down.
    //   During (on):        "is anything wrong?"  ->  switch card, and beside it the checklist: what Race Mode did.
    //   After (off again, it turns itself off when iRacing closes): the last session is the first thing beside the switch.
    //
    // Then one live band for both: FPS as the lead chart, and the other readings as small multiples in one card,
    // a column per part (CPU, GPU, memory). Each number is shown once, with its last minute under it.
    // Under 1000 px of page the second piece goes under the switch card instead of beside it.
    public partial class Shell
    {
        const double OverMax = 2000;
        const double AsideAt = 1000;    // page width from which the moment's second piece sits beside the switch card
        const double LiveBesideAt = 680;    // dashboard width from which FPS sits beside the small multiples
        bool _overHooked;
        string _overKey = "";

        bool SwitchOn() { var sw = Get<ToggleButton>("Switch"); return sw != null && sw.IsChecked == true; }

        // Turning the switch changes what the page leads with, so re-compose on it (and let the checklist fade in
        // once, the same 180 ms ease-out the switch card's proof used; instant with Windows animations off).
        void HookOverview()
        {
            if (_overHooked) return;
            var sw = Get<ToggleButton>("Switch"); if (sw == null) return;
            _overHooked = true;
            RoutedEventHandler h = (s, e) =>
            {
                ApplyLayout(); UpdateChecklist();
                var aside = Get<Border>("OverAside");
                if (aside != null && aside.IsVisible && SystemParameters.ClientAreaAnimation)
                    aside.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                    { EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 } });
            };
            sw.Checked += h; sw.Unchecked += h;
        }

        static void Detach(FrameworkElement el)
        {
            var b = el.Parent as Border; if (b != null) { b.Child = null; return; }
            var p = el.Parent as Panel; if (p != null) p.Children.Remove(el);
        }

        void ArrangeOverview(bool overview, bool small, double page)
        {
            var top = Get<Grid>("OverTop"); var aside = Get<Border>("OverAside");
            var check = Get<FrameworkElement>("CheckBlock"); var last = Get<FrameworkElement>("LastBlock");
            var bottom = Get<UniformGrid>("DashBottom");
            if (top == null || aside == null || check == null || last == null || bottom == null) return;
            HookOverview();
            // very wide windows: the page stops growing at OverMax (the same cap is on its column in the XAML)
            page = Math.Min(page, OverMax);
            // one name in both states: while off the rows say what will happen, while on what did
            var head = Get<TextBlock>("CheckHead"); if (head != null) head.Text = "What Race Mode does";

            bool page2 = overview && !small;
            bool beside = page2 && page >= AsideAt;
            // The steps always lead: beside the switch card when there's room, under it otherwise. The last session
            // goes under the switch card when the steps are beside it (that corner would otherwise be empty), and
            // under the live band everywhere else. With no session yet there is nothing to show, so it isn't shown.
            string key = !page2 ? "-" : beside ? "beside" : "stack";
            if (key != _overKey)
            {
                _overKey = key;
                Detach(check); Detach(last);
                var bottomEdge = new Thickness(7, 0, 7, 14);
                if (!page2)
                {
                    check.Margin = bottomEdge; last.Margin = bottomEdge;
                    bottom.Children.Add(check); bottom.Children.Add(last);
                }
                else
                {
                    aside.Child = check; check.Margin = new Thickness(0);
                    if (beside) { last.Margin = new Thickness(0, 18, 0, 0); Grid.SetRow(last, 1); Grid.SetColumn(last, 0); top.Children.Add(last); }
                    else { last.Margin = bottomEdge; bottom.Children.Add(last); }
                }
            }
            last.Visibility = string.IsNullOrEmpty(_lastSummary) ? Visibility.Collapsed : Visibility.Visible;
            aside.Visibility = page2 ? Visibility.Visible : Visibility.Collapsed;

            top.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            Get<ColumnDefinition>("StatsCol").Width = beside ? new GridLength(1.1, GridUnitType.Star) : new GridLength(0);
            Grid.SetRow(aside, beside ? 0 : 1); Grid.SetColumn(aside, beside ? 1 : 0); Grid.SetRowSpan(aside, beside ? 2 : 1);
            aside.Margin = beside ? new Thickness(20, 0, 0, 0) : new Thickness(0, 20, 0, 0);
            // beside, the switch card starts level with the list's card (under the list's heading) and keeps its own
            // height: stretched to the list's height it was the biggest red surface on the page and said the least
            var hero = Get<Border>("Hero");
            if (hero != null) { hero.Margin = new Thickness(0, beside ? 23 : 0, 0, 0); hero.VerticalAlignment = beside ? VerticalAlignment.Top : VerticalAlignment.Stretch; }

            // the steps run in two columns wherever they have the room (under the live band, ArrangeLive decides)
            if (page2) SetStepColumns((beside ? page * 1.1 / 2.1 : page) >= 500 ? 2 : 1);
        }

        // How much taller the steps make the top of the page than the old checklist the plot heights were tuned to
        // (about 130 px): the plots give that height back so the band still fits the window.
        double StepsExtra(double dashW)
        {
            if (_overKey == "-" || _overKey == "" || _stepRows == null) return 0;
            double a = _stepRows.Where(r => r.Btn.Parent == _stepColA).Sum(r => r.Weight);
            double b = _stepRows.Where(r => r.Btn.Parent == _stepColB).Sum(r => r.Weight);
            return Math.Max(0, Math.Max(a, b) * (_compact ? 40 : 46) + 16 - 130);
        }

        // The live band: FPS leads in its own card; the small multiples share one card, divided by hairlines.
        static readonly string[] Order3 = { "Busiest CPU core", "GPU", "Free RAM", "CPU temp", "GPU temp", "Video memory" };
        static readonly string[] Order2 = { "Busiest CPU core", "CPU temp", "GPU", "GPU temp", "Video memory", "Free RAM" };
        void ArrangeLive(bool overview, double dashW, double winH)
        {
            if (!_chartsBuilt || _charts == null) return;
            var grid = Get<UniformGrid>("ChartGrid"); var card = Get<Border>("SmallCard");
            var fpsCol = Get<ColumnDefinition>("FpsCol"); var smallCol = Get<ColumnDefinition>("SmallCol");
            if (grid == null || card == null || fpsCol == null || smallCol == null) return;

            // no FPS for a few seconds (FeedDashboard): FPS is a short strip on top and the other readings take the
            // full width; the small panel keeps its own FPS chart as it is
            bool strip = _fpsIdle && !_small;
            _fpsChart.SetStrip(strip);
            bool side = !strip && dashW >= LiveBesideAt;
            fpsCol.Width = new GridLength(1, GridUnitType.Star);
            smallCol.Width = side ? new GridLength(2.1, GridUnitType.Star) : new GridLength(0);
            Grid.SetRow(card, side ? 0 : 1); Grid.SetColumn(card, side ? 1 : 0);
            card.Margin = side ? new Thickness(12, 0, 0, 0) : new Thickness(0, strip ? 10 : 12, 0, 0);
            // beside, FPS fills the small multiples' height; stacked, it gets a plot of its own (shorter in short windows)
            _fpsChart.SetPlotHeight(side ? double.NaN : winH < 760 ? 96 : 120);

            // the steps under the live band (Overview off, or beside another section): two columns when there's room
            var bottom = Get<UniformGrid>("DashBottom");
            if ((_overKey == "-" || _overKey == "") && bottom != null) SetStepColumns(dashW / Math.Max(1, bottom.Columns) >= 560 ? 2 : 1);

            double smallW = side ? (dashW - 12) * 2.1 / 3.1 : dashW;
            int cols = smallW >= 600 ? 3 : smallW >= 340 ? 2 : 1;
            var order = cols == 3 ? Order3 : Order2;
            var tiles = order.Select(n => _charts.FirstOrDefault(c => c.Label == n)).Where(c => c != null && c.Cell != null).ToList();
            bool reorder = grid.Columns != cols || grid.Children.Count != tiles.Count;
            for (int i = 0; i < tiles.Count && !reorder; i++) if (grid.Children[i] != tiles[i].Cell) reorder = true;
            grid.Columns = cols;
            if (reorder) { grid.Children.Clear(); foreach (var t in tiles) grid.Children.Add(t.Cell); }
            int rows = (tiles.Count + cols - 1) / cols;
            // tall windows: the plots grow into the height instead of leaving it empty under the page
            double chrome = 96;   // a cell's label, number and note around its plot
            double room = overview ? winH - 420 - (cols == 3 ? 0 : 60) - (dashW < AsideAt ? 150 : 0) - (strip ? 50 : 0) - StepsExtra(dashW) : 0;   // less when the list sits under the switch card
            double plot = Math.Max(44, Math.Min(240, Math.Floor(room / rows - chrome)));
            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].SetPlotHeight(plot);
                int c = i % cols, r = i / cols;
                tiles[i].Cell.BorderThickness = new Thickness(0, 0, c < cols - 1 ? 1 : 0, r < rows - 1 ? 1 : 0);
            }
        }
    }
}
