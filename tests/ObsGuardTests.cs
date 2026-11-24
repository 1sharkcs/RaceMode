// ObsGuard: is OBS live? Built from lines in OBS 33's log format (times and names made up). Unknown counts as live:
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
        "08:40:12.104: Command Line Arguments: --startrecording",
        "08:40:13.950: [obs-websocket] [WebSocketServer::ServerRunner] IO thread started.",
        "08:40:13.962: ==== Startup complete ===============================================" };

    static List<string> Log(params string[] more) { var l = Boot.ToList(); l.AddRange(more); return l; }

    static int Main()
    {
        var s = ObsGuard.FromLog(Log());
        Check("01 started, nothing on: idle", s.State == ObsState.Idle && s.Active.Count == 0, s.State.ToString());
        s = ObsGuard.FromLog(Log("08:40:14.215: ==== Recording Start ===============================================",
            "08:40:14.215: [mp4 output: 'simple_file_output'] Writing Hybrid MP4/MOV file 'C:/Users/x/Videos/a.mp4'..."));
        Check("02 recording: live", s.State == ObsState.Live && s.ActiveText == "recording", s.ActiveText);
        s = ObsGuard.FromLog(Log("==== Recording Start ====", "==== Recording Stop ===="));
        Check("03 recording started then stopped: idle", s.State == ObsState.Idle, s.State.ToString());
        s = ObsGuard.FromLog(Log("==== Streaming Start ====", "==== Recording Start ===="));
        Check("04 streaming and recording", s.State == ObsState.Live && s.ActiveText == "streaming and recording", s.ActiveText);
        s = ObsGuard.FromLog(Log("08:52:03.477: ==== Virtual Camera Start ==========================================", "==== Replay Buffer Start ===="));
        Check("05 virtual camera and replay buffer count as live", s.State == ObsState.Live && s.Active.Contains("the virtual camera") && s.Active.Contains("the replay buffer"), s.ActiveText);
        s = ObsGuard.FromLog(Log("==== Streaming Start ====", "==== Streaming Stop ====", "==== Streaming Start ===="));
        Check("06 stream restarted after a stop: live", s.State == ObsState.Live, s.State.ToString());
        s = ObsGuard.FromLog(new[] { "08:38:51.330: Command Line Arguments: --startrecording --minimize-to-tray", "loading modules..." });
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
            var a = new DateTime(2030, 1, 15, 14, 2, 10); var b2 = a.AddSeconds(8);
            File.WriteAllLines(Path.Combine(dir, "2030-01-15 12-00-00.txt"), new[] { "==== Recording Start ====" });   // an old run
            var la = Path.Combine(dir, "2030-01-15 14-02-10.txt"); File.WriteAllLines(la, Log("==== Recording Start ===="));
            var lb = Path.Combine(dir, "2030-01-15 14-02-18.txt"); File.WriteAllLines(lb, Log());
            File.WriteAllText(Path.Combine(dir, "not a log name.txt"), "x");
            Check("14 two OBS at once: the recording one gets its own log, not the newer idle one", ObsGuard.LogFor(a.AddMilliseconds(-300), new[] { dir }) == la, ObsGuard.LogFor(a, new[] { dir }));
            Check("15 ...and the second gets its own", ObsGuard.LogFor(b2.AddMilliseconds(-500), new[] { dir }) == lb);
            Check("16 an OBS with no log starting near it: none (unknown, never idle)", ObsGuard.LogFor(a.AddHours(3), new[] { dir, null, Path.Combine(dir, "missing") }) == null);
            DateTime t;
            Check("17 log names read as local start times", ObsGuard.LogNameTime("2030-01-15 08-40-12", out t) && t == new DateTime(2030, 1, 15, 8, 40, 12) && !ObsGuard.LogNameTime("crash 1", out t) && !ObsGuard.LogNameTime(null, out t));
            using (var w = new FileStream(lb, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes("==== Streaming Start ====\r\n"); w.Write(bytes, 0, bytes.Length); w.Flush();
                var read = ObsGuard.ReadShared(lb).ToList();
                Check("18 a log OBS still has open is read", ObsGuard.FromLog(read).State == ObsState.Live, read.Count.ToString());
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        Check("19 only OBS itself counts as the main app; its parts don't", ObsGuard.IsObsMain("obs64") && ObsGuard.IsObsMain("OBS64") && !ObsGuard.IsObsMain("obs-browser-page") && !ObsGuard.IsObsMain("obs-ffmpeg-mux") && !ObsGuard.IsObsMain(null));

        var none = ObsGuard.Now(false);
        Check("20 Now() never throws (whatever is running here)", none != null);
        // 21: from an OBS whose log says idle, so only the stats can make it live (Now(true) was live with no OBS at all)
        var st = ObsGuard.WithStats(ObsGuard.FromLog(Log()), true);
        var idle = ObsGuard.WithStats(ObsGuard.FromLog(Log()), false);
        var unk = ObsGuard.WithStats(ObsGuard.FromLog(null), false);
        Check("21 Race Mode's stream stats saying 'streaming' make an idle OBS live; without them it stays idle (or unknown)",
            st.State == ObsState.Live && st.ActiveText == "streaming" && idle.State == ObsState.Idle && unk.State == ObsState.Unknown, st.State + " " + st.ActiveText + " / " + idle.State + " / " + unk.State);
        var both = ObsGuard.WithStats(ObsGuard.FromLog(Log("==== Streaming Start ====", "==== Recording Start ====")), true);
        Check("22 stats and log both saying streaming list it once", both.ActiveText == "streaming and recording", both.ActiveText);

        // An OBS that is still getting going reads idle in its log, but may be about to go live: unknown (never closed)
        s = ObsGuard.FromLog(Log("08:40:20.511: [rtmp stream: 'simple_stream'] Connecting to RTMP URL rtmp://live.example.invalid/app..."));
        Check("23 a stream still connecting (no Streaming Start yet): unknown", s.State == ObsState.Unknown, s.State.ToString());
        s = ObsGuard.FromLog(Log("[rtmp stream: 'simple_stream'] Connecting to RTMP URL rtmp://live.example.invalid/app...",
            "[rtmp stream: 'simple_stream'] Connection to rtmp://live.example.invalid/app successful", "==== Streaming Start ====", "==== Streaming Stop ===="));
        Check("24 a stream that connected, started and stopped: idle", s.State == ObsState.Idle, s.State.ToString());
        s = ObsGuard.FromLog(Log("[rtmp stream: 'simple_stream'] Connecting to RTMP URL rtmp://live.example.invalid/app...",
            "[rtmp stream: 'simple_stream'] Connection to rtmp://live.example.invalid/app failed: -2"));
        Check("25 a first connection that failed doesn't keep it unknown", s.State == ObsState.Idle, s.State.ToString());
        s = ObsGuard.FromLog(Log(), 20);
        Check("26 started under a minute ago and idle so far: unknown", s.State == ObsState.Unknown && s.Why.Length > 0, s.State.ToString());
        s = ObsGuard.FromLog(Log("==== Recording Start ===="), 20);
        Check("27 ...but already recording is live, and a minute in it's idle", s.State == ObsState.Live && ObsGuard.FromLog(Log(), 90).State == ObsState.Idle, s.State.ToString());

        Check("28 Now() looks for every name OBS runs as (obs64, obs32, obs)", new[] { "obs64", "obs32", "obs" }.All(n => ObsGuard.MainNames.Contains(n)));
        Check("29 other streaming apps are known; OBS and other apps aren't one of them", ObsGuard.IsOtherStreamApp("Streamlabs Desktop") && ObsGuard.IsOtherStreamApp("xsplit.core")
            && ObsGuard.IsOtherStreamApp("PRISMLiveStudio") && ObsGuard.IsOtherStreamApp("vMix64") && !ObsGuard.IsOtherStreamApp("obs64") && !ObsGuard.IsOtherStreamApp("Discord")
            && !ObsGuard.IsOtherStreamApp("") && !ObsGuard.IsOtherStreamApp(null));

        // A plugin output (Aitum Vertical, Multiple RTMP Outputs, NDI) doesn't write "==== Start": a connection out of
        // OBS to another machine is what shows it, so loopback must not count and everything else must
        Check("30 loopback doesn't count as a stream: 127.x, 0.0.0.0, ::1, ::, ::ffff:127.0.0.1",
            ObsGuard.IsLoopback(new byte[] { 127, 0, 0, 1 }) && ObsGuard.IsLoopback(new byte[] { 0, 0, 0, 0 })
            && ObsGuard.IsLoopback(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }) && ObsGuard.IsLoopback(new byte[16])
            && ObsGuard.IsLoopback(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 127, 0, 0, 1 }));
        Check("31 a connection to another machine counts: a server, the LAN (NDI), IPv6, IPv4-mapped",
            !ObsGuard.IsLoopback(new byte[] { 52, 223, 0, 10 }) && !ObsGuard.IsLoopback(new byte[] { 192, 168, 1, 20 })
            && !ObsGuard.IsLoopback(new byte[] { 0x26, 0x06, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 })
            && !ObsGuard.IsLoopback(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 10, 0, 0, 5 }));
        bool threw = false, noConn = true;
        try { noConn = !ObsGuard.OwnsRemoteTcp(-1) && !ObsGuard.OwnsRemoteTcp(0); ObsGuard.OwnsRemoteTcp(System.Diagnostics.Process.GetCurrentProcess().Id); } catch { threw = true; }
        Check("32 reading Windows' connection table works without admin and never throws; no process id, no connection", !threw && noConn);

        // An idle OBS keeps connections to obsproject.com open (update and what's-new checks): those aren't outputs,
        // or an idle OBS on the Close list would never be closed (found live 2026-11-23). Anything else still counts.
        var server = new byte[] { 37, 59, 185, 111 };
        Check("33 a connection to OBS's own service isn't an output", !ObsGuard.CountsAsOutput(server, ip => true));
        Check("34 a connection anywhere else is (a stream ingest, the LAN)", ObsGuard.CountsAsOutput(server, ip => false)
            && ObsGuard.CountsAsOutput(new byte[] { 192, 168, 1, 20 }, ip => false));
        Check("35 a look-up that fails counts as an output (unknown is live)", ObsGuard.CountsAsOutput(server, ip => { throw new System.Net.Sockets.SocketException(); })
            && ObsGuard.CountsAsOutput(server, null));
        Check("36 loopback is never an output, whatever the look-up says", !ObsGuard.CountsAsOutput(new byte[] { 127, 0, 0, 1 }, ip => false)
            && !ObsGuard.CountsAsOutput(new byte[] { 127, 0, 0, 1 }, ip => { throw new Exception(); }));

        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
