using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RaceMode
{
    // The racing-screen picker: a picture of the screens as Windows arranges them (Settings > Display), the one you
    // race on marked, an "I race on three screens" tick box, and "Don't check my screen".
    // The choice is a number in the hidden MonitorPick list's order (FillMonitorPick): 0 = don't check,
    // 1..n = one screen, then the rows of three, then "not connected right now". Settings applies a click straight
    // away; first-run setup keeps a draft and applies it on Next.
    public partial class Shell
    {
        // Settings: the picture sits under the card's words and follows the hidden list.
        void HostScreenPicker()
        {
            var host = Get<ContentControl>("MonitorMapHost");
            var box = Get<ComboBox>("MonitorPick");
            if (host == null || box == null) return;
            Action rebuild = () =>
            {
                FillMonitorPick();
                host.Content = BuildScreenPicker(() => box.SelectedIndex, i => { if (box.SelectedIndex != i) box.SelectedIndex = i; }, null);
            };
            rebuild();
            // screens come and go: look again each time Settings is opened
            host.IsVisibleChanged += (s, e) => { if (host.IsVisible) rebuild(); };
        }

        FrameworkElement BuildScreenPicker(Func<int> get, Action<int> set, string why)
        {
            var root = new StackPanel();
            var map = new Canvas { Height = 132, ClipToBounds = true };
            var mapBox = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0), Child = map };
            mapBox.SetResourceReference(Border.BackgroundProperty, "Field");
            AutomationProperties.SetName(mapBox, "Your screens");
            root.Children.Add(mapBox);

            var status = new TextBlock { Style = SetupFlow.Res<Style>("Sub"), Margin = new Thickness(2, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
            root.Children.Add(status);

            CheckBox three, off;
            var threeRow = (FrameworkElement)SetupFlow.CheckRow("I race on three screens side by side", "", false, out three);
            threeRow.Margin = new Thickness(0, 14, 0, 0);
            root.Children.Add(threeRow);
            var offRow = (FrameworkElement)SetupFlow.CheckRow("Don't check my screen", "Leave iRacing's screen settings alone.", false, out off);
            offRow.Margin = new Thickness(0, 12, 0, 0);
            root.Children.Add(offRow);

            int lastOn = 0;          // the choice before "Don't check" was ticked, to come back to
            bool wantThree = false;  // ticked: clicking a screen picks the row of three around it
            bool userTouched = false; // the tick box was changed here (a missing saved row no longer decides it)
            bool busy = false;
            Action render = null;

            Func<int> n = () => _pickMonitors.Count;
            Func<int, bool> isSingle = i => i >= 1 && i <= n();
            Func<int, bool> isTriple = i => i > n() && i <= n() + _pickTriples.Count;
            // a screen's row of three: the one it sits in the middle of, else the first it's part of
            Func<MonitorInfo, int> rowFor = m =>
            {
                int k = _pickTriples.FindIndex(r => r[1] == m);
                if (k < 0) k = _pickTriples.FindIndex(r => r.Contains(m));
                return k;
            };
            Action<int> choose = i =>
            {
                set(i);
                if (i > 0) lastOn = i;
                render();
            };

            render = () =>
            {
                busy = true;
                try
                {
                    int i = get();
                    bool missing = i > n() + _pickTriples.Count;   // the saved choice isn't connected right now
                    bool savedThree = _cfg.DisplayGuard != null && _cfg.DisplayGuard.IsTriple;
                    if (isTriple(i)) wantThree = true;
                    else if (isSingle(i)) wantThree = false;
                    else if (missing && !userTouched) wantThree = savedThree;
                    if (i > 0) lastOn = i;
                    var mons = _pickMonitors;
                    MonitorInfo single = isSingle(i) ? mons[i - 1] : null;
                    List<MonitorInfo> row = isTriple(i) ? _pickTriples[i - 1 - n()] : null;

                    // the tick boxes
                    three.IsChecked = wantThree;
                    three.IsEnabled = _pickTriples.Count > 0 || wantThree || (missing && savedThree);
                    off.IsChecked = i == 0;
                    var threeSub = ((Panel)((Grid)threeRow).Children[1]).Children.OfType<TextBlock>().Skip(1).FirstOrDefault();
                    bool joined = mons.Any(DisplayGuard.LooksJoined);
                    string tsub = _pickTriples.Count == 0
                        ? (joined ? "Three screens joined by NVIDIA Surround or AMD Eyefinity count as one screen: pick that one."
                           : mons.Count < 3 ? "Windows sees " + (mons.Count == 1 ? "one screen" : mons.Count + " screens") + " right now." : "Race Mode needs three screens next to each other in Windows' display settings.")
                        : "iRacing runs as one borderless window across all three.";
                    if (threeSub == null) ((Panel)((Grid)threeRow).Children[1]).Children.Add(new TextBlock { Text = tsub, Style = SetupFlow.Res<Style>("Sub"), TextWrapping = TextWrapping.Wrap });
                    else threeSub.Text = tsub;

                    // the picture (rebuilt each time; the tile that had the keyboard gets it back below)
                    MonitorInfo focused = null;
                    foreach (var c in map.Children.OfType<Button>()) if (c.IsKeyboardFocusWithin) focused = c.Tag as MonitorInfo;
                    map.Children.Clear();
                    if (mons.Count > 0 && map.ActualWidth > 0)
                    {
                        int left = mons.Min(m => m.X), top = mons.Min(m => m.Y);
                        int right = mons.Max(m => m.X + m.Width), bottom = mons.Max(m => m.Y + m.Height);
                        double scale = Math.Min(map.ActualWidth / Math.Max(1, right - left), map.Height / Math.Max(1, bottom - top));
                        double ox = (map.ActualWidth - (right - left) * scale) / 2, oy = (map.Height - (bottom - top) * scale) / 2;
                        foreach (var m in mons)
                        {
                            var mon = m;
                            string role = single == m ? "iRacing" : row == null ? null : row[0] == m ? "Left" : row[1] == m ? "Middle" : row[2] == m ? "Right" : null;
                            bool canPick = !wantThree || rowFor(m) >= 0;   // with three ticked, only screens in a row of three
                            var tile = ScreenTile(m, role, canPick, (m.Width) * scale - 4, (m.Height) * scale - 4);
                            Canvas.SetLeft(tile, ox + (m.X - left) * scale + 2);
                            Canvas.SetTop(tile, oy + (m.Y - top) * scale + 2);
                            tile.Click += (s, e) =>
                            {
                                if (wantThree)
                                {
                                    int k = rowFor(mon);
                                    if (k >= 0) choose(1 + n() + k);
                                }
                                else choose(1 + mons.IndexOf(mon));
                            };
                            tile.Tag = m;
                            map.Children.Add(tile);
                            if (focused == m && tile.IsEnabled) { var t = tile; t.Dispatcher.BeginInvoke(new Action(() => t.Focus()), System.Windows.Threading.DispatcherPriority.Input); }
                        }
                    }
                    else if (mons.Count == 0)
                        map.Children.Add(new TextBlock { Text = "Couldn't read your screens from Windows.", Style = SetupFlow.Res<Style>("Sub") });

                    // what happens, in words
                    string say;
                    if (i == 0) say = "Race Mode leaves iRacing's screen settings alone.";
                    else if (single != null)
                        say = "iRacing opens on your " + single.Width + " × " + single.Height + " screen" + Where(single, " (", ")") + " as a borderless window filling it."
                            + (DisplayGuard.LooksJoined(single) ? " It looks like three screens joined by NVIDIA Surround or AMD Eyefinity, so iRacing fills all three." : "")
                            + (mons.Count > 1 ? " Click another screen to change it." : "");
                    else if (row != null)
                    {
                        int x, y, w, h; DisplayGuard.TripleWindow(row, out x, out y, out w, out h);
                        say = "iRacing spans your three screens, left to right, as one " + w + " × " + h + " borderless window.";
                        bool sameHeights = row.All(m => m.Height == row[0].Height);
                        if (!sameHeights)
                            say += " Your screens aren't the same height, so it uses the " + h + " pixels they share; the extra height of the taller screens stays dark.";
                        else if (row.Any(m => m.Y != y))
                            say += " Your screens aren't lined up in Windows' display settings, so it uses the " + h + " pixels they share. Line them up in Settings > Display to use all of them.";
                        else if (!DisplayGuard.TripleMatches(row))
                            say += " Your screens are different widths; iRacing's three-screen view works best when all three match.";
                        if (DisplayGuard.IniNumMonitors(IniPath()) != 3)
                            say += " Then, in the iRacing sim (not Windows), open its monitor settings and set it up for 3 screens.";
                    }
                    else say = savedThree
                        ? "Your three racing screens aren't all connected side by side right now. Race Mode leaves iRacing's screen settings alone until they're back. Untick the box to race on one screen instead."
                        : "Your racing screen isn't connected right now. Click a screen to race on it instead, or leave it until it's back.";
                    status.Text = (string.IsNullOrEmpty(why) ? "" : why + " ") + say;
                }
                finally { busy = false; }
            };

            three.Checked += (s, e) =>
            {
                if (busy) return;
                wantThree = true; userTouched = true;
                int i = get();
                if (isTriple(i) || _pickTriples.Count == 0) { render(); return; }
                // keep the screen they had in the middle when it has neighbours on both sides
                int k = isSingle(i) ? rowFor(_pickMonitors[i - 1]) : -1;
                choose(1 + n() + (k >= 0 ? k : 0));
            };
            three.Unchecked += (s, e) =>
            {
                if (busy) return;
                wantThree = false; userTouched = true;
                int i = get();
                if (isTriple(i)) choose(1 + _pickMonitors.IndexOf(_pickTriples[i - 1 - n()][1]));
                else render();
            };
            off.Checked += (s, e) => { if (!busy) choose(0); };
            off.Unchecked += (s, e) =>
            {
                if (busy) return;
                int back = lastOn > 0 && lastOn <= n() + _pickTriples.Count ? lastOn : (n() > 0 ? 1 + SuggestRacingMonitor() : 0);
                choose(back);
            };
            map.SizeChanged += (s, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) render(); };
            render();
            return root;
        }

        // Where a screen sits, in words: "main screen", "left of main"... (empty when it can't be said simply)
        string Where(MonitorInfo m, string before, string after)
        {
            var main = _pickMonitors == null ? null : _pickMonitors.FirstOrDefault(x => x.Primary);
            string w = m.Primary ? "main screen" : main == null ? ""
                : m.X + m.Width <= main.X ? "left of main" : m.X >= main.X + main.Width ? "right of main"
                : m.Y + m.Height <= main.Y ? "above main" : m.Y >= main.Y + main.Height ? "below main" : "";
            return w.Length == 0 ? "" : before + w + after;
        }

        // One screen in the picture: its size and refresh rate, and what it's for ("iRacing", "Left"...).
        Button ScreenTile(MonitorInfo m, string role, bool canPick, double w, double h)
        {
            bool on = role != null;
            var face = new Border { Width = Math.Max(8, w), Height = Math.Max(8, h), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(on ? 2 : 1) };
            face.SetResourceReference(Border.BackgroundProperty, on ? "ScreenTileOn" : "ScreenTile");
            face.SetResourceReference(Border.BorderBrushProperty, on ? "Ink" : "ScreenTileEdge");
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            // the size leads (Windows' own screen numbers aren't available to apps, so none are shown)
            var num = new TextBlock { Text = m.Width + " × " + m.Height, FontFamily = SetupFlow.Res<FontFamily>("Display"), FontSize = h >= 44 && w >= 90 ? 15 : 11, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            num.SetResourceReference(TextBlock.ForegroundProperty, on ? "SegOnInk" : "Dim");
            words.Children.Add(num);
            if (h >= 52 && w >= 70)
            {
                var size = new TextBlock { Text = m.Hz + " Hz" + (m.Primary ? " · main" : ""), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                size.SetResourceReference(TextBlock.ForegroundProperty, on ? "SegOnInk" : "Faint");   // on the picked tile, its own ink (light theme)
                if (on) size.Opacity = 0.75;
                words.Children.Add(size);
            }
            if (on && h >= 40)
            {
                var tag = new TextBlock { Text = role, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
                tag.SetResourceReference(TextBlock.ForegroundProperty, "SegOnInk");
                words.Children.Add(tag);
            }
            face.Child = words;
            var b = new Button { Content = face, Cursor = canPick ? Cursors.Hand : null, IsEnabled = canPick, Opacity = canPick ? 1 : 0.45 };
            var t = new ControlTemplate(typeof(Button)); t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)); b.Template = t;
            b.FocusVisualStyle = SetupFlow.Res<Style>("FocusRing");
            if (!on)
            {
                b.MouseEnter += (s, e) => face.SetResourceReference(Border.BackgroundProperty, "ScreenTileHover");
                b.MouseLeave += (s, e) => face.SetResourceReference(Border.BackgroundProperty, "ScreenTile");
            }
            string what = m.Width + " by " + m.Height + " screen, " + m.Hz + " hertz" + Where(m, ", ", "");
            AutomationProperties.SetName(b, what + (on ? ", " + (role == "iRacing" ? "iRacing opens here" : role + " racing screen") : ""));
            if (!canPick) AutomationProperties.SetHelpText(b, "Not next to two other screens, so it can't be one of three.");
            ToolTipService.SetShowOnDisabled(b, true);   // the tooltip says why a greyed-out screen can't be picked
            b.ToolTip = what + (on ? "" : canPick ? ". Click to race on it." : ". Not next to two other screens.");
            return b;
        }
    }
}
