// Tests for Irsdk.cs against a FAKE iRacing telemetry map (never the real one).
// Build next to RaceMode.exe: csc -r:RaceMode.exe -r:System.Core.dll IrsdkTests.cs
using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using RaceMode;

class IrsdkTests
{
    static int pass, fail;
    static void Check(string n, bool ok, string d) { if (ok) pass++; else fail++; Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (ok || d == null ? "" : "  -> " + d)); }

    const string Name = @"Local\RaceModeTestIrsdk";
    const int VarHdr = 1024, BufLen = 64, Buf0 = 4096;
    // var layout in each buffer: FrameRate float@0, CpuUsageFG float@4, CpuUsageBG float@8, GpuUsage float@12, IsOnTrack bool@16, IsReplayPlaying bool@17
    static readonly string[] Names = { "FrameRate", "CpuUsageFG", "CpuUsageBG", "GpuUsage", "IsOnTrack", "IsReplayPlaying", "Speed" };
    static readonly int[] Types = { 4, 4, 4, 4, 1, 1, 4 };
    static readonly int[] Offs = { 0, 4, 8, 12, 16, 17, 20 };

    static MemoryMappedFile Make(int status, int numBuf, string pctUnit)
    {
        var m = MemoryMappedFile.CreateNew(Name, 16384);
        using (var v = m.CreateViewAccessor())
        {
            v.Write(0, 2); v.Write(4, status); v.Write(8, 60);
            v.Write(24, Names.Length); v.Write(28, VarHdr); v.Write(32, numBuf); v.Write(36, BufLen);
            for (int i = 0; i < Names.Length; i++)
            {
                long h = VarHdr + i * 144;
                v.Write(h, Types[i]); v.Write(h + 4, Offs[i]); v.Write(h + 8, 1);
                WStr(v, h + 16, Names[i]);
                WStr(v, h + 112, i == 0 ? "fps" : (i >= 1 && i <= 3) ? pctUnit : i == 6 ? "m/s" : "");
            }
            for (int b = 0; b < 4; b++) { v.Write(48 + b * 16, 0); v.Write(48 + b * 16 + 4, Buf0 + b * BufLen); }
        }
        return m;
    }
    static void WStr(MemoryMappedViewAccessor v, long at, string s) { var b = Encoding.ASCII.GetBytes(s); v.WriteArray(at, b, 0, b.Length); v.Write(at + b.Length, (byte)0); }
    static void SetBuf(MemoryMappedViewAccessor v, int b, int tick, float fps, float fg, float bg, float gpu, bool onTrack, bool replay)
    {
        long o = Buf0 + b * BufLen;
        v.Write(o, fps); v.Write(o + 4, fg); v.Write(o + 8, bg); v.Write(o + 12, gpu); v.Write(o + 16, (byte)(onTrack ? 1 : 0)); v.Write(o + 17, (byte)(replay ? 1 : 0));
        v.Write(48 + b * 16, tick);
    }

    static void Main()
    {
        Irsdk.MapName = Name; Irsdk.ForceOpen = true;
        string log = "";
        Action<string> ev = m => log += m + "\n";

        // 01 no map yet (sim still starting) -> null, no throw
        using (var r = new Irsdk()) Check("01 no map -> nothing, no crash", r.Read(true, 1, ev) == null, null);

        // 02 map exists but not connected (loading) -> sample with no values
        using (var m = Make(0, 3, "%"))
        using (var r = new Irsdk())
        {
            var s = r.Read(true, 1, ev);
            Check("02 status 0 -> not connected, values blank", s != null && !s.Connected && double.IsNaN(s.Fps), null);
        }

        // 03-06 connected, driving: newest buffer wins, fractions become %
        using (var m = Make(1, 3, "%"))
        using (var v = m.CreateViewAccessor())
        using (var r = new Irsdk())
        {
            SetBuf(v, 0, 10, 100f, 0.5f, 0.2f, 0.3f, true, false);
            SetBuf(v, 1, 12, 143.5f, 0.95f, 0.4f, 0.6f, true, false);
            SetBuf(v, 2, 11, 90f, 0.1f, 0.1f, 0.1f, true, false);
            log = "";
            var s = r.Read(true, 1, ev);
            Check("03 connected, newest buffer (tick 12) read", s != null && s.Connected && Math.Abs(s.Fps - 143.5) < 0.01, s == null ? "null" : s.Fps.ToString());
            Check("04 CpuUsageFG fraction 0.95 -> 95%", s != null && Math.Abs(s.CpuFg - 95) < 0.01 && Math.Abs(s.Gpu - 60) < 0.01 && Math.Abs(s.CpuBg - 40) < 0.01, s == null ? null : s.CpuFg + "/" + s.Gpu);
            Check("05 context = drive", s != null && s.Context == "drive", s == null ? null : s.Context);
            Check("06 units logged once on connect", log.Contains("iRacing telemetry connected") && log.Contains("FrameRate (fps)") && log.Contains("CpuUsageFG (%)"), log);
            SetBuf(v, 0, 13, 60f, 0.2f, 0.1f, 0.9f, false, true);
            log = "";
            s = r.Read(true, 1, ev);
            Check("07 replay context, values follow the newer buffer", s.Context == "replay" && Math.Abs(s.Fps - 60) < 0.01 && log == "", s.Context + " " + s.Fps);
            SetBuf(v, 0, 14, 60f, 0.2f, 0.1f, 0.9f, false, false);
            s = r.Read(true, 1, ev);
            Check("08 neither on track nor replay -> other", s.Context == "other", s.Context);
        }

        // 09 a "%" variable that really is 0..100 is passed through once a value > 1.5 is seen
        using (var m = Make(1, 1, "%"))
        using (var v = m.CreateViewAccessor())
        using (var r = new Irsdk())
        {
            // Seen in a real session: CpuUsageFG read 9.1 on a loading screen, then 0.3..0.6 while driving.
            // The old "above 1.5 means already percent" guess then showed 0.5% instead of 50%.
            SetBuf(v, 0, 1, 15f, 9.1f, 0.3f, 0.02f, false, false);
            var load = r.Read(true, 1, ev);
            SetBuf(v, 0, 2, 144f, 0.5f, 0.3f, 0.2f, true, false);
            var s = r.Read(true, 1, ev);
            Check("09 fraction above 1 on loading, then driving values still scaled (910%, then 50%)", Math.Abs(load.CpuFg - 910) < 0.1 && Math.Abs(s.CpuFg - 50) < 0.01 && Math.Abs(s.Gpu - 20) < 0.01, load.CpuFg + " then " + s.CpuFg + "/" + s.Gpu);
        }

        // 10 sim crashes (map goes away) -> null, then reopens when a new sim publishes again
        using (var r = new Irsdk())
        {
            var m = Make(1, 1, "%");
            var v = m.CreateViewAccessor(); SetBuf(v, 0, 1, 120f, 0.5f, 0.5f, 0.5f, true, false);
            var a = r.Read(true, 1, ev);
            v.Dispose(); m.Dispose();
            // Our reader still holds its view, so the OS keeps the map alive; a new PID forces a reopen.
            var b = r.Read(true, 2, ev);
            var m2 = Make(1, 1, "%"); var v2 = m2.CreateViewAccessor(); SetBuf(v2, 0, 5, 77f, 0.5f, 0.5f, 0.5f, true, false);
            var c = r.Read(true, 2, ev); if (c == null || !c.Connected) c = r.Read(true, 2, ev);
            Check("10 sim restart -> old map dropped, new one read", a.Connected && Math.Abs(a.Fps - 120) < 0.01 && b == null && c != null && Math.Abs(c.Fps - 77) < 0.01, (b == null ? "b null" : "b " + b.Fps) + " / " + (c == null ? "c null" : c.Fps.ToString()));
            v2.Dispose(); m2.Dispose();
        }

        // 11 sim not running and not forced -> map closed, nothing read
        Irsdk.ForceOpen = false;
        using (var m = Make(1, 1, "%"))
        using (var r = new Irsdk())
            Check("11 sim not running -> not opened", r.Read(false, -1, ev) == null, null);
        Irsdk.ForceOpen = true;

        // 12 garbage header (huge numVars / offset past the end) -> blank values, no crash
        using (var m = Make(1, 1, "%"))
        using (var v = m.CreateViewAccessor())
        using (var r = new Irsdk())
        {
            v.Write(24, 999999); v.Write(28, 16000);
            var s = r.Read(true, 1, ev);
            Check("12 corrupt header -> no crash, no values", s != null && double.IsNaN(s.Fps), s == null ? "null" : s.Fps.ToString());
        }

        // 13 torn reads: a writer thread rewrites buffers as fast as it can; every sample must be self-consistent
        using (var m = Make(1, 2, "%"))
        using (var v = m.CreateViewAccessor())
        using (var r = new Irsdk())
        {
            SetBuf(v, 0, 1, 100f, 0.10f, 0.10f, 0.10f, true, false);
            SetBuf(v, 1, 2, 100f, 0.10f, 0.10f, 0.10f, true, false);
            bool stop = false; int tick = 3;
            var w = new Thread(() =>
            {
                using (var wv = m.CreateViewAccessor())
                    while (!stop)
                    {
                        int b = tick % 2; float f = 100 + (tick % 50);
                        wv.Write(48 + b * 16, -1);                       // iRacing bumps tick after writing; mark mid-write
                        wv.Write(Buf0 + b * BufLen, f); wv.Write(Buf0 + b * BufLen + 4, f / 1000f);
                        wv.Write(48 + b * 16, tick); tick++;
                    }
            });
            w.Start();
            int bad = 0, ok = 0, skipped = 0;
            for (int i = 0; i < 200000; i++)
            {
                var s = r.Read(true, 1, ev);
                if (s == null || !s.Connected) { skipped++; continue; }
                ok++;
                if (Math.Abs(s.Fps / 10.0 - s.CpuFg) > 0.01) bad++;   // FG% must be FPS/10 when both come from one buffer
            }
            stop = true; w.Join();
            Check("13 torn-read stress: 0 mixed samples", bad == 0 && ok > 1000, "bad " + bad + ", ok " + ok + ", skipped " + skipped);
        }

        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
