using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RaceMode
{
    // Stream tab, OBS part: stream stats (ObsClient.cs), the OBS dock (ObsDock.cs) and the guided stream test.
    public partial class Shell
    {
        volatile string _dockJson = "{}";

        void BindObs()
        {
            Get<Button>("DockLinkBtn").Click += (s, e) =>
            {
                if (!ObsDock.Running) { Tick("Turn on Streaming first, then copy the dock link"); return; }
                try
                {
                    Clipboard.SetText(ObsDock.Url); Tick("Dock link copied. In OBS: Docks, Custom Browser Docks, paste it, name it Race Mode.");
                    if (_cfg.DockLinkChanged) { _cfg.DockLinkChanged = false; SaveConfig(); ShowDockChanged(); }
                }
                catch { Tick("Another app is using the clipboard. Try again in a moment."); }
            };
            Get<Button>("ObsEnableBtn").Click += (s, e) =>
            {
                var err = Obs.EnableRemoteControl();
                Installer.Log("obs", new[] { err ?? "OBS remote control switched on (from Stream)" }, Shell.Data);
                Tick(err ?? "OBS's remote control is on. Open OBS and Race Mode connects by itself.");
            };
            Get<Button>("StreamTestBtn").Click += (s, e) => { if (_test == null) StartStreamTest(); else StopStreamTest("Stream test stopped"); };
            Window.Closing += (s, e) => { if (!e.Cancel) StopObsFeatures(); };
            if (_cfg.Streaming) StartObsFeatures();
        }

        void StartObsFeatures()
        {
            Obs.Start();
            bool fix = false;
            if (_cfg.DockPort < 1024 || _cfg.DockPort > 65535) { _cfg.DockPort = ObsDock.DefaultPort; fix = true; }
            if (!ObsDock.ValidToken(_cfg.DockToken)) { _cfg.DockToken = ObsDock.NewToken(); fix = true; }
            if (fix) SaveConfig();
            var err = ObsDock.Start(_cfg.DockPort, _cfg.DockToken, () => _dockJson, DockSwitch, DockShow);
            if (err == null && ObsDock.Port != _cfg.DockPort)
            {
                // the usual port was taken, so the dock moved to the next free one: a new link (and key) for OBS
                _cfg.DockPort = ObsDock.Port; _cfg.DockToken = ObsDock.NewToken(); _cfg.DockLinkChanged = true; SaveConfig();
                ObsDock.Start(_cfg.DockPort, _cfg.DockToken, () => _dockJson, DockSwitch, DockShow);
                var moved = "The OBS dock moved to port " + _cfg.DockPort + " because the usual one was busy. Copy the dock link into OBS again.";
                Tick(moved); _mon.Event(moved);
            }
            if (err != null)
            {
                // Couldn't listen (usually the port is taken). Whoever holds it may have collected the old link's token, so make a new one.
                _cfg.DockToken = ObsDock.NewToken(); SaveConfig();
                err += ". Copy the dock link into OBS again once it's running.";
                Tick(err); _mon.Event(err);
            }
            Get<Button>("DockLinkBtn").IsEnabled = ObsDock.Running;
            ShowDockChanged();
        }

        void ShowDockChanged()
        {
            var note = Get<TextBlock>("DockChangedNote");
            if (note != null) note.Visibility = _cfg.DockLinkChanged && ObsDock.Running ? Visibility.Visible : Visibility.Collapsed;
        }

        // From the OBS dock (a background thread): flip the same switch as the window, on the UI thread.
        void DockSwitch(bool on)
        {
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var sw = Get<System.Windows.Controls.Primitives.ToggleButton>("Switch");
                if (sw == null || sw.IsChecked == on) return;
                _mon.Event("Race Mode turned " + (on ? "on" : "off") + " from the OBS dock");
                sw.IsChecked = on;
            }));
        }

        void DockShow()
        {
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Window.IsVisible) Window.Show();
                if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
                Window.Activate(); Window.Topmost = true; Window.Topmost = false; Window.Focus();
            }));
        }

        // The dock's "Setup" rows: what Race Mode is set to do, in the app's own words. Read-only.
        List<object> DockSetup()
        {
            var rows = new List<object>();
            Action<string, string> row = (k, v) => rows.Add(new Dictionary<string, object> { { "label", k }, { "value", v } });
            if (_cfg.GraphicsMode == "Photo")
            {
                int pl = _cfg.GraphicsPhoto != null ? _cfg.GraphicsPhoto.Level : 0;
                row("Graphics", "Photo mode" + (pl > 0 && pl < GraphicsCatalog.PhotoNames.Length ? ", replays " + GraphicsCatalog.PhotoNames[pl] : ""));
            }
            else
            {
                int rl = _cfg.GraphicsRace != null ? _cfg.GraphicsRace.Level : 0;
                row("Graphics", rl > 0 && rl < GraphicsCatalog.LevelNames.Length ? "Race graphics, level " + rl + " of 10 (" + GraphicsCatalog.LevelNames[rl] + ")" : "Race graphics");
            }
            var g = _cfg.DisplayGuard;
            row("Screen check", g != null && g.Enabled ? "on" + (g.EnforcesFpsCap ? ", FPS cap " + g.FpsLimit : "") : "off");
            row("Launch apps", _cfg.LaunchApps ? _launch.Count(l => l.Enabled).ToString(CultureInfo.InvariantCulture) : "off");
            row("Close list", _cfg.CloseApps ? _close.Count.ToString(CultureInfo.InvariantCulture) : "off");
            row("Put iRacing first", _cfg.SimHighPriority ? "on" : "off");
            row("Streaming", _cfg.Streaming ? (_cfg.StreamBoost ? "on, OBS put ahead" : "on") : "off");
            return rows;
        }

        // One line for the dock's folded Setup drawer, e.g. "Level 6 · 6 launch · 25 close".
        string DockSetupShort()
        {
            string gfx;
            if (_cfg.GraphicsMode == "Photo") gfx = "Photo";
            else { int rl = _cfg.GraphicsRace != null ? _cfg.GraphicsRace.Level : 0; gfx = rl > 0 && rl < GraphicsCatalog.LevelNames.Length ? "Level " + rl : "Race"; }
            return gfx + " · " + (_cfg.LaunchApps ? _launch.Count(l => l.Enabled) : 0) + " launch · " + (_cfg.CloseApps ? _close.Count : 0) + " close";
        }

        void StopObsFeatures()
        {
            Obs.Stop();
            ObsDock.Stop();
            Get<Button>("DockLinkBtn").IsEnabled = false;
            if (_test != null) StopStreamTest("Stream test stopped: Streaming was turned off");
        }

        // Called once a second from RefreshStats: OBS status on the Stream tab, and the dock's data.
        void RefreshObs(LiveStats live)
        {
            var o = Obs.Latest;
            bool on = _cfg.Streaming;
            var dot = Get<Ellipse>("ObsDot");
            var ws = on && !o.Connected ? Obs.ReadSettings() : null;
            bool needsEnable = on && Obs.Installed && ws != null && !ws.Enabled;
            string title, sub, fill;   // fill: the dot's colour token
            if (!on) { title = "Stream stats"; sub = "Turn on Streaming to see dropped frames and bitrate from OBS."; fill = "DotOff"; }
            else if (!Obs.Installed) { title = "OBS isn't installed"; sub = "Race Mode reads stream stats from OBS Studio."; fill = "DotOff"; }
            else if (needsEnable) { title = "OBS's remote control is off"; sub = "Race Mode needs it to see stream stats. Close OBS, then click Turn On OBS Stats."; fill = "Warn"; }
            else if (!o.Connected) { title = "Stream stats"; sub = o.Status; fill = "DotOff"; }
            else if (!o.Streaming) { title = "OBS is connected"; sub = "Not streaming right now."; fill = "Live"; }
            else
            {
                title = o.Reconnecting ? "OBS is reconnecting" : "Streaming";
                sub = (double.IsNaN(o.Kbps) ? "bitrate --" : N0(o.Kbps) + " kbps") + " · dropped " + Frames(o.NetDropped, o.NetTotal) +
                      " · lagged " + Frames(o.RenderSkipped + o.EncodeSkipped, o.RenderTotal + o.EncodeTotal);
                fill = o.Reconnecting || (o.NetTotal > 0 && o.NetDropped * 100.0 / o.NetTotal >= 1) ? "Warn" : "Live";
            }
            SetText("ObsTitle", title); SetText("ObsSub", sub);
            if (dot != null) dot.SetResourceReference(Shape.FillProperty, fill);
            Get<Button>("ObsEnableBtn").Visibility = needsEnable ? Visibility.Visible : Visibility.Collapsed;

            if (ObsDock.Running)
            {
                int cap = FpsCap();
                var obs = new Dictionary<string, object> {
                    { "connected", o.Connected }, { "status", o.Connected ? (o.Streaming ? (o.Reconnecting ? "Reconnecting" : "Streaming") : "OBS is connected, not streaming") : o.Status },
                    { "streaming", o.Streaming }, { "reconnecting", o.Reconnecting }, { "kbps", Num(o.Kbps) },
                    { "netDropped", o.NetDropped }, { "netTotal", o.NetTotal }, { "renderSkipped", o.RenderSkipped }, { "renderTotal", o.RenderTotal },
                    { "encodeSkipped", o.EncodeSkipped }, { "encodeTotal", o.EncodeTotal } };
                bool f = Monitor.HwinfoFahrenheit;
                var st = Get<TextBlock>("StatusText");
                var d = new Dictionary<string, object> {
                    { "on", _on }, { "phase", _phase }, { "headline", Get<TextBlock>("Headline") != null ? Get<TextBlock>("Headline").Text : "" },
                    { "status", st != null ? (st.Text ?? "").Split('\n')[0] : "" }, { "setup", DockSetup() }, { "setupShort", DockSetupShort() }, { "fps", Num(live.Fps) }, { "fpsCap", cap }, { "core", Num(live.CpuMaxCore) }, { "gpu", Num(live.GpuUtil) },
                    { "temp", Num(double.IsNaN(live.CpuTemp) ? double.NaN : f ? live.CpuTemp * 9 / 5 + 32 : live.CpuTemp) }, { "tempUnit", f ? "°F" : "°C" },
                    { "tempHot", !double.IsNaN(live.CpuTemp) && live.CpuTemp >= 85 }, { "obs", obs } };
                _dockJson = new JavaScriptSerializer().Serialize(d);
            }
            if (_test != null) StreamTestTick(live, o);
        }

        static object Num(double d) { return double.IsNaN(d) || double.IsInfinity(d) ? null : (object)Math.Round(d, 1); }
        static string Frames(long d, long t) { return t <= 0 ? "0" : d.ToString("N0", CultureInfo.CurrentCulture) + " (" + (100.0 * d / t).ToString(d > 0 && 100.0 * d / t < 0.1 ? "0.00" : "0.0", CultureInfo.CurrentCulture) + "%)"; }

        // ---------------- stream test: 5 minutes without streaming, then 5 minutes streaming ----------------
        class StreamTest
        {
            public string Phase = "baseline";   // baseline -> waiting (for the stream to start) -> live -> done
            public DateTime PhaseStart = DateTime.Now;
            public List<double> FpsA = new List<double>(), FpsB = new List<double>(), CoreA = new List<double>(), CoreB = new List<double>(), GpuA = new List<double>(), GpuB = new List<double>(), Kbps = new List<double>();
            public ObsStats First, Last;
        }
        StreamTest _test;
        const int TestPhaseSec = 300;

        void StartStreamTest()
        {
            if (!_cfg.Streaming) { SetText("StreamTestText", "Turn on Streaming first, so Race Mode can read OBS."); return; }
            var o = Obs.Latest;
            if (o.Streaming) { SetText("StreamTestText", "Stop the stream in OBS first. The test starts without streaming, then asks you to start it."); return; }
            _test = new StreamTest();
            Get<Button>("StreamTestBtn").Content = "Stop Test";
            SetText("StreamTestText", "Step 1 of 2: drive in iRacing for 5 minutes without streaming…");
            _mon.Event("Stream test started (baseline, not streaming)");
        }

        void StopStreamTest(string why)
        {
            _test = null;
            Get<Button>("StreamTestBtn").Content = "Start Test";
            SetText("StreamTestText", why);
        }

        void StreamTestTick(LiveStats live, ObsStats o)
        {
            var t = _test; var secs = (DateTime.Now - t.PhaseStart).TotalSeconds;
            bool driving = !double.IsNaN(live.Fps) && live.SimContext == "drive";
            if (t.Phase == "baseline")
            {
                if (o.Streaming) { StopStreamTest("The stream started during step 1, so the test was stopped. Start it again with the stream off."); return; }
                if (driving) { t.FpsA.Add(live.Fps); t.CoreA.Add(live.CpuMaxCore); if (!double.IsNaN(live.GpuUtil)) t.GpuA.Add(live.GpuUtil); }
                SetText("StreamTestText", "Step 1 of 2: drive without streaming · " + Left(secs) + (driving ? "" : " (waiting for you to be on track)"));
                if (secs >= TestPhaseSec && t.FpsA.Count >= 60) { t.Phase = "waiting"; t.PhaseStart = DateTime.Now; }
            }
            else if (t.Phase == "waiting")
            {
                SetText("StreamTestText", "Step 2 of 2: start streaming in OBS now (or OBS's bandwidth test), then keep driving.");
                if (o.Streaming) { t.Phase = "live"; t.PhaseStart = DateTime.Now; t.First = o; _mon.Event("Stream test: stream started"); }
            }
            else if (t.Phase == "live")
            {
                if (!o.Streaming) { StopStreamTest("The stream stopped during step 2, so the test was stopped."); return; }
                t.Last = o;
                if (driving) { t.FpsB.Add(live.Fps); t.CoreB.Add(live.CpuMaxCore); if (!double.IsNaN(live.GpuUtil)) t.GpuB.Add(live.GpuUtil); }
                if (!double.IsNaN(o.Kbps)) t.Kbps.Add(o.Kbps);
                SetText("StreamTestText", "Step 2 of 2: streaming · " + Left(secs) + (driving ? "" : " (waiting for you to be on track)"));
                if (secs >= TestPhaseSec && t.FpsB.Count >= 60) FinishStreamTest();
            }
        }

        static string Left(double secs) { var l = Math.Max(0, TestPhaseSec - (int)secs); return (l / 60) + ":" + (l % 60).ToString("00") + " left"; }

        void FinishStreamTest()
        {
            var t = _test;
            Func<List<double>, double> avg = l => l.Count == 0 ? double.NaN : l.Average();
            Func<List<double>, double> p5 = l => { if (l.Count == 0) return double.NaN; var s = l.OrderBy(x => x).ToList(); return s[(int)(s.Count * 0.05)]; };
            long dropped = Math.Max(0, t.Last.NetDropped - t.First.NetDropped), sent = Math.Max(0, t.Last.NetTotal - t.First.NetTotal);
            long lagged = Math.Max(0, t.Last.RenderSkipped - t.First.RenderSkipped) + Math.Max(0, t.Last.EncodeSkipped - t.First.EncodeSkipped);
            long frames = Math.Max(0, t.Last.RenderTotal - t.First.RenderTotal) + Math.Max(0, t.Last.EncodeTotal - t.First.EncodeTotal);
            double fpsCost = avg(t.FpsA) - avg(t.FpsB);
            var sb = new StringBuilder();
            sb.AppendLine("Race Mode stream test · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            sb.AppendLine("FPS without streaming : avg " + N0(avg(t.FpsA)) + ", worst 5% below " + N0(p5(t.FpsA)) + "  (" + t.FpsA.Count + " samples)");
            sb.AppendLine("FPS while streaming   : avg " + N0(avg(t.FpsB)) + ", worst 5% below " + N0(p5(t.FpsB)) + "  (" + t.FpsB.Count + " samples)");
            sb.AppendLine("Busiest CPU core      : " + N0(avg(t.CoreA)) + "% -> " + N0(avg(t.CoreB)) + "%   GPU: " + N0(avg(t.GpuA)) + "% -> " + N0(avg(t.GpuB)) + "%");
            sb.AppendLine("Stream bitrate        : avg " + N0(avg(t.Kbps)) + " kbps, lowest " + N0(t.Kbps.Count == 0 ? double.NaN : t.Kbps.Min()) + " kbps");
            sb.AppendLine("Dropped (network)     : " + Frames(dropped, sent));
            sb.AppendLine("Lagged (PC too busy)  : " + Frames(lagged, frames));
            string verdict =
                sent > 0 && dropped * 100.0 / sent >= 1 ? "Your connection dropped frames: lower the stream bitrate in OBS." :
                frames > 0 && lagged * 100.0 / frames >= 1 ? "Your PC couldn't keep up with streaming: lower OBS's output resolution or frame rate, or use the GPU encoder (NVENC)." :
                fpsCost >= 10 ? "The stream was healthy, but it cost about " + N0(fpsCost) + " FPS in iRacing." :
                "The stream was healthy and cost about " + N0(Math.Max(0, fpsCost)) + " FPS.";
            sb.AppendLine(verdict);
            try
            {
                Directory.CreateDirectory(LogsRoot);
                var file = System.IO.Path.Combine(LogsRoot, "stream-test-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture) + ".txt");
                File.WriteAllText(file, sb.ToString());
            }
            catch { }
            _mon.Event("Stream test finished: " + verdict);
            _test = null;
            Get<Button>("StreamTestBtn").Content = "Start Test";
            SetText("StreamTestText", verdict + " FPS " + N0(avg(t.FpsA)) + " -> " + N0(avg(t.FpsB)) + ", bitrate " + N0(avg(t.Kbps)) + " kbps, dropped " + Frames(dropped, sent) + ". Saved in the log folder.");
        }
    }
}
