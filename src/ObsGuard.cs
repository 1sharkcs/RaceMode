// Is OBS live? Race Mode must never close, kill, lower or interrupt OBS (or any part of it) while it is streaming,
// recording, running its replay buffer or its virtual camera: a racer who is live can't have Race Mode end the stream.
//
// OBS writes every output start and stop to its log as it happens ("==== Recording Start ====", "==== Streaming Stop
// ===="), so the running OBS's own log says what is live, with no settings to change and no password. When Race Mode's
// stream stats are connected (obs-websocket), "streaming" from there counts too. If neither can say (no log for this
// OBS yet, OBS still starting up, the log can't be read), the answer is "unknown", and unknown is treated as live:
// OBS is left alone and Race Mode says so.
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
            var s = new ObsStatus();
            if (lines == null) { s.Why = "no log"; return s; }
            bool started = false;
            var on = new bool[Outputs.Length];
            foreach (var raw in lines)
            {
                if (raw == null) continue;
                if (raw.IndexOf("==== Startup complete", StringComparison.Ordinal) >= 0) started = true;
                for (int i = 0; i < Outputs.Length; i++)
                {
                    if (raw.IndexOf(Outputs[i][1], StringComparison.Ordinal) >= 0) on[i] = true;
                    else if (raw.IndexOf(Outputs[i][2], StringComparison.Ordinal) >= 0) on[i] = false;
                }
            }
            for (int i = 0; i < Outputs.Length; i++) if (on[i]) s.Active.Add(Outputs[i][0]);
            if (s.Active.Count > 0) s.State = ObsState.Live;
            else if (!started) { s.State = ObsState.Unknown; s.Why = "OBS is still starting"; }
            else s.State = ObsState.Idle;
            return s;
        }

        // The running OBS's log: the newest log file in OBS's logs folder written since that OBS started.
        // logsDirs: candidate folders (the normal %APPDATA% one, and a portable OBS's own config folder).
        public static string LogFor(DateTime obsStarted, IEnumerable<string> logsDirs)
        {
            FileInfo best = null;
            foreach (var d in logsDirs)
            {
                try
                {
                    if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                    foreach (var f in new DirectoryInfo(d).GetFiles("*.txt"))
                        if (f.LastWriteTime >= obsStarted.AddSeconds(-5) && f.CreationTime >= obsStarted.AddSeconds(-30) && (best == null || f.LastWriteTime > best.LastWriteTime)) best = f;
                }
                catch { }
            }
            return best == null ? null : best.FullName;
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

        // Reads a log OBS is still writing (shared read).
        public static List<string> ReadShared(string path)
        {
            var lines = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs))
            {
                string l; while ((l = r.ReadLine()) != null) lines.Add(l);
            }
            return lines;
        }

        // Every running OBS: live if any is live; unknown if any can't be read; idle only when all are known idle.
        // statsStreaming: Race Mode's own stream stats say OBS is streaming (obs-websocket), if connected.
        public static ObsStatus Now(bool statsStreaming)
        {
            var all = new ObsStatus { State = ObsState.Idle };
            Process[] ps;
            try { ps = Process.GetProcessesByName("obs64").Concat(Process.GetProcessesByName("obs32")).ToArray(); } catch { return new ObsStatus { Why = "couldn't list processes" }; }
            try
            {
                foreach (var p in ps)
                {
                    ObsStatus one;
                    try
                    {
                        string exe = null; try { exe = p.MainModule.FileName; } catch { }
                        var log = LogFor(p.StartTime, LogDirs(exe));
                        one = log == null ? new ObsStatus { Why = "no log for this OBS" } : FromLog(ReadShared(log));
                    }
                    catch (Exception ex) { one = new ObsStatus { Why = "couldn't read OBS's log (" + ex.Message + ")" }; }
                    foreach (var a in one.Active) if (!all.Active.Contains(a)) all.Active.Add(a);
                    if (one.State == ObsState.Unknown && all.State == ObsState.Idle) { all.State = ObsState.Unknown; all.Why = one.Why; }
                }
            }
            finally { foreach (var p in ps) p.Dispose(); }
            if (statsStreaming && !all.Active.Contains("streaming")) all.Active.Insert(0, "streaming");
            if (all.Active.Count > 0) all.State = ObsState.Live;
            return all;
        }
    }
}
