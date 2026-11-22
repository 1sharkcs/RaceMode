using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RaceMode
{
    // RaceMode.exe --uitest <report.txt>
    //
    // What the layout test can't see: how the window behaves when it is used. Off-screen, and without turning
    // Race Mode on or touching any process, it checks:
    //   names      every control a screen reader can reach has a name, at five window sizes and every section
    //   tab order  Tab reaches every visible control in each section exactly once, and where it goes first
    //   small      the Small panel button shrinks the window and brings back the size and section it had
    //   sections   asking for a section from the small panel opens the full window on that section
    //   links      each Settings jump link scrolls its group to the top
    //   focus      the keyboard is never left on a control the layout has just hidden
    // PASS / FAIL lines, then the Tab order of every section for reading.
    public partial class Shell
    {
        public void RunUiTest(string outPath)
        {
            var sb = new StringBuilder(); int fails = 0;
            Action<bool, string> check = (ok, what) => { if (!ok) fails++; sb.AppendLine((ok ? "PASS  " : "FAIL  ") + what); };
            var tabs = Get<TabControl>("Tabs");
            var setup = Get<UIElement>("SetupOverlay");
            if (tabs == null || (setup != null && setup.Visibility == Visibility.Visible))
            { File.WriteAllText(outPath, "Race Mode UI test: not run. First-run setup is showing; use a data folder whose config.json has SetupDone true."); return; }
            try
            {
                // ---- names ----
                var unnamed = new Dictionary<string, string>();
                foreach (var size in new[] { new Size(960, 720), new Size(660, 700), new Size(460, 262), new Size(480, 900), new Size(1300, 200), new Size(1600, 900) })
                {
                    Window.Width = size.Width; Window.Height = size.Height; Pump();
                    int n = _small ? 1 : tabs.Items.Count;
                    for (int s = 0; s < n; s++)
                    {
                        if (!_small) tabs.SelectedIndex = s;
                        Pump();
                        string where = (_small ? "small panel" : (string)((TabItem)tabs.Items[s]).Header) + " at " + size.Width + "x" + size.Height;
                        var peer = UIElementAutomationPeer.CreatePeerForElement(Window);
                        if (peer != null) Unnamed(peer, where, unnamed, 0);
                    }
                }
                check(unnamed.Count == 0, "names: every reachable control has a name" + (unnamed.Count == 0 ? "" : " (" + unnamed.Count + " without)"));
                foreach (var kv in unnamed) sb.AppendLine("        no name: " + kv.Key + "   first seen in " + kv.Value);

                // ---- tab order ----
                Window.Width = 960; Window.Height = 720; Pump();
                var orders = new StringBuilder();
                for (int s = 0; s < tabs.Items.Count; s++)
                {
                    tabs.SelectedIndex = s; Pump();
                    string sec = (string)((TabItem)tabs.Items[s]).Header;
                    var order = TabWalk();
                    if (order == null) { sb.AppendLine("SKIP  tab order, " + sec + ": WPF's Tab logic couldn't be reached on this .NET version"); continue; }
                    // a list is one stop that the arrow keys move within: its rows count once
                    var names = new List<string>();
                    foreach (var stop in order.Select(DescribeStop)) if (names.Count == 0 || names[names.Count - 1] != stop) names.Add(stop);
                    var twice = names.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                    var want = TabStops().Select(DescribeStop).Distinct().ToList();
                    var missed = want.Where(x => !names.Contains(x)).ToList();
                    check(twice.Count == 0, "tab order, " + sec + ": nothing is visited twice" + (twice.Count == 0 ? "" : " (" + string.Join(", ", twice) + ")"));
                    check(missed.Count == 0, "tab order, " + sec + ": every visible control is reached" + (missed.Count == 0 ? "" : " (missed: " + string.Join(", ", missed.Take(12)) + ")"));
                    // the menu: its switch block at the top, then the sections, then its collapse button; then the page
                    int nav = names.IndexOf("Nav"), firstBody = names.FindIndex(x => x != "Nav" && x != "NavSwitch" && x != "NavCollapse" && !x.EndsWith("Btn (title bar)", StringComparison.Ordinal));
                    int sw = names.IndexOf("NavSwitch"), fold = names.IndexOf("NavCollapse");
                    // the Overview has its own big switch, so the menu's switch is hidden there and the section list leads
                    bool home = tabs.SelectedIndex == 0;
                    check(home ? sw < 0 : sw == 0, "tab order, " + sec + (home ? ": the menu's switch is hidden (the page has the big one)" : ": the menu's switch is the first stop"));
                    check(nav >= 0 && nav == (home ? 0 : sw + 1), "tab order, " + sec + (home ? ": the section list is the first stop" : ": the section list comes straight after the menu's switch"));
                    check(fold >= 0 && fold == nav + 1, "tab order, " + sec + ": the collapse button comes straight after the section list");
                    check(nav >= 0 && (firstBody < 0 || Math.Max(nav, fold) < firstBody), "tab order, " + sec + ": the menu comes before the section");
                    int firstBar = names.FindIndex(x => x.EndsWith("(title bar)", StringComparison.Ordinal));
                    check(firstBar >= 0 && names.Skip(firstBar).All(x => x.EndsWith("(title bar)", StringComparison.Ordinal)), "tab order, " + sec + ": the title bar's buttons come last");
                    orders.AppendLine(sec + ": " + string.Join(" > ", names));
                }

                // ---- the Small panel button, and a section asked for from the small panel ----
                tabs.SelectedIndex = 5; Window.Width = 1100; Window.Height = 760; Pump();
                ToggleSmall(); Pump();
                check(_small && Math.Abs(Window.ActualWidth - PanelW) < 2 && Math.Abs(Window.ActualHeight - PanelH) < 2, "small: the button shrinks the window to " + PanelW + "x" + PanelH + " (got " + Math.Round(Window.ActualWidth) + "x" + Math.Round(Window.ActualHeight) + ")");
                check(tabs.SelectedIndex == 0, "small: the small panel shows the Overview");
                var nr = Get<FrameworkElement>("NavRail");
                check(nr != null && !nr.IsVisible, "small: the menu is hidden");
                ToggleSmall(); Pump();
                check(!_small && Math.Abs(Window.ActualWidth - 1100) < 2 && Math.Abs(Window.ActualHeight - 760) < 2, "small: the button brings back 1100x760 (got " + Math.Round(Window.ActualWidth) + "x" + Math.Round(Window.ActualHeight) + ")");
                check(tabs.SelectedIndex == 5, "small: and the section that was open (Settings)");
                ToggleSmall(); Pump();
                GoToSection(3); Pump();
                check(!_small && tabs.SelectedIndex == 3, "sections: asking for Stream from the small panel opens the full window on it");
                Window.Width = 460; Window.Height = 300; Pump();
                tabs.SelectedIndex = 4; Pump();
                check(tabs.SelectedIndex == 0, "sections: the small panel can't be switched to another section behind the user's back");

                // ---- Settings jump links ----
                Window.Width = 660; Window.Height = 700; Pump();
                tabs.SelectedIndex = 5; Pump();
                var links = Get<Panel>("SetIndexLinks"); var scroll = Get<ScrollViewer>("SetScroll");
                if (links == null || scroll == null) check(false, "links: the Settings index exists");
                else
                    foreach (var link in links.Children.OfType<Button>())
                    {
                        scroll.ScrollToVerticalOffset(0); Pump();
                        link.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
                        var target = Get<FrameworkElement>(link.Tag as string);
                        double y = target == null ? double.NaN : target.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
                        bool atEnd = scroll.VerticalOffset >= scroll.ScrollableHeight - 1;
                        check(target != null && (Math.Abs(y) < 24 || (atEnd && y >= 0 && y < scroll.ViewportHeight)), "links: \"" + link.Content + "\" brings its group into view (top at " + (double.IsNaN(y) ? "?" : Math.Round(y).ToString()) + " px)");
                    }

                // ---- focus is never left on something hidden ----
                Window.Width = 960; Window.Height = 720; tabs.SelectedIndex = 1; Pump();
                var ns = Get<UIElement>("NavSwitch"); if (ns != null) ns.Focus(); Pump();
                tabs.SelectedIndex = 0; Pump();   // the Overview hides the menu's switch; the keyboard stays in the menu
                var f = FocusManager.GetFocusedElement(Window) as UIElement;
                bool inMenu = f != null && f.IsVisible && f.IsDescendantOf(Get<DependencyObject>("Nav") ?? Window) || f == Get<UIElement>("Nav");
                check(ns != null && !ns.IsVisible && inMenu, "focus: on the Overview the menu's switch hides and the keyboard stays in the menu (now on " + (f == null ? "nothing" : DescribeStop(f)) + ")");
                tabs.SelectedIndex = 1; Pump();
                check(ns != null && ns.IsVisible, "focus: the menu's switch is back on the other sections");
                // the collapse button: folds the menu and back, and hides under 760 px without stranding the keyboard
                var fb = Get<Button>("NavCollapse");
                bool wasFolded = _cfg.NavCollapsed;
                if (fb != null) { fb.Focus(); Pump(); fb.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); }
                var rail = Get<FrameworkElement>("NavRail");
                check(fb != null && rail != null && _cfg.NavCollapsed != wasFolded && Math.Abs(rail.ActualWidth - (_cfg.NavCollapsed ? NavNarrow : NavWide)) < 1
                      && System.Windows.Automation.AutomationProperties.GetName(fb) == (_cfg.NavCollapsed ? "Expand menu" : "Collapse menu"),
                      "collapse: the menu's button folds it to icons and renames itself (menu " + (rail == null ? "?" : Math.Round(rail.ActualWidth).ToString()) + " px)");
                if (fb != null) { fb.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); }
                check(_cfg.NavCollapsed == wasFolded, "collapse: and the same button brings the names back");
                if (fb != null) { fb.Focus(); Pump(); }
                Window.Width = 660; Pump();   // icons only by width: the button has nothing to expand into and hides
                f = FocusManager.GetFocusedElement(Window) as UIElement;
                check(fb != null && !fb.IsVisible && (f == null || f.IsVisible), "focus: narrowing under 760 px hides the collapse button without stranding the keyboard (now on " + (f == null ? "nothing" : DescribeStop(f)) + ")");
                Window.Width = 960; Pump();
                tabs.SelectedIndex = 5; Pump();
                var first = TabStops().FirstOrDefault(x => Ancestor<TabControl>(x) != null); if (first != null) first.Focus(); Pump();
                Window.Width = 460; Window.Height = 262; Pump();   // into the small panel: the section is gone
                f = FocusManager.GetFocusedElement(Window) as UIElement;
                check(f == null || f.IsVisible, "focus: shrinking to the small panel doesn't strand the keyboard in the hidden section (now on " + (f == null ? "nothing" : DescribeStop(f)) + ")");

                sb.AppendLine(); sb.AppendLine("Tab order at 960x720:"); sb.Append(orders);
            }
            catch (Exception ex) { fails++; sb.AppendLine("FAIL  EXCEPTION " + ex); }
            File.WriteAllText(outPath, "Race Mode UI test: " + (fails == 0 ? "all passed" : fails + " failed") + Environment.NewLine + sb);
        }

        // Controls a screen reader lands on must say what they are.
        static void Unnamed(AutomationPeer peer, string where, Dictionary<string, string> found, int depth)
        {
            if (depth > 40) return;
            var t = peer.GetAutomationControlType();
            bool interactive = t == AutomationControlType.Button || t == AutomationControlType.CheckBox || t == AutomationControlType.RadioButton || t == AutomationControlType.Edit
                            || t == AutomationControlType.ComboBox || t == AutomationControlType.Slider || t == AutomationControlType.ListItem || t == AutomationControlType.List || t == AutomationControlType.Tab;
            if (interactive && !peer.IsOffscreen() && string.IsNullOrEmpty(peer.GetName()))
            {
                var owner = peer as UIElementAutomationPeer; var fe = owner == null ? null : owner.Owner as FrameworkElement;
                // parts inside a scroll bar, slider or dropdown are named by the control they belong to
                if (fe == null || (Ancestor<ScrollBar>(fe) == null && Ancestor<Slider>(fe) == null && !(fe is RepeatButton) && !(fe is Thumb)))
                {
                    string key = t.ToString().Replace("AutomationControlType.", "") + " " + (fe == null ? peer.GetClassName() : Describe(fe));
                    if (!found.ContainsKey(key)) found[key] = where;
                }
            }
            var kids = peer.GetChildren(); if (kids == null) return;
            foreach (var k in kids) Unnamed(k, where, found, depth + 1);
        }

        // Every visible, enabled control that takes part in Tab.
        List<UIElement> TabStops()
        {
            var list = new List<UIElement>(); var root = Get<Grid>("Root"); if (root == null) return list;
            var stack = new Stack<DependencyObject>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var o = stack.Pop(); var el = o as UIElement;
                if (el != null && el.Visibility != Visibility.Visible) continue;
                var c = o as Control;
                if (c != null && c.Focusable && c.IsEnabled && c.IsTabStop && KeyboardNavigation.GetIsTabStop(c) && !(c is ListBoxItem) && !(c is ScrollViewer) && !(c is TabItem) && !(c is TabControl)
                    && Ancestor<ScrollBar>(c) == null && !(c is ListBox && ((ListBox)c).Items.Count > 0 && KeyboardNavigation.GetTabNavigation(c) == KeyboardNavigationMode.Continue)) list.Add(c);
                int n = VisualTreeHelper.GetChildrenCount(o);
                for (int i = n - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(o, i));
            }
            return list;
        }

        // Where Tab goes, asked of WPF's own Tab logic (KeyboardNavigation) one step at a time. The window is off-screen
        // and never activated, so real keyboard focus isn't available (and isn't wanted: a stray key must not reach it).
        // Returns null when that logic can't be reached on this .NET version.
        List<UIElement> TabWalk()
        {
            var root = Get<Grid>("Root"); if (root == null) return null;
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var kn = typeof(KeyboardNavigation);
                var current = kn.GetProperty("Current", flags).GetValue(null, null);
                var next = kn.GetMethods(flags).First(m => m.Name == "GetNextTab" && m.GetParameters().Length == 3);
                var group = kn.GetMethods(flags).First(m => m.Name == "GetGroupParent" && m.GetParameters().Length == 2);
                var seen = new List<UIElement>();
                var cur = next.Invoke(current, new object[] { null, root, true }) as DependencyObject;
                for (int i = 0; i < 160 && cur != null; i++)
                {
                    var el = cur as UIElement;
                    if (el != null) { if (seen.Contains(el)) break; seen.Add(el); }
                    var container = group.Invoke(current, new object[] { cur, true }) as DependencyObject;
                    cur = next.Invoke(current, new object[] { cur, container, false }) as DependencyObject;
                }
                return seen;
            }
            catch { return null; }
        }

        string DescribeStop(UIElement el)
        {
            var fe = el as FrameworkElement; if (fe == null) return el.GetType().Name;
            // a row inside a list stands for its list
            var item = fe as ListBoxItem ?? Ancestor<ListBoxItem>(fe);
            if (item != null) { var lb = Ancestor<ListBox>(item); if (lb != null && !string.IsNullOrEmpty(lb.Name)) return lb.Name; }
            string n = !string.IsNullOrEmpty(fe.Name) ? fe.Name : Describe(fe);
            var bar = Get<Grid>("TitleBar");
            if (bar != null && fe is Button && bar.Children.Contains(fe)) n += " (title bar)";
            return n;
        }
    }
}
