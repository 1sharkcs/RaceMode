// Race Mode - system monitor and logger.
// Reads Windows performance counters, NVIDIA's NVML library (no child processes),
// and optionally HWiNFO's shared sensor values. Never opens other processes except iRacing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace RaceMode
{
    public class LiveStats
    {
        public DateTime Time;
        public double Cpu, CpuMaxCore, CpuMHz, CpuTemp = double.NaN;
        public double RamUsedGB, RamTotalGB, CommitGB, CommitLimitGB, DiskBusy;
        public double GpuUtil = double.NaN, GpuTemp = double.NaN, GpuPower = double.NaN, GpuClock = double.NaN;
        public double GpuMemClock = double.NaN, VramUsedMB = double.NaN, VramTotalMB = double.NaN, GpuFan = double.NaN;
        public string GpuLimit = "";
        public bool SimRunning;
        public double SimCpu, SimRamMB;
        public string SimPriority = "";
        // From iRacing's own telemetry (Irsdk.cs). NaN / "" when the sim isn't connected.
        public double Fps = double.NaN, SimCpuFg = double.NaN, SimCpuBg = double.NaN, SimGpu = double.NaN;
        public string SimContext = "";
    }

    static class Nvml
    {
        [StructLayout(LayoutKind.Sequential)] public struct Util { public uint gpu, memory; }
        [StructLayout(LayoutKind.Sequential)] public struct Mem { public ulong total, free, used; }
        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] public static extern int Init();
        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")] public static extern int Shutdown();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] public static extern int GetHandle(uint index, out IntPtr dev);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] public static extern int GetUtil(IntPtr dev, out Util u);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] public static extern int GetTemp(IntPtr dev, int sensor, out uint t);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")] public static extern int GetPower(IntPtr dev, out uint mw);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")] public static extern int GetClock(IntPtr dev, int type, out uint mhz);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] public static extern int GetMem(IntPtr dev, out Mem m);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")] public static extern int GetFan(IntPtr dev, out uint pct);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")] public static extern int GetThrottle(IntPtr dev, out ulong reasons);
    }

    public class Monitor
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        // Summaries get copied and shared: show the user's own folder as %USERPROFILE%, not their account name.
        static string HidePrivatePath(string path)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(home)) return path;
            return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "%USERPROFILE%" + path.Substring(home.Length) : path;
        }
        public const string SimName = "iRacingSim64DX11";

        readonly object _lock = new object();
        Thread _thread;
        volatile bool _stop;
        int _intervalMs = 2000;
        LiveStats _live = new LiveStats();

        PerformanceCounter _cpuTotal, _cpuPerf, _diskIdle;
        readonly List<PerformanceCounter> _cores = new List<PerformanceCounter>();
        double _baseMHz;
        bool _nvmlOk; IntPtr _gpu;

        // iRacing CPU tracking
        int _simPid = -1; TimeSpan _simLastCpu; DateTime _simLastTime;
        readonly Irsdk _irsdk = new Irsdk();

        // The FPS cap iRacing runs at, set by the window; used for "time below cap" in summary.txt. 0 = unknown.
        public volatile int FpsCap;

        // per-process CPU via the "Process" counter category (no process handles opened)
        Dictionary<string, CounterSample> _procPrev = new Dictionary<string, CounterSample>();
        DateTime _lastProcSample = DateTime.MinValue;

        // logging
        string _logDir; DateTime _logStart;
        StreamWriter _sys, _procs, _events;
        Stats _stats; int _rows;

        class Stats
        {
            public int N, SimN, ProcSamples;
            public double CpuSum, CpuMax, CoreMax, GpuSum, GpuMax, GpuTempMax, GpuPowerMax, CpuTempMax = double.NaN;
            public double MinFreeRam = double.MaxValue, CommitMax, CommitLimit, SimCpuSum, MinClock = double.MaxValue;
            public Dictionary<string, double> ProcCpu = new Dictionary<string, double>();
            public Dictionary<string, int> Limits = new Dictionary<string, int>();
            // Frame rate per context ("drive", "replay") from iRacing telemetry; one value per sample.
            public Dictionary<string, List<float>> Fps = new Dictionary<string, List<float>>();
            public int FgSamples, CpuBoundSamples; public double FgMax;
            // Streaming (OBS): first and last report while live, and the bitrate.
            public int ObsN, KbpsN; public double KbpsSum, KbpsMin = double.MaxValue; public ObsStats ObsFirst, ObsLast;
        }

        public bool IsLogging { get { return _sys != null; } }
        public string LogDir { get { return _logDir; } }
        public LiveStats Live { get { lock (_lock) return _live; } }

        public void Start(int intervalMs)
        {
            if (_thread != null) return;
            _intervalMs = Math.Max(1000, intervalMs);
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "RaceModeMonitor" };
            _thread.Start();
        }

        public void SetInterval(int ms) { _intervalMs = Math.Max(1000, ms); }

        public void Stop()
        {
            _stop = true;
            try { if (_thread != null) _thread.Join(3000); } catch { }
            _thread = null;
        }

        // ---------- logging ----------
        public void StartLogging(string dir)
        {
            lock (_lock)
            {
                CloseWriters();
                Directory.CreateDirectory(dir);
                _logDir = dir; _logStart = DateTime.Now; _stats = new Stats(); _rows = 0;
                _procPrev.Clear(); _lastProcSample = DateTime.MinValue; // no CPU deltas spanning two sessions
                var enc = new UTF8Encoding(false);
                _sys = new StreamWriter(Path.Combine(dir, "system.csv"), false, enc);
                _sys.WriteLine("time,cpu_pct,cpu_busiest_core_pct,cpu_mhz,cpu_temp_c,ram_used_gb,ram_total_gb,commit_gb,commit_limit_gb,disk_busy_pct,gpu_pct,gpu_temp_c,gpu_power_w,gpu_clock_mhz,gpu_mem_clock_mhz,vram_used_mb,vram_total_mb,gpu_fan_pct,gpu_limit,sim_running,sim_cpu_pct,sim_ram_mb,sim_priority,fps,sim_fg_pct,sim_bg_pct,sim_gpu_pct,sim_context,obs_streaming,obs_kbps,obs_dropped,obs_render_lagged,obs_encode_lagged,obs_congestion");
                _procs = new StreamWriter(Path.Combine(dir, "processes.csv"), false, enc);
                _procs.WriteLine("time,process,cpu_pct_of_total");
                _events = new StreamWriter(Path.Combine(dir, "events.log"), true, enc);
            }
            Event("Logging started, every " + (_intervalMs / 1000) + "s");
        }

        public void Event(string msg)
        {
            // Never throws: it's called from the sampling thread's catch and from TurnOff, where an
            // exception (disk full, log folder deleted) would kill the process or leave Race Mode stuck on.
            lock (_lock)
            {
                if (_events == null) return;
                try { _events.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) + "  " + msg); _events.Flush(); } catch { }
            }
        }

        public string StopLogging()
        {
            lock (_lock)
            {
                if (_sys == null) return null;
                var summary = BuildSummary();
                try { File.WriteAllText(Path.Combine(_logDir, "summary.txt"), summary, new UTF8Encoding(false)); } catch { }
                try { _events.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) + "  Logging stopped"); } catch { }
                CloseWriters();
                return summary;
            }
        }

        void CloseWriters()
        {
            foreach (var w in new[] { _sys, _procs, _events }) { try { if (w != null) w.Dispose(); } catch { } }
            _sys = null; _procs = null; _events = null;
        }

        // ---------- sampling ----------
        void Init()
        {
            try { _cpuTotal = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); _cpuTotal.NextValue(); }
            catch { try { _cpuTotal = new PerformanceCounter("Processor Information", "% Processor Time", "_Total"); _cpuTotal.NextValue(); } catch { _cpuTotal = null; } }
            try { _cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total"); _cpuPerf.NextValue(); } catch { _cpuPerf = null; }
            try { _diskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total"); _diskIdle.NextValue(); } catch { _diskIdle = null; }
            try
            {
                foreach (var inst in new PerformanceCounterCategory("Processor Information").GetInstanceNames())
                {
                    if (inst.Contains("_Total")) continue;
                    var pc = new PerformanceCounter("Processor Information", "% Processor Time", inst);
                    pc.NextValue(); _cores.Add(pc);
                }
            }
            catch { }
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (k != null) _baseMHz = Convert.ToDouble(k.GetValue("~MHz", 0));
            }
            catch { }
            try { _nvmlOk = Nvml.Init() == 0 && Nvml.GetHandle(0, out _gpu) == 0; } catch { _nvmlOk = false; }
        }

        void Loop()
        {
            Init();
            Thread.Sleep(800);
            while (!_stop)
            {
                var t0 = DateTime.Now;
                try { Sample(); } catch (Exception ex) { Event("Sample error: " + ex.Message); }
                var wait = _intervalMs - (int)(DateTime.Now - t0).TotalMilliseconds;
                for (int slept = 0; slept < wait && !_stop; slept += 100) Thread.Sleep(100);
            }
            try { if (_nvmlOk) Nvml.Shutdown(); } catch { }
            _irsdk.Dispose();
        }

        // True when HWiNFO is set to Fahrenheit; the UI then shows temperatures in °F too. Values stay °C internally.
        public static volatile bool HwinfoFahrenheit;

        static DateTime _hwCheck = DateTime.MinValue; static bool _hwRunning = true;
        // Why the last read had no CPU temperature (shown in the tile's tooltip); "ok" when it worked.
        public static volatile string HwinfoReason = "not read yet";

        static double HwinfoCpuTemp()
        {
            // HWiNFO -> Sensor settings -> HWiNFO Gadget -> tick "Report value in Gadget" for a CPU temperature.
            try
            {
                // If HWiNFO quits without clearing its values, the registry keeps the last reading forever.
                // Only trust it while HWiNFO is actually running (checked every 5 s).
                if ((DateTime.Now - _hwCheck).TotalSeconds >= 5)
                {
                    _hwCheck = DateTime.Now;
                    // If the check itself fails, keep trusting the registry rather than going blank.
                    try { var ps = Process.GetProcessesByName("HWiNFO64"); _hwRunning = ps.Length > 0; foreach (var p in ps) p.Dispose(); }
                    catch (Exception ex) { _hwRunning = true; HwinfoReason = "process check failed: " + ex.Message; }
                }
                if (!_hwRunning) { HwinfoReason = "HWiNFO64 isn't running"; return double.NaN; }
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\HWiNFO64\VSB"))
                {
                    if (k == null) { HwinfoReason = "HWiNFO is sharing nothing (no Gadget values)"; return double.NaN; }
                    int seen = 0;
                    double best = double.NaN; int bestRank = 99;
                    for (int i = 0; i < 2000; i++)
                    {
                        var label = k.GetValue("Label" + i) as string;
                        if (label == null) break;
                        seen++;
                        // Only temperatures: "CPU Package Power" (W) must never be read as degrees.
                        var shown = Convert.ToString(k.GetValue("Value" + i)) ?? "";
                        if (shown.IndexOf("°C", StringComparison.Ordinal) < 0 && shown.IndexOf("°F", StringComparison.Ordinal) < 0) continue;
                        var l = label.ToLowerInvariant(); int rank = 99;
                        // Intel: "CPU Package", "Core Max". AMD Ryzen: "CPU (Tctl/Tdie)", "CPU Die (average)".
                        if (l.Contains("cpu package") || l.Contains("tctl") || l.Contains("tdie")) rank = 0;
                        else if (l.Contains("core max") || l.Contains("cpu die")) rank = 1;
                        else if (l.Contains("cpu") && l.Contains("temp")) rank = 2;
                        if (rank >= bestRank) continue;
                        double d;
                        if (double.TryParse(Convert.ToString(k.GetValue("ValueRaw" + i)), NumberStyles.Float, Inv, out d)) { var isF = shown.IndexOf("°F", StringComparison.Ordinal) >= 0; HwinfoFahrenheit = isF; best = isF ? (d - 32) * 5 / 9 : d; bestRank = rank; if (rank == 0) break; }
                    }
                    HwinfoReason = double.IsNaN(best) ? "no CPU temperature among " + seen + " shared values" : "ok";
                    return best;
                }
            }
            catch (Exception ex) { HwinfoReason = "read failed: " + ex.Message; return double.NaN; }
        }

        static string LimitText(ulong v)
        {
            if (v == 0) return "none";
            var p = new List<string>();
            if ((v & 0x1) != 0) p.Add("idle");
            if ((v & 0x4) != 0) p.Add("power_cap");
            if ((v & 0x8) != 0) p.Add("hw_slowdown");
            if ((v & 0x20) != 0) p.Add("thermal_sw");
            if ((v & 0x40) != 0) p.Add("thermal_hw");
            if ((v & 0x80) != 0) p.Add("power_brake");
            return p.Count == 0 ? "other" : string.Join("+", p);
        }

        void Sample()
        {
            var s = new LiveStats { Time = DateTime.Now };
            if (_cpuTotal != null) s.Cpu = Math.Min(100, _cpuTotal.NextValue());
            double maxCore = 0;
            foreach (var c in _cores) { try { maxCore = Math.Max(maxCore, c.NextValue()); } catch { } }
            s.CpuMaxCore = Math.Min(100, maxCore);
            if (_cpuPerf != null && _baseMHz > 0) s.CpuMHz = _baseMHz * _cpuPerf.NextValue() / 100.0;
            if (_diskIdle != null) s.DiskBusy = Math.Max(0, 100 - _diskIdle.NextValue());
            s.CpuTemp = HwinfoCpuTemp();

            var m = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(m))
            {
                const double GB = 1073741824.0;
                s.RamTotalGB = m.ullTotalPhys / GB; s.RamUsedGB = (m.ullTotalPhys - m.ullAvailPhys) / GB;
                s.CommitLimitGB = m.ullTotalPageFile / GB; s.CommitGB = (m.ullTotalPageFile - m.ullAvailPageFile) / GB;
            }

            if (_nvmlOk)
            {
                Nvml.Util u; uint v; Nvml.Mem mem; ulong thr;
                if (Nvml.GetUtil(_gpu, out u) == 0) s.GpuUtil = u.gpu;
                if (Nvml.GetTemp(_gpu, 0, out v) == 0) s.GpuTemp = v;
                if (Nvml.GetPower(_gpu, out v) == 0) s.GpuPower = v / 1000.0;
                if (Nvml.GetClock(_gpu, 0, out v) == 0) s.GpuClock = v;
                if (Nvml.GetClock(_gpu, 2, out v) == 0) s.GpuMemClock = v;
                if (Nvml.GetMem(_gpu, out mem) == 0) { s.VramUsedMB = mem.used / 1048576.0; s.VramTotalMB = mem.total / 1048576.0; }
                if (Nvml.GetFan(_gpu, out v) == 0) s.GpuFan = v;
                if (Nvml.GetThrottle(_gpu, out thr) == 0) s.GpuLimit = LimitText(thr);
            }

            var sims = Process.GetProcessesByName(SimName);
            try
            {
                if (sims.Length > 0)
                {
                    var p = sims[0]; s.SimRunning = true;
                    var now = DateTime.Now; var cpu = p.TotalProcessorTime;
                    if (p.Id == _simPid)
                    {
                        var dt = (now - _simLastTime).TotalMilliseconds;
                        if (dt > 0) s.SimCpu = (cpu - _simLastCpu).TotalMilliseconds / dt * 100.0 / Environment.ProcessorCount;
                    }
                    _simPid = p.Id; _simLastCpu = cpu; _simLastTime = now;
                    s.SimRamMB = p.WorkingSet64 / 1048576.0;
                    s.SimPriority = p.PriorityClass.ToString();
                }
                else _simPid = -1;
            }
            catch { }
            finally { foreach (var p in sims) p.Dispose(); }

            // Frame rate and the sim's own load, read from iRacing's telemetry (read-only shared memory).
            var t = _irsdk.Read(s.SimRunning, _simPid, Event);
            if (t != null && t.Connected)
            {
                s.Fps = t.Fps; s.SimCpuFg = t.CpuFg; s.SimCpuBg = t.CpuBg; s.SimGpu = t.Gpu; s.SimContext = t.Context;
            }

            lock (_lock) _live = s;

            if (_sys != null)
            {
                WriteRow(s);
                if ((DateTime.Now - _lastProcSample).TotalSeconds >= 10) SampleProcesses(s.Time);
            }
        }

        static string F(double d, string fmt) { return double.IsNaN(d) ? "" : d.ToString(fmt, Inv); }

        void WriteRow(LiveStats s)
        {
            // OBS columns only while OBS stats are on (Stream mode); blank otherwise.
            var o = Obs.Active ? Obs.Latest : null;
            if (o != null && !o.Connected) o = null;
            lock (_lock)
            {
                if (_sys == null) return;
                _sys.WriteLine(string.Join(",", new[] {
                    s.Time.ToString("yyyy-MM-dd HH:mm:ss", Inv), F(s.Cpu,"0.0"), F(s.CpuMaxCore,"0.0"), F(s.CpuMHz,"0"), F(s.CpuTemp,"0.0"),
                    F(s.RamUsedGB,"0.00"), F(s.RamTotalGB,"0.00"), F(s.CommitGB,"0.00"), F(s.CommitLimitGB,"0.00"), F(s.DiskBusy,"0.0"),
                    F(s.GpuUtil,"0"), F(s.GpuTemp,"0"), F(s.GpuPower,"0.0"), F(s.GpuClock,"0"), F(s.GpuMemClock,"0"),
                    F(s.VramUsedMB,"0"), F(s.VramTotalMB,"0"), F(s.GpuFan,"0"), s.GpuLimit,
                    s.SimRunning ? "1" : "0", F(s.SimCpu,"0.0"), F(s.SimRamMB,"0"), s.SimPriority,
                    F(s.Fps,"0.0"), F(s.SimCpuFg,"0.0"), F(s.SimCpuBg,"0.0"), F(s.SimGpu,"0.0"), s.SimContext,
                    o == null ? "" : o.Streaming ? "1" : "0", o == null ? "" : F(o.Kbps,"0"), o == null ? "" : o.NetDropped.ToString(Inv),
                    o == null ? "" : o.RenderSkipped.ToString(Inv), o == null ? "" : o.EncodeSkipped.ToString(Inv), o == null ? "" : F(o.Congestion,"0.00") }));
                if (++_rows % 5 == 0) _sys.Flush();

                var st = _stats; st.N++;
                st.CpuSum += s.Cpu; st.CpuMax = Math.Max(st.CpuMax, s.Cpu); st.CoreMax = Math.Max(st.CoreMax, s.CpuMaxCore);
                if (s.CpuMHz > 0) st.MinClock = Math.Min(st.MinClock, s.CpuMHz);
                if (!double.IsNaN(s.CpuTemp)) st.CpuTempMax = double.IsNaN(st.CpuTempMax) ? s.CpuTemp : Math.Max(st.CpuTempMax, s.CpuTemp);
                if (!double.IsNaN(s.GpuUtil)) { st.GpuSum += s.GpuUtil; st.GpuMax = Math.Max(st.GpuMax, s.GpuUtil); }
                if (!double.IsNaN(s.GpuTemp)) st.GpuTempMax = Math.Max(st.GpuTempMax, s.GpuTemp);
                if (!double.IsNaN(s.GpuPower)) st.GpuPowerMax = Math.Max(st.GpuPowerMax, s.GpuPower);
                st.MinFreeRam = Math.Min(st.MinFreeRam, s.RamTotalGB - s.RamUsedGB);
                if (s.CommitGB >= st.CommitMax) { st.CommitMax = s.CommitGB; st.CommitLimit = s.CommitLimitGB; }
                if (s.SimRunning) { st.SimCpuSum += s.SimCpu; st.SimN++; }
                if (o != null && o.Streaming)
                {
                    if (st.ObsN == 0) { st.ObsFirst = o; }
                    st.ObsLast = o; st.ObsN++;
                    if (!double.IsNaN(o.Kbps)) { st.KbpsSum += o.Kbps; st.KbpsN++; st.KbpsMin = Math.Min(st.KbpsMin, o.Kbps); }
                }
                if (!double.IsNaN(s.Fps) && (s.SimContext == "drive" || s.SimContext == "replay"))
                {
                    List<float> l; if (!st.Fps.TryGetValue(s.SimContext, out l)) st.Fps[s.SimContext] = l = new List<float>();
                    l.Add((float)s.Fps);
                }
                if (!double.IsNaN(s.SimCpuFg) && s.SimContext == "drive")
                {
                    st.FgSamples++; st.FgMax = Math.Max(st.FgMax, s.SimCpuFg);
                    // The sim's main thread is maxed while the GPU still has room: the CPU is what limits FPS.
                    if (s.SimCpuFg >= 90 && (double.IsNaN(s.SimGpu) || s.SimGpu < 80)) st.CpuBoundSamples++;
                }
                if (!string.IsNullOrEmpty(s.GpuLimit) && s.GpuLimit != "none" && s.GpuLimit != "idle")
                {
                    int c; st.Limits.TryGetValue(s.GpuLimit, out c); st.Limits[s.GpuLimit] = c + 1;
                }
            }
        }

        void SampleProcesses(DateTime time)
        {
            _lastProcSample = DateTime.Now;
            InstanceDataCollectionCollection data;
            try { data = new PerformanceCounterCategory("Process").ReadCategory(); } catch { return; }
            var cpu = data["% Processor Time"];
            if (cpu == null) return;
            var cur = new Dictionary<string, CounterSample>();
            var totals = new Dictionary<string, double>();
            foreach (InstanceData inst in cpu.Values)
            {
                var name = inst.InstanceName;
                if (name == "_Total" || name == "Idle") continue;
                cur[name] = inst.Sample;
                CounterSample prev;
                if (_procPrev.TryGetValue(name, out prev))
                {
                    var pct = CounterSample.Calculate(prev, inst.Sample) / Environment.ProcessorCount;
                    var baseName = name.Contains("#") ? name.Substring(0, name.IndexOf('#')) : name;
                    double t; totals.TryGetValue(baseName, out t); totals[baseName] = t + pct;
                }
            }
            _procPrev = cur;
            if (totals.Count == 0) return;
            lock (_lock)
            {
                if (_procs == null) return;
                var ts = time.ToString("yyyy-MM-dd HH:mm:ss", Inv);
                foreach (var kv in totals.OrderByDescending(k => k.Value).Take(15))
                    _procs.WriteLine(ts + "," + kv.Key.Replace(",", " ") + "," + kv.Value.ToString("0.0", Inv));
                _procs.Flush();
                _stats.ProcSamples++;
                foreach (var kv in totals)
                {
                    double t; _stats.ProcCpu.TryGetValue(kv.Key, out t); _stats.ProcCpu[kv.Key] = t + kv.Value;
                }
            }
        }

        // OBS counts frames since its stream started, so the session's share is last minus first.
        void AppendObs(StringBuilder sb, Stats st)
        {
            if (st.ObsN == 0) return;
            var a = st.ObsFirst; var b = st.ObsLast;
            Func<long, long, long, long, string> pct = (d0, d1, t0, t1) =>
            {
                long d = Math.Max(0, d1 - d0), t = Math.Max(0, t1 - t0);
                return d.ToString(Inv) + (t > 0 ? " (" + (100.0 * d / t).ToString(d > 0 && 100.0 * d / t < 0.1 ? "0.00" : "0.0", Inv) + "%)" : "");
            };
            sb.AppendLine("Stream   : " + (st.KbpsN > 0 ? "avg " + (st.KbpsSum / st.KbpsN).ToString("0", Inv) + " kbps, lowest " + st.KbpsMin.ToString("0", Inv) + " kbps" : "bitrate not seen") +
                          "   dropped (network) " + pct(a.NetDropped, b.NetDropped, a.NetTotal, b.NetTotal) +
                          "   lagged: rendering " + pct(a.RenderSkipped, b.RenderSkipped, a.RenderTotal, b.RenderTotal) +
                          ", encoding " + pct(a.EncodeSkipped, b.EncodeSkipped, a.EncodeTotal, b.EncodeTotal));
        }

        void AppendFps(StringBuilder sb, Stats st)
        {
            if (st.Fps.Count == 0) { sb.AppendLine("FPS      : not recorded (iRacing wasn't running while the log was on)"); return; }
            int cap = FpsCap;
            foreach (var ctx in new[] { "drive", "replay" })
            {
                List<float> l;
                if (!st.Fps.TryGetValue(ctx, out l) || l.Count == 0) continue;
                var sorted = new List<float>(l); sorted.Sort();
                double avg = 0; foreach (var f in l) avg += f; avg /= l.Count;
                double p5 = sorted[(int)Math.Floor((sorted.Count - 1) * 0.05)];
                var line = "FPS " + (ctx == "drive" ? "drive " : "replay") + ": avg " + avg.ToString("0", Inv) + "   lowest " + sorted[0].ToString("0", Inv) +
                           "   worst 5% below " + p5.ToString("0", Inv) + "   (" + l.Count + " samples)";
                if (cap > 0)
                {
                    int below = 0; foreach (var f in l) if (f < cap - 5) below++;
                    line += "   below " + (cap - 5) + ": " + (100.0 * below / l.Count).ToString("0", Inv) + "% of the time";
                }
                sb.AppendLine(line);
            }
            if (st.FgSamples > 0)
                sb.AppendLine("Sim load : iRacing's main thread peaked at " + st.FgMax.ToString("0", Inv) + "% of its time" +
                              (st.CpuBoundSamples * 10 >= st.FgSamples ? "   << Your CPU is what limits FPS (" + (100 * st.CpuBoundSamples / st.FgSamples) + "% of the time driving, iRacing was maxed while the graphics card had room to spare)" : ""));
            sb.AppendLine("           FPS is averaged over each second, so very short stutters won't show here.");
        }

        string BuildSummary()
        {
            var st = _stats; var sb = new StringBuilder();
            var dur = DateTime.Now - _logStart;
            sb.AppendLine("RACE MODE SESSION SUMMARY");
            sb.AppendLine("Started  : " + _logStart.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            sb.AppendLine("Duration : " + ((int)dur.TotalHours).ToString("00") + ":" + dur.Minutes.ToString("00") + ":" + dur.Seconds.ToString("00") + "  (" + st.N + " samples)");
            sb.AppendLine("Folder   : " + HidePrivatePath(_logDir));
            sb.AppendLine();
            if (st.N == 0) { sb.AppendLine("No samples recorded."); return sb.ToString(); }
            sb.AppendLine("CPU      : avg " + (st.CpuSum / st.N).ToString("0", Inv) + "%   peak " + st.CpuMax.ToString("0", Inv) + "%   busiest core peak " + st.CoreMax.ToString("0", Inv) + "%");
            if (st.MinClock < double.MaxValue) sb.AppendLine("CPU clock: lowest " + st.MinClock.ToString("0", Inv) + " MHz");
            sb.AppendLine("CPU temp : " + (double.IsNaN(st.CpuTempMax) ? "not available (needs HWiNFO with Gadget sharing on)" : "peak " + st.CpuTempMax.ToString("0", Inv) + " C"));
            sb.AppendLine("GPU      : avg " + (st.GpuSum / st.N).ToString("0", Inv) + "%   peak " + st.GpuMax.ToString("0", Inv) + "%   temp peak " + st.GpuTempMax.ToString("0", Inv) + " C   power peak " + st.GpuPowerMax.ToString("0", Inv) + " W");
            if (st.Limits.Count > 0)
                sb.AppendLine("GPU limit: " + string.Join(", ", st.Limits.OrderByDescending(k => k.Value).Select(k => k.Key + " (" + k.Value + " samples)")));
            sb.AppendLine("RAM      : lowest free " + st.MinFreeRam.ToString("0.0", Inv) + " GB");
            sb.AppendLine("Total use: peak " + st.CommitMax.ToString("0.0", Inv) + " of " + st.CommitLimit.ToString("0.0", Inv) + " GB (memory plus page file)" + (st.CommitMax > st.CommitLimit * 0.9 ? "   << NEARLY FULL: a bigger page file would help" : ""));
            AppendFps(sb, st);
            AppendObs(sb, st);
            sb.AppendLine("iRacing  : " + (st.SimN > 0 ? "avg " + (st.SimCpuSum / st.SimN).ToString("0.0", Inv) + "% of your whole CPU (" + st.SimN + " samples)" : "not seen"));
            if (st.ProcSamples > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Top CPU users (avg % of total CPU):");
                foreach (var kv in st.ProcCpu.OrderByDescending(k => k.Value).Take(10))
                    sb.AppendLine("  " + kv.Key.PadRight(30) + (kv.Value / st.ProcSamples).ToString("0.0", Inv));
            }
            return sb.ToString();
        }
    }
}
