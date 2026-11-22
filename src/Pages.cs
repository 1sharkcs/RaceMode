using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RaceMode
{
    // How the sections (Launch to Logs) use the page they're given. The XAML caps every section at 720 px of
    // content, so rows and descriptions stay readable; this only handles what depends on the page's size:
    //   Settings  two columns of groups from 900 px of page (race on the left, app and Windows on the right);
    //             one column with a short index of its groups under that
    //   Logs      the logging options beside the last session from 900 px, under it otherwise; with a real
    //             summary the summary takes the page's height
    // Called from the end of ApplyLayout with the open section's width.
    public partial class Shell
    {
        const double TwoColAt = 900;
        bool _pagesWired;

        void ApplyPages(double page)
        {
            if (!_pagesWired) WirePages();
            bool two = page >= TwoColAt;

            // ---- Settings ----
            var a = Get<ColumnDefinition>("SetColADef"); var b = Get<ColumnDefinition>("SetColBDef");
            var colA = Get<FrameworkElement>("SetColA"); var colB = Get<FrameworkElement>("SetColB");
            if (a != null && b != null && colA != null && colB != null)
            {
                a.MaxWidth = two ? double.PositiveInfinity : 720;
                b.Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                Grid.SetRow(colB, two ? 0 : 1); Grid.SetColumn(colB, two ? 1 : 0);
                colA.Margin = two ? new Thickness(0, 0, 12, 0) : new Thickness(0);
                colB.Margin = two ? new Thickness(12, 0, 0, 0) : new Thickness(0, 22, 0, 0);
            }
            var index = Get<FrameworkElement>("SetIndex");
            if (index != null) index.Visibility = two ? Visibility.Collapsed : Visibility.Visible;

            // ---- Logs ----
            var main = Get<ColumnDefinition>("LogMainCol"); var side = Get<ColumnDefinition>("LogSideCol");
            var hdr = Get<FrameworkElement>("LogOptsHdr"); var opts = Get<FrameworkElement>("LogOpts");
            if (main != null && side != null && hdr != null && opts != null)
            {
                main.MaxWidth = two ? double.PositiveInfinity : 720;
                side.Width = two ? new GridLength(320) : new GridLength(0);
                Grid.SetColumn(hdr, two ? 1 : 0); Grid.SetRow(hdr, two ? 0 : 2);
                Grid.SetColumn(opts, two ? 1 : 0); Grid.SetRow(opts, two ? 1 : 3);
                hdr.VerticalAlignment = two ? VerticalAlignment.Center : VerticalAlignment.Top;
                hdr.Margin = two ? new Thickness(40, 0, 0, 8) : new Thickness(16, 22, 0, 7);
                opts.Margin = two ? new Thickness(24, 0, 0, 0) : new Thickness(0);
            }
            FitSummaryRow();

            // ---- Close: the add bar is one row, or two when the page can't hold the box and three buttons ----
            var box = Get<FrameworkElement>("NewCloseBox"); var hint = Get<FrameworkElement>("NewCloseHint");
            var add = Get<FrameworkElement>("AddCloseBtn"); var pick = Get<FrameworkElement>("PickRunningBtn"); var remove = Get<FrameworkElement>("RemoveCloseBtn");
            if (box != null && hint != null && add != null && pick != null && remove != null)
            {
                bool rows2 = page < 500;
                Grid.SetColumnSpan(box, rows2 ? 3 : 1); Grid.SetColumnSpan(hint, rows2 ? 3 : 1);
                Grid.SetColumn(add, rows2 ? 3 : 1);
                Grid.SetRow(pick, rows2 ? 1 : 0); Grid.SetColumn(pick, rows2 ? 0 : 2); Grid.SetColumnSpan(pick, rows2 ? 4 : 1);
                pick.HorizontalAlignment = rows2 ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
                pick.Margin = rows2 ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
                Grid.SetRow(remove, rows2 ? 1 : 0); Grid.SetColumn(remove, rows2 ? 0 : 3); Grid.SetColumnSpan(remove, rows2 ? 4 : 1);
                remove.HorizontalAlignment = rows2 ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
                remove.Margin = rows2 ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
            }
        }

        // The summary takes the page's height once there is one (ShowSummary enables Copy Summary when it fills the box).
        void FitSummaryRow()
        {
            var row = Get<RowDefinition>("LogSumRow"); var copy = Get<Button>("CopySummaryBtn");
            if (row == null || copy == null) return;
            row.Height = copy.IsEnabled ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }

        void WirePages()
        {
            _pagesWired = true;
            var copy = Get<Button>("CopySummaryBtn");
            if (copy != null) copy.IsEnabledChanged += (s, e) => FitSummaryRow();

            // Settings index: each link scrolls its group's header to the top of the page
            var index = Get<Panel>("SetIndexLinks"); var scroll = Get<ScrollViewer>("SetScroll");
            if (index == null || scroll == null) return;
            foreach (var child in index.Children)
            {
                var link = child as Button; if (link == null) continue;
                link.Click += (s, e) =>
                {
                    var target = Get<FrameworkElement>((string)((Button)s).Tag);
                    if (target == null || !target.IsVisible) return;
                    double y = target.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
                    scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + y));
                };
            }
        }
    }
}
