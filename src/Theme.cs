// Light and dark themes.
//
// Every colour in the window is a named brush in the window's resources (RaceMode.xaml defines them with the dark
// values). XAML uses them as DynamicResource and code uses SetResourceReference, so swapping the brushes here
// recolours the open window. Which theme: Config.Theme "System" (default) follows Windows' app mode
// (HKCU ...\Themes\Personalize, AppsUseLightTheme: 1 = light, missing = dark, since Race Mode has always been dark),
// "Dark" or "Light". --snapshot takes theme=light / theme=dark to override both for that run.
//
// The light theme is its own palette, not an inversion: a soft off-white window, white cards and graphite ink.
// Red, amber and green keep their one meaning each and are deepened so they still read on white (contrast numbers
// for every light pair are in the pull request: text at least 4.5:1, switch tracks and other state marks at least 3:1).
// Dark tooltips stay Windows' own, as before; the light theme styles them to match its cards (Palette.Apply).
// The window frame: RoundCorners (App.cs) only asks DWM for round corners and sets no dark-mode attribute, so there is
// nothing to flip per theme; the 1 px border is the Edge token.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace RaceMode
{
    public static class Palette
    {
        // token, dark, light. The dark column is exactly what RaceMode.xaml declares.
        static readonly string[,] Tokens =
        {
            { "Bg",             "#121214", "#F2F2F4" },
            { "Card",           "#1D1D20", "#FFFFFF" },
            { "Raised",         "#2A2A2E", "#EDEDF0" },
            { "Hairline",       "#2C2C30", "#E2E2E6" },
            { "Line",           "#38383D", "#C4C4CA" },
            { "Ink",            "#F5F5F7", "#1C1C1E" },
            { "Dim",            "#A1A1A6", "#55555B" },
            { "Faint",          "#98989E", "#64646A" },
            { "Red",            "#FF453A", "#D92D20" },
            { "Warn",           "#FF9F0A", "#A35400" },
            { "Live",           "#30D158", "#1E8A3C" },
            { "Edge",           "#2C2C30", "#CBCBD1" },
            { "Soft",           "#D1D1D6", "#3A3A3E" },
            { "Field",          "#141416", "#EDEDF0" },
            { "LineHover",      "#4A4A50", "#A8A8AF" },
            { "LineFocus",      "#8A8A90", "#5E5E64" },
            { "Selection",      "#8E8E99", "#8E8E99" },
            { "Button",         "#2E2E33", "#E5E5EA" },
            { "ButtonEdge",     "#3C3C42", "#F0F0F3" },
            { "ButtonHover",    "#3A3A40", "#DADADF" },
            { "ButtonPressed",  "#26262A", "#CECED4" },
            { "OnInk",          "#121214", "#FFFFFF" },
            { "PrimaryHover",   "#FFFFFF", "#3A3A3F" },
            { "PrimaryPressed", "#D1D1D6", "#000000" },
            { "CaptionHover",   "#2A2A2E", "#E3E3E8" },
            { "CloseHover",     "#C42B1C", "#C42B1C" },
            { "CloseInk",       "#FFFFFF", "#FFFFFF" },
            { "Track",          "#6C6C74", "#84848B" },
            { "TrackHover",     "#7C7C84", "#727279" },
            { "Thumb",          "#FFFFFF", "#FFFFFF" },
            { "ThumbEdge",      "#00000000", "#8A8A91" },
            { "OnRed",          "#FFFFFF", "#FFFFFF" },
            { "CheckFill",      "#2A2A2E", "#FFFFFF" },
            { "CheckEdge",      "#6E6E75", "#8A8A91" },
            { "CheckHover",     "#A1A1A6", "#55555B" },
            { "SegOn",          "#46464C", "#4A4A50" },
            { "SegOnInk",       "#F5F5F7", "#FFFFFF" },
            { "ScreenTile",     "#242428", "#DCDCE1" },   // the screen picture (ScreenPick.cs): a screen
            { "ScreenTileEdge", "#323237", "#C8C8CE" },
            { "ScreenTileHover","#2C2C31", "#D0D0D6" },
            { "ScreenTileOn",   "#34343A", "#4A4A50" },   // the picked one (white outline and text on it)
            { "Scroll",         "#48484D", "#B0B0B6" },
            { "RowHover",       "#26262A", "#F2F2F5" },
            { "RowSelected",    "#2F2F35", "#E8E8EC" },
            { "NavHover",       "#1D1D20", "#E7E7EB" },
            { "NavOn",          "#34343A", "#FFFFFF" },
            { "NavOnEdge",      "#4A4A52", "#D4D4D9" },
            { "SliderTrack",    "#3A3A3F", "#D9D9DE" },
            { "Tick",           "#5A5A60", "#A8A8AF" },
            { "DotOff",         "#5A5A60", "#84848B" },
            { "MeterTrack",     "#303035", "#E3E3E7" },
            { "Popup",          "#2A2A2E", "#FFFFFF" },
            { "PopupHover",     "#3A3A40", "#EDEDF0" },
            { "OnTint",         "#2A1A1A", "#FDECEA" },
            { "OnEdge",         "#5A2420", "#F0B7B1" },
            { "HeroOnTop",      "#3A1A18", "#FCE4E1" },
            { "HeroOnMid",      "#221B1D", "#FFF7F6" },
            { "TipBg",          "#FFFFFF", "#FFFFFF" },
            { "TipEdge",        "#C4C4CA", "#C4C4CA" },
        };

        public static Color Get(string key, bool light)
        {
            for (int i = 0; i < Tokens.GetLength(0); i++)
                if (Tokens[i, 0] == key) return Parse(Tokens[i, light ? 2 : 1]);
            throw new ArgumentException("No colour token " + key);
        }

        static Color Parse(string hex)
        {
            var h = hex.TrimStart('#');
            byte a = 255; int o = 0;
            if (h.Length == 8) { a = byte.Parse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture); o = 2; }
            return Color.FromArgb(a, byte.Parse(h.Substring(o, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                                     byte.Parse(h.Substring(o + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                                     byte.Parse(h.Substring(o + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        // "System", "Dark" or "Light" (anything else counts as System).
        public static string Normalize(string setting)
        {
            if (string.Equals(setting, "Dark", StringComparison.OrdinalIgnoreCase)) return "Dark";
            if (string.Equals(setting, "Light", StringComparison.OrdinalIgnoreCase)) return "Light";
            return "System";
        }

        public static bool IsLight(string setting)
        {
            var s = Normalize(setting);
            return s == "Light" || (s == "System" && WindowsAppsLight());
        }

        // Windows' app mode (Settings > Personalisation > Colours). Read-only; missing or unreadable = dark.
        public static bool WindowsAppsLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        // Swaps every token brush in the element's resources. Elements that refer to them dynamically repaint.
        public static void Apply(FrameworkElement root, bool light)
        {
            var res = root.Resources;
            for (int i = 0; i < Tokens.GetLength(0); i++)
            {
                var b = new SolidColorBrush(Parse(Tokens[i, light ? 2 : 1]));
                b.Freeze();
                res[Tokens[i, 0]] = b;
            }
            // Tooltips: the dark theme keeps Windows' own (as it always has); the light theme gets one that matches its cards.
            if (light) res[typeof(ToolTip)] = TipStyle();
            else if (res.Contains(typeof(ToolTip))) res.Remove(typeof(ToolTip));
        }

        static Style TipStyle()
        {
            var s = new Style(typeof(ToolTip));
            s.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Ink")));
            s.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("TipBg")));
            s.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("TipEdge")));
            s.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI Variable Text, Segoe UI")));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 6)));
            s.Setters.Add(new Setter(ToolTip.HasDropShadowProperty, false));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            s.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ToolTip)) { VisualTree = border }));
            return s;
        }

        // Shorthands for code that colours an element: a live reference to a token, never a copied brush.
        public static void Ref(FrameworkElement e, DependencyProperty p, string key) { if (e != null) e.SetResourceReference(p, key); }
        public static System.Windows.Documents.Run Run(string text, string key)
        {
            var r = new System.Windows.Documents.Run(text);
            r.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, key);
            return r;
        }
    }

    public partial class Shell
    {
        // --snapshot ... theme=light|dark: overrides Config.Theme and Windows for that run (Program.Run sets it).
        public static string ThemeArg;
        bool _light, _themeReady;
        EventHandler _closedUnhook;
        UserPreferenceChangedEventHandler _prefHandler;

        string ThemeSetting { get { return ThemeArg ?? _cfg.Theme; } }

        void InitTheme()
        {
            BuildHeroTint();
            _light = Palette.IsLight(ThemeSetting);
            Palette.Apply(Window, _light);
            HeroTint(false);
            _themeReady = true;
            // Windows' app mode changed (Settings > Personalisation > Colours): follow it without a restart.
            _prefHandler = (s, e) =>
            {
                if (e.Category != UserPreferenceCategory.General) return;
                try { Window.Dispatcher.BeginInvoke(new Action(ApplyTheme)); } catch { }
            };
            SystemEvents.UserPreferenceChanged += _prefHandler;
            _closedUnhook = (s, e) => { SystemEvents.UserPreferenceChanged -= _prefHandler; };
            Window.Closed += _closedUnhook;
        }

        // Re-reads the setting (and Windows) and swaps the palette if it changed.
        void ApplyTheme()
        {
            if (!_themeReady) return;
            bool light = Palette.IsLight(ThemeSetting);
            if (light == _light) return;
            _light = light;
            Palette.Apply(Window, light);
            HeroTint(false);
            try { RefreshStats(); } catch { }   // a few readings copy a colour from another element; repaint them now
        }

        // --snapshot ... theme=flip: starts dark and switches to light once the window is up, to check that an open
        // window repaints completely (the picture should match a theme=light one, live numbers aside).
        public void SwitchThemeForSnapshot(string setting) { ThemeArg = setting; ApplyTheme(); }

        // Settings > Windows > Appearance: System / Dark / Light.
        void BindThemeChoice()
        {
            var pick = new[] { Get<RadioButton>("ThemeSystem"), Get<RadioButton>("ThemeDark"), Get<RadioButton>("ThemeLight") };
            var names = new[] { "System", "Dark", "Light" };
            var cur = Palette.Normalize(_cfg.Theme);
            for (int i = 0; i < pick.Length; i++)
            {
                if (pick[i] == null) continue;
                pick[i].IsChecked = names[i] == cur;
                var name = names[i];
                pick[i].Checked += (s, e) =>
                {
                    if (_cfg.Theme == name) return;
                    _cfg.Theme = name; SaveConfig(); ApplyTheme();
                };
            }
        }

        // The switch card: Card while off, warming to a red tint from the top left while on, in the 240 ms the thumb
        // takes (To-only animations, so switching back mid-way is smooth). The colours come from the current theme.
        LinearGradientBrush _heroBg;
        SolidColorBrush _heroEdge;

        void BuildHeroTint()
        {
            var hero = Get<Border>("Hero"); var sw = Get<ToggleButton>("Switch");
            if (hero == null || sw == null) return;
            var card = Palette.Get("Card", false);
            _heroBg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            _heroBg.GradientStops.Add(new GradientStop(card, 0));
            _heroBg.GradientStops.Add(new GradientStop(card, 0.55));
            _heroBg.GradientStops.Add(new GradientStop(card, 1));
            _heroEdge = new SolidColorBrush(Palette.Get("Hairline", false));
            hero.Background = _heroBg; hero.BorderBrush = _heroEdge;
            sw.Checked += (s, e) => HeroTint(true);
            sw.Unchecked += (s, e) => HeroTint(true);
        }

        void HeroTint(bool animate)
        {
            var sw = Get<ToggleButton>("Switch");
            if (_heroBg == null || sw == null) return;
            bool on = sw.IsChecked == true;
            Fade(_heroBg.GradientStops[0], GradientStop.ColorProperty, Palette.Get(on ? "HeroOnTop" : "Card", _light), animate);
            Fade(_heroBg.GradientStops[1], GradientStop.ColorProperty, Palette.Get(on ? "HeroOnMid" : "Card", _light), animate);
            Fade(_heroBg.GradientStops[2], GradientStop.ColorProperty, Palette.Get("Card", _light), false);
            Fade(_heroEdge, SolidColorBrush.ColorProperty, Palette.Get(on ? "OnEdge" : "Hairline", _light), animate);
        }

        static void Fade(Animatable target, DependencyProperty p, Color to, bool animate)
        {
            if (!animate) { target.BeginAnimation(p, null); target.SetValue(p, to); return; }
            target.BeginAnimation(p, new ColorAnimation(to, TimeSpan.FromSeconds(0.24))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 }
            });
        }
    }
}
