// Display guard ("screen check") - keeps iRacing on the racing monitor.
//
// Why this exists: Windows sometimes renumbers monitors (DISPLAY1/2/3 swap between boots). iRacing's
// rendererDX11Monitor.ini stores the monitor as a plain index (deviceIdx), so after a renumber it points at
// the wrong screen, iRacing falls back to the primary monitor and writes that back to the ini when the sim
// closes. Tools that save graphics profiles (irSidekick) can then copy the bad values and re-apply them.
//
// The guard finds the racing monitor by its EDID id (stable, unlike the DISPLAYn number), works out its
// current index and desktop position, and puts the [Display] values back before iRacing reads them. The FPS
// cap is only enforced when KeepFpsCap is on (off for new installs, so a user's own cap is never changed).
// It never writes while the sim is running, backs the ini up first (keeps the last 5), and writes atomically.
// deviceIdx = the monitor's position in DXGI's output order (what a DX11 game enumerates).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RaceMode
{
    public class DisplayGuardConfig
    {
        // Off until a racing monitor is picked (Settings tab or first-run setup).
        public bool Enabled = false;
        public string MonitorId = "";          // EDID id, as Windows lists it in MONITOR\<id>
        public int Width, Height, RefreshRate;
        public bool FullScreen = false;        // false = borderless window covering the whole monitor
        // Enforce FpsLimit too. True here only so configs saved before this option keep their behaviour;
        // new installs (Config.Defaults) set it false, so iRacing's own FPS cap is left alone.
        public bool KeepFpsCap = true;
        public int FpsLimit;
        public string IniPath = "";            // blank = Documents\iRacing\rendererDX11Monitor.ini
        // Three screens side by side, as EDID ids left, centre, right (null = one screen). Then MonitorId is the
        // centre one, Width/Height are one screen's size and RefreshRate the slowest of the three.
        public List<string> Triple;

        public bool EnforcesFpsCap { get { return KeepFpsCap && FpsLimit > 0; } }
        public bool IsTriple { get { return Triple != null && Triple.Count == 3 && Triple.All(t => !string.IsNullOrWhiteSpace(t)); } }
    }

    public class MonitorInfo
    {
        public int Index, X, Y, Width, Height, Hz;
        public bool Primary;
        public string DeviceName = "", MonitorId = "", Name = "";
        public override string ToString()
        {
            return Index + ": " + DeviceName + " " + MonitorId + " " + Width + "x" + Height + " @" + Hz + "Hz at " + X + "," + Y + (Primary ? " (primary)" : "");
        }
    }

    public class GuardResult
    {
        public bool Changed, Skipped;
        public string Message = "", Backup, Warning;
        public List<string> Changes = new List<string>();
        public List<MonitorInfo> Monitors = new List<MonitorInfo>();
    }

    public static class DisplayGuard
    {
        public static string DefaultIniPath
        {
            get
            {
                // Under RACEMODE_TEST_ROOT (test and harness runs) the default is inside the test folder, never the real file.
                var test = Environment.GetEnvironmentVariable("RACEMODE_TEST_ROOT");
                var docs = string.IsNullOrEmpty(test) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.Combine(test, "Documents");
                return Path.Combine(Path.Combine(docs, "iRacing"), "rendererDX11Monitor.ini");
            }
        }

        // Never throws: any failure comes back as a skipped result with the reason.
        public static GuardResult Run(DisplayGuardConfig cfg, bool apply)
        {
            return Run(cfg, apply, null, null);
        }

        // monitors / simRunning can be supplied by tests; null means read the real system.
        public static GuardResult Run(DisplayGuardConfig cfg, bool apply, List<MonitorInfo> monitors, bool? simRunningOverride)
        {
            var r = new GuardResult();
            try
            {
                if (cfg == null || !cfg.Enabled) return Skip(r, "Screen check is off");
                if (string.IsNullOrWhiteSpace(cfg.MonitorId)) return Skip(r, "No racing monitor picked for the screen check");
                if (cfg.Width <= 0 || cfg.Height <= 0 || cfg.RefreshRate <= 0)
                    return Skip(r, "The screen check settings look wrong (size or refresh rate is 0), so nothing was changed");

                bool simRunning;
                if (simRunningOverride.HasValue) simRunning = simRunningOverride.Value;
                else { var sims = Process.GetProcessesByName(Monitor.SimName); simRunning = sims.Length > 0; foreach (var p in sims) p.Dispose(); }
                if (simRunning && apply) return Skip(r, "iRacing is running, so the screen check waits until you close it");

                var path = string.IsNullOrWhiteSpace(cfg.IniPath) ? DefaultIniPath : cfg.IniPath;
                if (!File.Exists(path)) return Skip(r, "iRacing's screen settings file wasn't found at " + path);

                r.Monitors = monitors ?? ListMonitors();
                if (cfg.IsTriple) return RunTriple(r, cfg, path, apply);

                // iRacing is set up for three screens but Race Mode guards one: putting the window back on one
                // screen would undo the triple setup. A Surround/Eyefinity display (one very wide screen) is fine.
                if (IniNumMonitors(path) == 3)
                {
                    var one = r.Monitors.FirstOrDefault(m => (m.MonitorId ?? "").IndexOf(cfg.MonitorId.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                    if (one == null || !LooksJoined(one))
                        return Skip(r, "iRacing is set up for three screens, so Race Mode left its screen settings alone. To have them checked, pick your three screens as the racing monitor on the Settings tab");
                }

                var matches = r.Monitors.Where(m => (m.MonitorId ?? "").IndexOf(cfg.MonitorId.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                // Two identical monitors share an EDID id: prefer the one at the wanted resolution; if that's still
                // ambiguous, don't guess.
                if (matches.Count > 1) matches = matches.Where(m => m.Width == cfg.Width && m.Height == cfg.Height).ToList();
                if (matches.Count > 1)
                    return Skip(r, "Two monitors of the same model are connected, so Race Mode can't tell which one to race on and left iRacing's screen settings alone (" + cfg.MonitorId + ")");
                var target = matches.FirstOrDefault();
                if (target == null)
                    return Skip(r, "Your racing monitor isn't connected or is switched off, so iRacing's screen settings were left alone (looking for " + cfg.MonitorId + ", found " + string.Join(", ", r.Monitors.Select(m => m.MonitorId).ToArray()) + ")");
                if (target.Width <= 0 || target.Height <= 0)
                    return Skip(r, "Couldn't tell where your racing monitor is on the desktop, so iRacing's screen settings were left alone (" + cfg.MonitorId + ")");

                var inv = CultureInfo.InvariantCulture;
                // Section -> key -> wanted value. Windowed size follows the monitor's real desktop size so the
                // borderless window always covers it exactly.
                var want = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                want["Display"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "deviceIdx", target.Index.ToString(inv) },
                    { "fullScreen", cfg.FullScreen ? "1" : "0" },
                    { "fullScreenWidth", cfg.Width.ToString(inv) },
                    { "fullScreenHeight", cfg.Height.ToString(inv) },
                    { "RefreshRate", cfg.RefreshRate.ToString("0.000", inv) },
                    { "windowedXPos", target.X.ToString(inv) },
                    { "windowedYPos", target.Y.ToString(inv) },
                    { "windowedWidth", target.Width.ToString(inv) },
                    { "windowedHeight", target.Height.ToString(inv) },
                    { "windowedMaximized", "0" },
                    { "border", "0" },
                };
                if (cfg.EnforcesFpsCap)
                    want["Graphics Options"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "LimitFrameRate", "1" },
                        { "DesiredFPSLimit", cfg.FpsLimit.ToString(inv) },
                    };

                // A borderless window runs at whatever mode Windows has the monitor in, so flag a dropped
                // refresh rate or resolution (Windows sometimes falls back to 60 Hz after updates or wake).
                if (target.Hz > 0 && target.Hz < cfg.RefreshRate)
                    r.Warning = "Windows has your racing monitor at " + target.Hz + " Hz, not " + cfg.RefreshRate + " Hz. Change it in Settings > Display > Advanced display";
                else if (target.Width != cfg.Width || target.Height != cfg.Height)
                    r.Warning = "Windows has your racing monitor at " + target.Width + "×" + target.Height + ", not " + cfg.Width + "×" + cfg.Height + ". Change it in Settings > Display";
                var desc = "your " + target.Width + "×" + target.Height + " racing monitor";
                return Write(r, cfg, path, want, apply, "on " + desc, "back on " + desc, null);
            }
            catch (Exception ex)
            {
                Shell.LogCrash(ex);
                r.Changed = false; r.Changes.Clear();
                return Skip(r, "The screen check didn't work, so iRacing's settings were left as they were: " + ex.Message);
            }
        }

        // Three screens side by side: iRacing runs as one borderless window across all three (iRacing's own
        // "run in a window" triple method; full screen across separate screens needs Surround/Eyefinity).
        // Only the window's position and size are kept: deviceIdx and the full-screen mode are left as iRacing
        // has them, and so is everything in [MonitorSetup] (screen angles, bezels, distance), which the
        // driver tunes inside iRacing.
        static GuardResult RunTriple(GuardResult r, DisplayGuardConfig cfg, string path, bool apply)
        {
            var rows = TripleRows(r.Monitors).Where(row => IdMatches(row[0], cfg.Triple[0]) && IdMatches(row[1], cfg.Triple[1]) && IdMatches(row[2], cfg.Triple[2])).ToList();
            if (rows.Count == 0)
                return Skip(r, "Your three racing screens aren't all connected side by side right now, so iRacing's screen settings were left alone (looking for " + string.Join(", ", cfg.Triple.ToArray()) + ", found " + string.Join(", ", r.Monitors.Select(m => m.MonitorId).ToArray()) + ")");
            if (rows.Count > 1)
                return Skip(r, "More than one row of three matching screens is connected, so Race Mode can't tell which to race on and left iRacing's screen settings alone");
            var row3 = rows[0];
            var inv = CultureInfo.InvariantCulture;
            int width = row3.Sum(m => m.Width);
            var want = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            want["Display"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "fullScreen", "0" },
                { "windowedXPos", row3[0].X.ToString(inv) },
                { "windowedYPos", row3[0].Y.ToString(inv) },
                { "windowedWidth", width.ToString(inv) },
                { "windowedHeight", row3[0].Height.ToString(inv) },
                { "windowedMaximized", "0" },
                { "border", "0" },
            };
            if (cfg.EnforcesFpsCap)
                want["Graphics Options"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "LimitFrameRate", "1" },
                    { "DesiredFPSLimit", cfg.FpsLimit.ToString(inv) },
                };
            var slow = row3.Where(m => m.Hz > 0 && m.Hz < cfg.RefreshRate).OrderBy(m => m.Hz).FirstOrDefault();
            if (slow != null)
                r.Warning = "Windows has one of your racing screens (screen " + (slow.Index + 1) + ") at " + slow.Hz + " Hz, not " + cfg.RefreshRate + " Hz. Change it in Settings > Display > Advanced display";
            else if (row3.Any(m => m.Width != cfg.Width || m.Height != cfg.Height))
                r.Warning = "Windows has your racing screens at a different size than when you picked them (" + cfg.Width + "×" + cfg.Height + " each). Change it in Settings > Display";
            // Not Race Mode's to change: iRacing's own monitor settings decide how the picture is split.
            string note = IniNumMonitors(path) == 1 ? "iRacing itself is set up for one screen, so the side screens look stretched: in iRacing, open Settings > Display > Monitor and set it up for 3 screens" : null;
            var desc = "your three racing screens (" + width + "×" + row3[0].Height + ")";
            return Write(r, cfg, path, want, apply, "across " + desc, "back across " + desc, note);
        }

        // Shared tail: write (or preview) the wanted values and describe what happened.
        static GuardResult Write(GuardResult r, DisplayGuardConfig cfg, string path, Dictionary<string, Dictionary<string, string>> want, bool apply, string okWhere, string fixedWhere, string note)
        {
            var patch = IniFile.Patch(path, want, apply);
            r.Changes = patch.Changes;
            var missing = patch.Missing;
            if (r.Changes.Count == 0)
                r.Message = "Screen settings OK: iRacing is " + okWhere + (cfg.EnforcesFpsCap ? ", FPS cap " + cfg.FpsLimit : "");
            else if (patch.ReadOnly)
            {
                r.Changes.Clear();
                return Skip(r, "iRacing's screen settings file is read-only, so it couldn't be fixed. Right-click it, choose Properties and untick Read-only");
            }
            else if (apply)
            {
                r.Backup = patch.Backup;
                r.Changed = patch.Written;
                r.Message = "Put iRacing " + fixedWhere + " (" + r.Changes.Count + " setting" + (r.Changes.Count == 1 ? "" : "s") + " fixed): " + string.Join(", ", r.Changes.ToArray());
            }
            else r.Message = "Would fix (" + okWhere + "): " + string.Join(", ", r.Changes.ToArray());
            if (missing.Count > 0) r.Message += " (not in the file: " + string.Join(", ", missing.ToArray()) + ")";
            if (note != null) r.Message += ". " + note;
            return r;
        }

        static bool IdMatches(MonitorInfo m, string id) { return id != null && (m.MonitorId ?? "").IndexOf(id.Trim(), StringComparison.OrdinalIgnoreCase) >= 0; }

        // Three same-size screens in a row: same top edge, each starting where the one before ends. Left to right.
        public static List<List<MonitorInfo>> TripleRows(List<MonitorInfo> mons)
        {
            var rows = new List<List<MonitorInfo>>();
            if (mons == null) return rows;
            Func<MonitorInfo, MonitorInfo, bool> next = (a, b) => b != a && b.Width > 0 && b.Y == a.Y && b.Width == a.Width && b.Height == a.Height && b.X == a.X + a.Width;
            foreach (var a in mons.Where(m => m.Width > 0 && m.Height > 0).OrderBy(m => m.X))
            {
                var b = mons.FirstOrDefault(m => next(a, m)); if (b == null) continue;
                var c = mons.FirstOrDefault(m => m != a && next(b, m)); if (c == null) continue;
                rows.Add(new List<MonitorInfo> { a, b, c });
            }
            return rows;
        }

        // One very wide display: almost always three screens joined by NVIDIA Surround or AMD Eyefinity
        // (5760×1080 is 16:3). A 32:9 super-ultrawide is 3.6:1, so it doesn't count.
        public static bool LooksJoined(MonitorInfo m) { return m != null && m.Height > 0 && m.Width >= 4 * m.Height; }

        // iRacing's own [MonitorSetup] NumMonitors (1 or 3), or 0 if the file doesn't say.
        public static int IniNumMonitors(string path)
        {
            try
            {
                var ini = IniFile.Read(path);
                Dictionary<string, string> k; string v; int n;
                if (ini.TryGetValue("MonitorSetup", out k) && k.TryGetValue("NumMonitors", out v) && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            }
            catch { }
            return 0;
        }

        static GuardResult Skip(GuardResult r, string why) { r.Skipped = true; r.Message = why; return r; }

        // Cheap fingerprint of everything the guard depends on: the ini's write time and size, the monitor
        // layout and the guard settings. When it hasn't changed, there's nothing new to check.
        public static string Signature(DisplayGuardConfig cfg)
        {
            try
            {
                var path = cfg == null || string.IsNullOrWhiteSpace(cfg.IniPath) ? DefaultIniPath : cfg.IniPath;
                var fi = new FileInfo(path);
                var file = fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : "missing";
                var mons = string.Join("|", ListMonitors().Select(m => m.ToString()).ToArray());
                var c = cfg == null ? "" : cfg.Enabled + "," + cfg.MonitorId + "," + cfg.Width + "x" + cfg.Height + "@" + cfg.RefreshRate + "," + cfg.FullScreen + "," + cfg.FpsLimit + "," + cfg.KeepFpsCap + "," + (cfg.IsTriple ? string.Join("/", cfg.Triple.ToArray()) : "");
                return file + "#" + mons + "#" + c;
            }
            catch (Exception ex) { return "error:" + ex.Message; }
        }

        // ---------------- monitors (user32, read-only) ----------------
        const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4, EnumCurrentSettings = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        // ---------------- DXGI (the output order a DX11 game like iRacing enumerates) ----------------
        // DXGI lists the PRIMARY output first, then the others. That only matches Windows' DISPLAYn order
        // while DISPLAY1 is primary; with another screen as primary the two orders differ.
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DxEnumFn(IntPtr self, uint index, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DxOutDescFn(IntPtr self, out DXGI_OUTPUT_DESC desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint DxReleaseFn(IntPtr self);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            public int AttachedToDesktop, Rotation;
            public IntPtr Monitor;
        }
        static T Slot<T>(IntPtr obj, int slot) where T : class
        {
            var vtbl = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T)) as T;
        }
        static void DxRelease(IntPtr o) { if (o != IntPtr.Zero) { try { Slot<DxReleaseFn>(o, 2)(o); } catch { } } }

        // Device names (\\.\DISPLAYn) in DXGI order across all adapters; null if DXGI isn't available.
        public static List<string> DxgiOrder()
        {
            try
            {
                var names = new List<string>();
                var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
                IntPtr factory;
                if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == IntPtr.Zero) return null;
                try
                {
                    for (uint a = 0; a < 16; a++)
                    {
                        IntPtr adapter;
                        if (Slot<DxEnumFn>(factory, 12)(factory, a, out adapter) != 0) break;   // IDXGIFactory1::EnumAdapters1
                        try
                        {
                            for (uint o = 0; o < 16; o++)
                            {
                                IntPtr output;
                                if (Slot<DxEnumFn>(adapter, 7)(adapter, o, out output) != 0) break; // IDXGIAdapter::EnumOutputs
                                try
                                {
                                    DXGI_OUTPUT_DESC d;
                                    if (Slot<DxOutDescFn>(output, 7)(output, out d) == 0 && !string.IsNullOrEmpty(d.DeviceName)) names.Add(d.DeviceName); // IDXGIOutput::GetDesc
                                }
                                finally { DxRelease(output); }
                            }
                        }
                        finally { DxRelease(adapter); }
                    }
                }
                finally { DxRelease(factory); }
                return names.Count > 0 ? names : null;
            }
            catch { return null; }
        }

        // Monitors attached to the desktop. Index = iRacing's deviceIdx: the position in DXGI's output order.
        // If DXGI can't be read, falls back to its rule: primary first, then the rest in DISPLAYn order.
        public static List<MonitorInfo> ListMonitors()
        {
            var fake = FakeMonitors(); if (fake != null) return fake;
            var raw = ListMonitorsRaw();
            var order = DxgiOrder();
            List<MonitorInfo> sorted;
            if (order != null && raw.All(m => order.Contains(m.DeviceName, StringComparer.OrdinalIgnoreCase)))
                sorted = raw.OrderBy(m => order.FindIndex(n => string.Equals(n, m.DeviceName, StringComparison.OrdinalIgnoreCase))).ToList();
            else
                sorted = raw.OrderBy(m => m.Primary ? 0 : 1).ThenBy(m => m.Index).ToList();
            for (int i = 0; i < sorted.Count; i++) sorted[i].Index = i;
            return sorted;
        }

        // Trials only: under RACEMODE_TEST_ROOT, RACEMODE_TEST_MONITORS stands in for the real screens, as
        // "id,x,y,width,height,hz[,primary]" entries separated by ';' (listed in deviceIdx order). Ignored otherwise.
        static List<MonitorInfo> FakeMonitors()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RACEMODE_TEST_ROOT"))) return null;
            var spec = Environment.GetEnvironmentVariable("RACEMODE_TEST_MONITORS");
            if (string.IsNullOrWhiteSpace(spec)) return null;
            var list = new List<MonitorInfo>();
            foreach (var e in spec.Split(';'))
            {
                var f = e.Split(','); int x, y, w, h, hz;
                if (f.Length < 6 || !int.TryParse(f[1], out x) || !int.TryParse(f[2], out y) || !int.TryParse(f[3], out w) || !int.TryParse(f[4], out h) || !int.TryParse(f[5], out hz)) continue;
                list.Add(new MonitorInfo { Index = list.Count, MonitorId = f[0].Trim(), DeviceName = "DISPLAY" + (list.Count + 1), Name = "Test screen", X = x, Y = y, Width = w, Height = h, Hz = hz, Primary = f.Length > 6 && f[6].Trim() == "primary" });
            }
            return list.Count > 0 ? list : null;
        }

        // Monitors attached to the desktop, in Windows' DISPLAYn order (Index = position in that order).
        static List<MonitorInfo> ListMonitorsRaw()
        {
            var list = new List<MonitorInfo>();
            for (uint i = 0; i < 32; i++)
            {
                var dd = new DISPLAY_DEVICE(); dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & AttachedToDesktop) == 0) continue;
                var info = new MonitorInfo { Index = list.Count, DeviceName = dd.DeviceName, Primary = (dd.StateFlags & PrimaryDevice) != 0 };
                var mon = new DISPLAY_DEVICE(); mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0))
                {
                    info.Name = mon.DeviceString ?? "";
                    var parts = (mon.DeviceID ?? "").Split('\\');   // MONITOR\<EDID id>\{guid}\0001
                    info.MonitorId = parts.Length > 1 ? parts[1] : (mon.DeviceID ?? "");
                }
                var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(dd.DeviceName, EnumCurrentSettings, ref dm))
                {
                    info.X = dm.dmPositionX; info.Y = dm.dmPositionY;
                    info.Width = dm.dmPelsWidth; info.Height = dm.dmPelsHeight; info.Hz = dm.dmDisplayFrequency;
                }
                list.Add(info);
            }
            return list;
        }
    }
}
