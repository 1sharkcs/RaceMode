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

        // which log belongs to which running OBS: by the start time in the log's name
        var dir = Path.Combine(Path.GetTempPath(), "obsguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = new DateTime(2026, 11, 23, 11, 46, 23); var b2 = a.AddSeconds(8);
            File.WriteAllLines(Path.Combine(dir, "2026-11-23 10-00-00.txt"), new[] { "==== Recording Start ====" });   // an old run
            var la = Path.Combine(dir, "2026-11-23 11-46-23.txt"); File.WriteAllLines(la, Log("==== Recording Start ===="));
            var lb = Path.Combine(dir, "2026-11-23 11-46-31.txt"); File.WriteAllLines(lb, Log());
            File.WriteAllText(Path.Combine(dir, "not a log name.txt"), "x");
            Check("14 two OBS at once: the recording one gets its own log, not the newer idle one", ObsGuard.LogFor(a.AddMilliseconds(-300), new[] { dir }) == la, ObsGuard.LogFor(a, new[] { dir }));
            Check("15 ...and the second gets its own", ObsGuard.LogFor(b2.AddMilliseconds(-500), new[] { dir }) == lb);
            Check("16 an OBS with no log starting near it: none (unknown, never idle)", ObsGuard.LogFor(a.AddHours(3), new[] { dir, null, Path.Combine(dir, "missing") }) == null);
            DateTime t;
            Check("17 log names read as local start times", ObsGuard.LogNameTime("2026-11-23 11-07-25", out t) && t == new DateTime(2026, 11, 23, 11, 7, 25) && !ObsGuard.LogNameTime("crash 1", out t) && !ObsGuard.LogNameTime(null, out t));
            using (var w = new FileStream(lb, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes("==== Streaming Start ====\r\n"); w.Write(bytes, 0, bytes.Length); w.Flush();
                var read = ObsGuard.ReadShared(lb);
                Check("18 a log OBS still has open is read", ObsGuard.FromLog(read).State == ObsState.Live, read.Count.ToString());
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        Check("19 only OBS itself counts as the main app; its parts don't", ObsGuard.IsObsMain("obs64") && ObsGuard.IsObsMain("OBS64") && !ObsGuard.IsObsMain("obs-browser-page") && !ObsGuard.IsObsMain("obs-ffmpeg-mux") && !ObsGuard.IsObsMain(null));

        var none = ObsGuard.Now(false);
        Check("20 Now() never throws (whatever is running here)", none != null);
        var st = ObsGuard.Now(true);
        Check("21 Race Mode's stream stats saying 'streaming' make it live", st.State == ObsState.Live && st.Active.Contains("streaming"), st.State + " " + st.ActiveText);

        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
