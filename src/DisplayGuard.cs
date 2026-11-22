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

        public bool EnforcesFpsCap { get { return KeepFpsCap && FpsLimit > 0; } }
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

                var patch = IniFile.Patch(path, want, apply);
                r.Changes = patch.Changes;
                var missing = patch.Missing;

                var targetDesc = "your " + target.Width + "×" + target.Height + " racing monitor";
                // A borderless window runs at whatever mode Windows has the monitor in, so flag a dropped
                // refresh rate or resolution (Windows sometimes falls back to 60 Hz after updates or wake).
                if (target.Hz > 0 && target.Hz < cfg.RefreshRate)
                    r.Warning = "Windows has your racing monitor at " + target.Hz + " Hz, not " + cfg.RefreshRate + " Hz. Change it in Settings > Display > Advanced display";
                else if (target.Width != cfg.Width || target.Height != cfg.Height)
                    r.Warning = "Windows has your racing monitor at " + target.Width + "×" + target.Height + ", not " + cfg.Width + "×" + cfg.Height + ". Change it in Settings > Display";
                if (r.Changes.Count == 0)
                {
                    r.Message = "Screen settings OK: iRacing is on " + targetDesc + (cfg.EnforcesFpsCap ? ", FPS cap " + cfg.FpsLimit : "");
                    if (missing.Count > 0) r.Message += " (not in the file: " + string.Join(", ", missing.ToArray()) + ")";
                    return r;
                }

                if (patch.ReadOnly)
                {
                    r.Changes.Clear();
                    return Skip(r, "iRacing's screen settings file is read-only, so it couldn't be fixed. Right-click it, choose Properties and untick Read-only");
                }
                if (apply)
                {
                    r.Backup = patch.Backup;
                    r.Changed = patch.Written;
                    r.Message = "Put iRacing back on " + targetDesc + " (" + r.Changes.Count + " setting" + (r.Changes.Count == 1 ? "" : "s") + " fixed): " + string.Join(", ", r.Changes.ToArray());
                }
                else r.Message = "Would fix (" + targetDesc + "): " + string.Join(", ", r.Changes.ToArray());
                if (missing.Count > 0) r.Message += " (not in the file: " + string.Join(", ", missing.ToArray()) + ")";
                return r;
            }
            catch (Exception ex)
            {
                Shell.LogCrash(ex);
                r.Changed = false; r.Changes.Clear();
                return Skip(r, "The screen check didn't work, so iRacing's settings were left as they were: " + ex.Message);
            }
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
                var c = cfg == null ? "" : cfg.Enabled + "," + cfg.MonitorId + "," + cfg.Width + "x" + cfg.Height + "@" + cfg.RefreshRate + "," + cfg.FullScreen + "," + cfg.FpsLimit + "," + cfg.KeepFpsCap;
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
