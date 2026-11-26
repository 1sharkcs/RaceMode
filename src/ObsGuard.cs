// Is OBS live? Race Mode must never close, kill, lower or interrupt OBS (or any part of it) while it is streaming,
// recording, running its replay buffer or its virtual camera: a racer who is live can't have Race Mode end the stream.
//
// OBS writes every output start and stop to its log as it happens ("==== Recording Start ====", "==== Streaming Stop
// ===="), so the running OBS's own log says what is live, with no settings to change and no password. When Race Mode's
// stream stats are connected (obs-websocket), "streaming" from there counts too. If neither can say (no log for this
// OBS yet, OBS still starting up or started under a minute ago, a stream still connecting, the log can't be read),
// the answer is "unknown", and unknown is treated as live: OBS is left alone and Race Mode says so.
// Plugin outputs (Aitum Vertical, Multiple RTMP Outputs, NDI) don't write those log lines; an OBS whose log reads
// idle but which holds a network connection to another machine is "unknown" too (OBS's own update and what's-new
// connections to obsproject.com don't count). Known limit: a plugin that only
// writes a file (Source Record) holds no connection and can't be seen; OBS's own exit prompt doesn't cover it either.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RaceMode
{
    public enum ObsState { Idle, Live, Unknown }

    public class ObsStatus
    {
        public ObsState State = ObsState.Unknown;
        public List<string> Active = new List<string>();   // "streaming", "recording", "the replay buffer", "the virtual camera"
        public string Why = "";                             // why it's unknown, for the log
        // "streaming and recording": for "Left OBS open: it's streaming and recording"
        public string ActiveText { get { return Active.Count == 0 ? "" : Active.Count == 1 ? Active[0] : string.Join(", ", Active.Take(Active.Count - 1).ToArray()) + " and " + Active.Last(); } }
    }

    public static class ObsGuard
    {
        // OBS's own processes (a muxer killed mid-recording corrupts the file; browser sources carry overlays).
        static readonly string[] Family = { "obs64", "obs32", "obs", "obs-browser-page", "obs-ffmpeg-mux", "obs-amf-test", "obs-nvenc-test", "obs-qsv-test", "obs-vst-scanner" };

        // Other streaming apps: Race Mode can't read whether they are live, so by the same rule as an OBS it can't read
        // (unknown counts as live) they are never closed, even when they're on the Close list. Race Mode says so.
        static readonly string[] OtherStreamApps = { "Streamlabs Desktop", "Streamlabs OBS", "XSplit.Core", "XSplit.Broadcaster", "PRISMLiveStudio",
            "MeldStudio", "Meld Studio", "TwitchStudio", "Twitch Studio", "vMix64", "vMix", "Wirecast" };

        public static bool IsOtherStreamApp(string name)
        {
            return !string.IsNullOrEmpty(name) && OtherStreamApps.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        // OBS itself (the app a user means by "OBS"); everything else in Family is a part of it
        public static bool IsObsMain(string name)
        {
            return !string.IsNullOrEmpty(name) && (string.Equals(name, "obs64", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "obs32", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "obs", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsObs(string name)
        {
            return !string.IsNullOrEmpty(name) && Family.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        static readonly string[][] Outputs = {
            new[] { "streaming", "==== Streaming Start", "==== Streaming Stop" },
            new[] { "recording", "==== Recording Start", "==== Recording Stop" },
            new[] { "the replay buffer", "==== Replay Buffer Start", "==== Replay Buffer Stop" },
            new[] { "the virtual camera", "==== Virtual Camera Start", "==== Virtual Camera Stop" } };

        // What a running OBS's log says. The log of an OBS that hasn't finished starting is "unknown": it may be about
        // to start recording (--startrecording) or streaming (auto-start).
        public static ObsStatus FromLog(IEnumerable<string> lines)
        {
            return FromLog(lines, double.MaxValue);
        }

        // runningSeconds: how long this OBS has been running. One started under a minute ago that reads idle may still
        // be about to start recording or streaming on its own (auto-start, --startrecording): "unknown".
        public static ObsStatus FromLog(IEnumerable<string> lines, double runningSeconds)
        {
            var s = new ObsStatus();
            if (lines == null) { s.Why = "no log"; return s; }
            bool started = false;
            // A stream still connecting: OBS writes "Connecting to RTMP URL" first and "==== Streaming Start" only once
            // it's connected, so in between it read as idle while the racer's stream was about to go live.
            bool connecting = false;
            var on = new bool[Outputs.Length];
            foreach (var raw in lines)
            {
                if (raw == null) continue;
                if (raw.IndexOf("==== Startup complete", StringComparison.Ordinal) >= 0) { started = true; connecting = false; }
                if (raw.IndexOf("Connecting to RTMP URL", StringComparison.OrdinalIgnoreCase) >= 0) connecting = true;
                // a first connection that failed doesn't start the stream (OBS shows an error): no longer connecting
                else if (connecting && raw.IndexOf("[rtmp stream", StringComparison.OrdinalIgnoreCase) >= 0 && raw.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0) connecting = false;
                for (int i = 0; i < Outputs.Length; i++)
                {
                    // Outputs[0] is streaming: its Start or Stop ends the "connecting" wait
                    if (raw.IndexOf(Outputs[i][1], StringComparison.Ordinal) >= 0) { on[i] = true; if (i == 0) connecting = false; }
                    else if (raw.IndexOf(Outputs[i][2], StringComparison.Ordinal) >= 0) { on[i] = false; if (i == 0) connecting = false; }
                }
            }
            for (int i = 0; i < Outputs.Length; i++) if (on[i]) s.Active.Add(Outputs[i][0]);
            if (s.Active.Count > 0) s.State = ObsState.Live;
            else if (!started) { s.State = ObsState.Unknown; s.Why = "OBS is still starting"; }
            else if (connecting) { s.State = ObsState.Unknown; s.Why = "a stream is still connecting"; }
            else if (runningSeconds < 60) { s.State = ObsState.Unknown; s.Why = "OBS started under a minute ago"; }
            else s.State = ObsState.Idle;
            return s;
        }

        // The running OBS's own log. OBS names each log after the moment it started ("2026-11-23 11-07-25.txt"), so
        // the log whose name is closest to (and not before) this OBS's start is its own: with two OBS running at once
        // (--multi), "the newest log" was the other one's (found live 2026-11-23: a recording OBS read as idle).
        // Null when no log name matches within a minute of the start.
        public static string LogFor(DateTime obsStarted, IEnumerable<string> logsDirs)
        {
            string best = null; double bestGap = double.MaxValue;
            foreach (var d in logsDirs)
            {
                try
                {
                    if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d, "*.txt"))
                    {
                        DateTime t;
                        if (!LogNameTime(Path.GetFileNameWithoutExtension(f), out t)) continue;
                        double gap = (t - obsStarted).TotalSeconds;
                        if (gap < -2 || gap > 60) continue;   // a log starts within moments of its OBS
                        if (Math.Abs(gap) < bestGap) { bestGap = Math.Abs(gap); best = f; }
                    }
                }
                catch { }
            }
            return best;
        }

        // "2026-11-23 11-07-25" (OBS's log file name) -> that local time
        public static bool LogNameTime(string name, out DateTime t)
        {
            return DateTime.TryParseExact(name ?? "", "yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out t);
        }

        public static IEnumerable<string> LogDirs(string obsExe)
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"obs-studio\logs");
            if (!string.IsNullOrEmpty(obsExe))
            {
                // portable OBS (portable_mode.txt next to the install): <install>\config\obs-studio\logs
                string root = null;
                try { root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(obsExe), @"..\..")); } catch { }
                if (root != null) yield return Path.Combine(root, @"config\obs-studio\logs");
            }
        }

        // Reads a log OBS is still writing (shared read), one line at a time as FromLog asks for it: an OBS left running
        // for days can have a huge log, and holding all of it in memory at once isn't needed. A read that fails throws
        // to the caller (Now), which counts it as unknown.
        public static IEnumerable<string> ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs))
            {
                string l; while ((l = r.ReadLine()) != null) yield return l;
            }
        }

        // The process names of OBS itself (64-bit, 32-bit, and "obs" on some builds).
        public static readonly string[] MainNames = { "obs64", "obs32", "obs" };

        // Only an OBS in Race Mode's own Windows session counts (another signed-in user's OBS isn't one Race Mode
        // closes), and only one the app catalog accepts as OBS (another program can use the same exe name).
        static bool Ours(Process p, int mySession)
        {
            try
            {
                if (p.SessionId != mySession) return false;
                return AppScan.BelongsTo(p.ProcessName, AppScan.ImagePath(p.Id));
            }
            catch { return true; }   // can't tell: judged like any OBS (unknown if its log can't be read), never skipped
        }

        // Every running OBS: live if any is live; unknown if any can't be read; idle only when all are known idle.
        // statsStreaming: Race Mode's own stream stats say OBS is streaming (obs-websocket), if connected.
        public static ObsStatus Now(bool statsStreaming) { return Now(statsStreaming, false); }

        // statsRecording: the same connection says OBS is recording.
        public static ObsStatus Now(bool statsStreaming, bool statsRecording)
        {
            var all = new ObsStatus { State = ObsState.Idle };
            Process[] ps;
            int mySession;
            try
            {
                using (var me = Process.GetCurrentProcess()) mySession = me.SessionId;
                ps = MainNames.SelectMany(n => Process.GetProcessesByName(n)).ToArray();
            }
            catch { return new ObsStatus { Why = "couldn't list processes" }; }
            try
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in ps)
                {
                    if (!Ours(p, mySession)) continue;
                    ObsStatus one;
                    try
                    {
                        string exe = AppScan.ImagePath(p.Id);
                        if (exe == null) try { exe = p.MainModule.FileName; } catch { }
                        var startedAt = p.StartTime;
                        var log = LogFor(startedAt, LogDirs(exe));
                        if (log != null && !used.Add(log)) one = new ObsStatus { Why = "two OBS share one log" };   // can't tell them apart
                        else one = log == null ? new ObsStatus { Why = "no log for this OBS" } : FromLog(ReadShared(log), (DateTime.Now - startedAt).TotalSeconds);
                    }
                    catch (Exception ex) { one = new ObsStatus { Why = "couldn't read OBS's log (" + ex.Message + ")" }; }
                    // Plugin outputs (Aitum Vertical, Multiple RTMP Outputs, NDI, ...) don't write OBS's "==== Start"
                    // lines, so the log can read idle while one streams. Every live network output holds a connection
                    // out of OBS itself; an idle OBS normally holds none (its browser sources are separate processes).
                    if (one.State == ObsState.Idle && HasRemoteConnection(p.Id)) one = new ObsStatus { Why = "OBS has a network connection open (a plugin may be streaming)" };
                    foreach (var a in one.Active) if (!all.Active.Contains(a)) all.Active.Add(a);
                    if (one.State == ObsState.Unknown && all.State == ObsState.Idle) { all.State = ObsState.Unknown; all.Why = one.Why; }
                }
            }
            finally { foreach (var p in ps) p.Dispose(); }
            return WithStats(all, statsStreaming, statsRecording);
        }

        // Whether a process has an established TCP connection to another machine (IPv4 or IPv6; loopback doesn't
        // count). From Windows' connection table: no admin rights and no handle on the process needed. A table that
        // can't be read counts as "yes" (unknown is treated as live). Tests can replace it.
        public static Func<int, bool> HasRemoteConnection = OwnsRemoteTcp;

        [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
        const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidConnections = 4, TcpEstablished = 5;

        public static bool OwnsRemoteTcp(int pid)
        {
            if (pid <= 0) return false;
            foreach (int family in new[] { AfInet, AfInet6 })
            {
                IntPtr buf = IntPtr.Zero;
                try
                {
                    int size = 0;
                    GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidConnections, 0);
                    if (size <= 0) continue;
                    size += 4096;   // room for connections opened in between
                    buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
                    if (GetExtendedTcpTable(buf, ref size, false, family, TcpTableOwnerPidConnections, 0) != 0) return true;
                    int n = System.Runtime.InteropServices.Marshal.ReadInt32(buf);
                    int rowSize = family == AfInet ? 24 : 56;
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr row = buf + 4 + i * rowSize;
                        if (family == AfInet)
                        {
                            // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid
                            if (System.Runtime.InteropServices.Marshal.ReadInt32(row, 20) != pid) continue;
                            if (System.Runtime.InteropServices.Marshal.ReadInt32(row, 0) != TcpEstablished) continue;
                            var b = BitConverter.GetBytes(System.Runtime.InteropServices.Marshal.ReadInt32(row, 12));
                            if (CountsAsOutput(b, IsObsService)) return true;
                        }
                        else
                        {
                            // MIB_TCP6ROW_OWNER_PID: local addr[16], scope, port, remote addr[16] (at 24), scope, port, state (48), pid (52)
                            if (System.Runtime.InteropServices.Marshal.ReadInt32(row, 52) != pid) continue;
                            if (System.Runtime.InteropServices.Marshal.ReadInt32(row, 48) != TcpEstablished) continue;
                            var b = new byte[16];
                            System.Runtime.InteropServices.Marshal.Copy(row + 24, b, 0, 16);
                            if (CountsAsOutput(b, IsObsService)) return true;
                        }
                    }
                }
                catch { return true; }
                finally { if (buf != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
            }
            return false;
        }

        // Whether a connection out of OBS could be an output (a stream). Loopback can't. Nor can OBS's own service:
        // an idle OBS keeps connections open to obsproject.com for its update and what's-new checks (found live
        // 2026-11-23), which made every idle OBS "unknown" so it was never closed. isObsService answers for an address;
        // if it throws, the connection counts (unknown is treated as live).
        public static bool CountsAsOutput(byte[] addr, Func<System.Net.IPAddress, bool> isObsService)
        {
            if (IsLoopback(addr)) return false;
            try { return !(isObsService != null && isObsService(new System.Net.IPAddress(addr))); }
            catch { return true; }
        }

        // Whether an address belongs to OBS's own service: one that obsproject.com's names resolve to, or whose
        // reverse name is obsproject.com or ends in .obsproject.com. Each look-up waits at most 800 ms; a failed or
        // slow one says "no", so the connection still counts. Answers are kept for the session (a "no" isn't, so a
        // look-up that failed is tried again next time).
        public static Func<System.Net.IPAddress, bool> IsObsService = LookUpObsService;
        static readonly string[] ObsServiceNames = { "obsproject.com", "cdn-fastly.obsproject.com" };
        static readonly object _svcLock = new object();
        static HashSet<string> _svcAddrs;                      // what the names resolve to, once per session
        static readonly HashSet<string> _svcYes = new HashSet<string>();

        static bool LookUpObsService(System.Net.IPAddress ip)
        {
            string key = ip.ToString();
            lock (_svcLock) { if (_svcYes.Contains(key)) return true; }
            if (_svcAddrs == null)
            {
                var found = new HashSet<string>();
                foreach (var n in ObsServiceNames)
                {
                    try
                    {
                        var ar = System.Net.Dns.BeginGetHostAddresses(n, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(800)) foreach (var a in System.Net.Dns.EndGetHostAddresses(ar)) found.Add(a.ToString());
                    }
                    catch { }
                }
                if (found.Count > 0) lock (_svcLock) _svcAddrs = found;
            }
            bool yes = false;
            lock (_svcLock) yes = _svcAddrs != null && _svcAddrs.Contains(key);
            if (!yes)
            {
                try
                {
                    var ar = System.Net.Dns.BeginGetHostEntry(ip, null, null);
                    if (ar.AsyncWaitHandle.WaitOne(800))
                    {
                        string host = (System.Net.Dns.EndGetHostEntry(ar).HostName ?? "").TrimEnd('.').ToLowerInvariant();
                        yes = host == "obsproject.com" || host.EndsWith(".obsproject.com", StringComparison.Ordinal);
                    }
                }
                catch { }
            }
            if (yes) lock (_svcLock) _svcYes.Add(key);
            return yes;
        }

        // 127.x.x.x, 0.0.0.0, ::1, :: and IPv4-mapped 127.x (::ffff:127.x.x.x), as the bytes appear on the wire.
        public static bool IsLoopback(byte[] a)
        {
            if (a == null) return true;
            if (a.Length == 4) return a[0] == 127 || (a[0] == 0 && a[1] == 0 && a[2] == 0 && a[3] == 0);
            if (a.Length != 16) return true;
            bool zero10 = true; for (int i = 0; i < 10; i++) if (a[i] != 0) zero10 = false;
            if (zero10 && a[10] == 0xff && a[11] == 0xff) return a[12] == 127 || (a[12] == 0 && a[13] == 0 && a[14] == 0 && a[15] == 0);
            bool allZero = zero10 && a[10] == 0 && a[11] == 0 && a[12] == 0 && a[13] == 0 && a[14] == 0;
            return allZero && (a[15] == 1 || a[15] == 0);
        }

        // Adds what Race Mode's own stream stats say: "streaming" or "recording" there makes OBS live whatever its log says.
        // The stats only ever add: they never make a live or unknown OBS idle.
        public static ObsStatus WithStats(ObsStatus s, bool statsStreaming) { return WithStats(s, statsStreaming, false); }

        public static ObsStatus WithStats(ObsStatus s, bool statsStreaming, bool statsRecording)
        {
            if (statsStreaming && !s.Active.Contains("streaming")) s.Active.Insert(0, "streaming");
            if (statsRecording && !s.Active.Contains("recording")) s.Active.Insert(s.Active.Contains("streaming") ? 1 : 0, "recording");
            if (s.Active.Count > 0) s.State = ObsState.Live;
            return s;
        }
    }
}
