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
            bool on = SwitchOn();
            var head = Get<TextBlock>("CheckHead"); if (head != null) head.Text = on ? "Race Mode checklist" : "Before you race";

            bool page2 = overview && !small;
            bool beside = page2 && page >= AsideAt;
            FrameworkElement lead = page2 ? (on ? check : last) : null;
            string key = (lead == null ? "-" : lead.Name);
            if (key != _overKey)
            {
                _overKey = key;
                Detach(check); Detach(last);
                var bottomEdge = new Thickness(7, 0, 7, 14);
                if (lead != null)
                {
                    var rest = lead == check ? last : check;
                    aside.Child = lead; lead.Margin = new Thickness(0);
                    rest.Margin = bottomEdge; bottom.Children.Add(rest);
                }
                else
                {
                    check.Margin = bottomEdge; last.Margin = bottomEdge;
                    bottom.Children.Add(check); bottom.Children.Add(last);
                }
            }
            aside.Visibility = page2 ? Visibility.Visible : Visibility.Collapsed;

            // beside: the switch card a little wider than the list; it stretches to the list's height
            top.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            Get<ColumnDefinition>("StatsCol").Width = beside ? new GridLength(1.1, GridUnitType.Star) : new GridLength(0);
            Grid.SetRow(aside, beside ? 0 : 1); Grid.SetColumn(aside, beside ? 1 : 0);
            aside.Margin = beside ? new Thickness(20, 0, 0, 0) : new Thickness(0, 20, 0, 0);
            // beside, the switch card starts level with the list's card, under the list's heading
            var hero = Get<Border>("Hero"); if (hero != null) hero.Margin = new Thickness(0, beside ? 23 : 0, 0, 0);

            // the checklist runs in two columns wherever it has the room
            var list = Get<UniformGrid>("Checklist");
            if (list != null)
            {
                double w = lead == check ? (beside ? page * 1.1 / 2.1 : page) : 0;
                list.Columns = w >= 500 ? 2 : 1;
            }
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

            bool side = dashW >= LiveBesideAt;
            fpsCol.Width = new GridLength(1, GridUnitType.Star);
            smallCol.Width = side ? new GridLength(2.1, GridUnitType.Star) : new GridLength(0);
            Grid.SetRow(card, side ? 0 : 1); Grid.SetColumn(card, side ? 1 : 0);
            card.Margin = side ? new Thickness(12, 0, 0, 0) : new Thickness(0, 12, 0, 0);
            // beside, FPS fills the small multiples' height; stacked, it gets a plot of its own (shorter in short windows)
            _fpsChart.SetPlotHeight(side ? double.NaN : winH < 760 ? 96 : 120);

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
            double room = overview ? winH - 420 - (cols == 3 ? 0 : 60) - (dashW < AsideAt ? 150 : 0) : 0;   // less when the list sits under the switch card
            double plot = Math.Max(44, Math.Min(240, Math.Floor(room / rows - chrome)));
            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].SetPlotHeight(plot);
                int c = i % cols, r = i / cols;
                tiles[i].Cell.BorderThickness = new Thickness(0, 0, c < cols - 1 ? 1 : 0, r < rows - 1 ? 1 : 0);
            }
        }

        // --snapshot ... on: the same made-up "all good" state the switch card shows, with the real app names.
        void PreviewChecklist(Panel list)
        {
            foreach (var item in _launch.Where(i => i.Enabled && !string.IsNullOrEmpty(i.ProcessName)).Take(3))
                list.Children.Add(CheckRow(item.Name, "running", "Live"));
            list.Children.Add(CheckRow("iRacing", "racing, put first", "Live"));
            list.Children.Add(CheckRow("Close list", "all closed", "Live"));
            list.Children.Add(CheckRow("Screen", "OK", "Live"));
            list.Children.Add(CheckRow("Race graphics", "set", "Live"));
            list.Children.Add(CheckRow("Background apps", "5 slowed down", "Live"));
        }
    }
}
