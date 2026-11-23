// ObsGuard: is OBS live? Built from real OBS 33.0.1 log lines (2026-11-23 on the user's PC). Unknown counts as live:
// Race Mode never closes OBS when it can't tell. Nothing real is touched; the log tests use temp files only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RaceMode;

static class ObsGuardTests
{
    static int pass, fail;
    static void Check(string name, bool ok, string detail = null)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail != null && !ok ? "  [" + detail + "]" : ""));
    }

    static readonly string[] Boot = {
        "11:07:25.526: Command Line Arguments: --startrecording",
        "11:07:27.000: [obs-websocket] [WebSocketServer::ServerRunner] IO thread started.",
        "11:07:27.008: ==== Startup complete ===============================================" };

    static List<string> Log(params string[] more) { var l = Boot.ToList(); l.AddRange(more); return l; }

    static int Main()
    {
        var s = ObsGuard.FromLog(Log());
        Check("01 started, nothing on: idle", s.State == ObsState.Idle && s.Active.Count == 0, s.State.ToString());
        s = ObsGuard.FromLog(Log("11:07:27.307: ==== Recording Start ===============================================",
            "11:07:27.307: [mp4 output: 'simple_file_output'] Writing Hybrid MP4/MOV file 'C:/Users/x/Videos/a.mp4'..."));
        Check("02 recording: live", s.State == ObsState.Live && s.ActiveText == "recording", s.ActiveText);
        s = ObsGuard.FromLog(Log("==== Recording Start ====", "==== Recording Stop ===="));
        Check("03 recording started then stopped: idle", s.State == ObsState.Idle, s.State.ToString());
        s = ObsGuard.FromLog(Log("==== Streaming Start ====", "==== Recording Start ===="));
        Check("04 streaming and recording", s.State == ObsState.Live && s.ActiveText == "streaming and recording", s.ActiveText);
        s = ObsGuard.FromLog(Log("11:21:36.121: ==== Virtual Camera Start ==========================================", "==== Replay Buffer Start ===="));
        Check("05 virtual camera and replay buffer count as live", s.State == ObsState.Live && s.Active.Contains("the virtual camera") && s.Active.Contains("the replay buffer"), s.ActiveText);
        s = ObsGuard.FromLog(Log("==== Streaming Start ====", "==== Streaming Stop ====", "==== Streaming Start ===="));
        Check("06 stream restarted after a stop: live", s.State == ObsState.Live, s.State.ToString());
        s = ObsGuard.FromLog(new[] { "11:05:40.699: Command Line Arguments: --startrecording --minimize-to-tray", "loading modules..." });
        Check("07 still starting (may be about to record): unknown, never idle", s.State == ObsState.Unknown, s.State.ToString());
        s = ObsGuard.FromLog(null);
        Check("08 no log: unknown", s.State == ObsState.Unknown && s.Why.Length > 0);
        s = ObsGuard.FromLog(new string[0]);
        Check("09 empty log: unknown", s.State == ObsState.Unknown);
        s = ObsGuard.FromLog(Log("==== Streaming Start ====", null, "", "garbage \u0000 line"));
        Check("10 blank and junk lines don't throw", s.State == ObsState.Live);
        s = ObsGuard.FromLog(Log("A source named ==== Recording Start in the middle of a line"));
        Check("11 the marker anywhere in a line still counts (safe side)", s.State == ObsState.Live);
        s = ObsGuard.FromLog(Log("==== Recording Start ====", "==== Streaming Stop ===="));
        Check("12 a stop for another output doesn't end recording", s.State == ObsState.Live && s.ActiveText == "recording", s.ActiveText);

        Check("13 every OBS process is OBS's", ObsGuard.IsObs("obs64") && ObsGuard.IsObs("OBS64") && ObsGuard.IsObs("obs-ffmpeg-mux") && ObsGuard.IsObs("obs-browser-page")
            && !ObsGuard.IsObs("obsidian") && !ObsGuard.IsObs("Discord") && !ObsGuard.IsObs("") && !ObsGuard.IsObs(null));

        // which log belongs to the running OBS
        var dir = Path.Combine(Path.GetTempPath(), "obsguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var started = DateTime.Now.AddMinutes(-10);
            var old = Path.Combine(dir, "old.txt"); File.WriteAllText(old, "==== Recording Start ===="); File.SetCreationTime(old, started.AddHours(-2)); File.SetLastWriteTime(old, started.AddHours(-1));
            var cur = Path.Combine(dir, "cur.txt"); File.WriteAllLines(cur, Log()); File.SetCreationTime(cur, started.AddSeconds(1)); File.SetLastWriteTime(cur, started.AddSeconds(3));
            var picked = ObsGuard.LogFor(started, new[] { dir, null, Path.Combine(dir, "missing") });
            Check("14 the running OBS's log is picked, not an older run's", picked == cur, picked);
            Check("15 no log written since it started: none", ObsGuard.LogFor(DateTime.Now.AddMinutes(5), new[] { dir }) == null);
            using (var w = new FileStream(cur, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var b = System.Text.Encoding.UTF8.GetBytes("==== Streaming Start ====\r\n"); w.Write(b, 0, b.Length); w.Flush();
                var read = ObsGuard.ReadShared(cur);
                Check("16 a log OBS still has open is read", ObsGuard.FromLog(read).State == ObsState.Live, read.Count.ToString());
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Check("19 other streaming apps are recognised (never closed: live state unknown)", ObsGuard.IsOtherStreamApp("Streamlabs Desktop") && ObsGuard.IsOtherStreamApp("xsplit.core")
            && ObsGuard.IsOtherStreamApp("PRISMLiveStudio") && !ObsGuard.IsOtherStreamApp("Discord") && !ObsGuard.IsOtherStreamApp(null));
        var none = ObsGuard.Now(false);
        Check("17 Now() never throws (whatever is running here)", none != null);
        var st = ObsGuard.Now(true);
        Check("18 Race Mode's stream stats saying 'streaming' make it live", st.State == ObsState.Live && st.Active.Contains("streaming"), st.State + " " + st.ActiveText);

        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
