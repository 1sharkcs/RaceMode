// Triple-screen tests for RaceMode.DisplayGuard. Every write goes to a temp copy of the public fixture ini;
// the real iRacing file is never opened. Screens are made up (no real monitor ids).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using RaceMode;

class TripleTests
{
    static int pass, fail;
    static string Root, Fixture;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok || detail == null ? "" : "  -> " + detail));
    }

    static MonitorInfo M(int idx, string id, int x, int y, int w, int h, int hz, bool primary)
    {
        return new MonitorInfo { Index = idx, MonitorId = id, DeviceName = "DISPLAY" + (idx + 1), X = x, Y = y, Width = w, Height = h, Hz = hz, Primary = primary };
    }
    // Three identical 1080p screens, the centre one is the main screen (left one at negative X), deviceIdx order shuffled.
    static List<MonitorInfo> Row() { return new List<MonitorInfo> { M(0, "TST0001", 0, 0, 1920, 1080, 144, true), M(1, "TST0001", 1920, 0, 1920, 1080, 144, false), M(2, "TST0001", -1920, 0, 1920, 1080, 144, false) }; }
    // A desk like many people have: three screens of different sizes, not a triple rig.
    static List<MonitorInfo> Mixed() { return new List<MonitorInfo> { M(0, "TSTA", 0, 0, 1920, 1080, 360, true), M(1, "TSTB", -4480, 0, 2560, 1440, 144, false), M(2, "TSTC", -1920, 0, 1920, 1080, 144, false) }; }

    static string Ini(string name, int numMonitors)
    {
        var p = Path.Combine(Root, name + ".ini");
        var src = File.ReadAllText(Fixture, Encoding.Default);
        if (!src.Contains("NumMonitors=1 ")) throw new Exception("fixture no longer has 'NumMonitors=1 ': the iRacing-on-3 tests would silently test 1 screen");
        var text = src.Replace("NumMonitors=1 ", "NumMonitors=" + numMonitors + " ");
        File.WriteAllText(p, text, Encoding.Default);
        return p;
    }
    static DisplayGuardConfig Triple(string ini) { return new DisplayGuardConfig { Enabled = true, MonitorId = "TST0001", Width = 1920, Height = 1080, RefreshRate = 144, KeepFpsCap = false, IniPath = ini, Triple = new List<string> { "TST0001", "TST0001", "TST0001" } }; }

    static string Val(string path, string section, string key)
    {
        string sec = "";
        foreach (var raw in File.ReadAllLines(path, Encoding.Default))
        {
            var t = raw.Trim();
            if (t.StartsWith("[") && t.EndsWith("]")) { sec = t.Substring(1, t.Length - 2); continue; }
            if (!string.Equals(sec, section, StringComparison.OrdinalIgnoreCase)) continue;
            var eq = raw.IndexOf('='); if (eq < 0) continue;
            if (!string.Equals(raw.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
            var v = raw.Substring(eq + 1); var sc = v.IndexOf(';'); if (sc >= 0) v = v.Substring(0, sc);
            return v.Trim();
        }
        return null;
    }
    static string Section(string path, string section)
    {
        var lines = File.ReadAllLines(path, Encoding.Default); var sb = new StringBuilder(); bool inSec = false;
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.StartsWith("[")) inSec = string.Equals(t, "[" + section + "]", StringComparison.OrdinalIgnoreCase);
            else if (inSec) sb.AppendLine(l);
        }
        return sb.ToString();
    }

    static int Main()
    {
        Fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"fixtures\live-before-race-preset.ini");
        Root = Path.Combine(Path.GetTempPath(), "racemode-triple-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Root);
        try
        {
            // ---- finding rows of three
            var rows = DisplayGuard.TripleRows(Row());
            Check("three screens in a row are found", rows.Count == 1, rows.Count + " rows");
            Check("the row runs left to right", rows.Count == 1 && rows[0][0].X == -1920 && rows[0][1].X == 0 && rows[0][2].X == 1920, null);
            // a desk of different screens side by side is a row too; the picker only offers it once three screens is ticked
            var mixed = DisplayGuard.TripleRows(Mixed());
            Check("screens of different sizes side by side still make a row", mixed.Count == 1 && !DisplayGuard.TripleMatches(mixed[0]), null);
            Check("three matching screens match", DisplayGuard.TripleMatches(rows[0]), null);
            int wx, wy, ww, wh;
            DisplayGuard.TripleWindow(mixed[0], out wx, out wy, out ww, out wh);
            Check("mixed row: the window spans all three over the height they share", wx == -4480 && wy == 0 && ww == 6400 && wh == 1080, wx + "," + wy + " " + ww + "x" + wh);
            var four = Row(); four.Add(M(3, "TST0001", 3840, 0, 1920, 1080, 144, false));
            Check("four in a row give two possible rows", DisplayGuard.TripleRows(four).Count == 2, null);
            var gap = Row(); gap[1].X = 1930;
            Check("a gap between screens is not a row", DisplayGuard.TripleRows(gap).Count == 0, null);
            var tall = Row(); tall[1].Y = 50;
            var tallRows = DisplayGuard.TripleRows(tall);
            DisplayGuard.TripleWindow(tallRows.Count == 1 ? tallRows[0] : Row(), out wx, out wy, out ww, out wh);
            Check("a screen set lower is still in the row; the window uses the part all three share", tallRows.Count == 1 && wy == 50 && wh == 1030 && ww == 5760, wx + "," + wy + " " + ww + "x" + wh);
            var apart = Row(); apart[1].Y = 1080;
            Check("a screen below the others, not beside them, is not a row", DisplayGuard.TripleRows(apart).Count == 0, null);
            Check("5760x1080 looks like joined screens", DisplayGuard.LooksJoined(M(0, "X", 0, 0, 5760, 1080, 144, true)), null);
            Check("a 32:9 ultrawide is not joined screens", !DisplayGuard.LooksJoined(M(0, "X", 0, 0, 5120, 1440, 240, true)), null);

            // ---- triple run
            var ini = Ini("triple", 3);
            var setupBefore = Section(ini, "MonitorSetup");
            var devBefore = Val(ini, "Display", "deviceIdx");
            var r = DisplayGuard.Run(Triple(ini), true, Row(), false);
            Check("triple: changed", r.Changed && !r.Skipped, r.Message);
            Check("triple: window starts at the left screen", Val(ini, "Display", "windowedXPos") == "-1920", Val(ini, "Display", "windowedXPos"));
            Check("triple: window spans all three", Val(ini, "Display", "windowedWidth") == "5760" && Val(ini, "Display", "windowedHeight") == "1080", Val(ini, "Display", "windowedWidth"));
            Check("triple: borderless window, not full screen", Val(ini, "Display", "fullScreen") == "0" && Val(ini, "Display", "border") == "0" && Val(ini, "Display", "windowedMaximized") == "0", null);
            Check("triple: deviceIdx left as it was", Val(ini, "Display", "deviceIdx") == devBefore, Val(ini, "Display", "deviceIdx"));
            Check("triple: iRacing's monitor geometry untouched", Section(ini, "MonitorSetup") == setupBefore, null);
            Check("triple: backup made", r.Backup != null && File.Exists(r.Backup), r.Backup);
            Check("triple: message names three screens", r.Message.Contains("three racing screens"), r.Message);
            var again = DisplayGuard.Run(Triple(ini), true, Row(), false);
            Check("triple: second run has nothing to fix", !again.Changed && again.Changes.Count == 0 && again.Message.StartsWith("Screen settings OK"), again.Message);

            // ---- iRacing itself set up for one screen: still guarded, with a pointer to iRacing's settings
            var ini1 = Ini("triple-num1", 1);
            var r1 = DisplayGuard.Run(Triple(ini1), true, Row(), false);
            Check("triple, iRacing on 1 screen: window still fixed", r1.Changed && Val(ini1, "Display", "windowedWidth") == "5760", r1.Message);
            Check("triple, iRacing on 1 screen: says how to fix it in iRacing", r1.Message.Contains("set up for one screen") && Val(ini1, "MonitorSetup", "NumMonitors") == "1", r1.Message);

            // ---- configs saved by 1.1 keep the whole window's size; offset and staggered rows
            var iniNew = Ini("triple-newcfg", 3);
            var cfgNew = Triple(iniNew); cfgNew.Width = 5760; cfgNew.Height = 1080;
            var rNew = DisplayGuard.Run(cfgNew, true, Row(), false);
            Check("1.1 config (whole window size): fixed, no size warning", rNew.Changed && rNew.Warning == null, rNew.Message + " | " + rNew.Warning);
            var bigger = Row(); foreach (var m in bigger) { m.Width = 2560; m.Height = 1440; } bigger[1].X = 2560; bigger[2].X = -2560;
            var rBig = DisplayGuard.Run(cfgNew, false, bigger, false);
            Check("1.1 config, screens changed resolution since: warns", rBig.Warning != null && rBig.Warning.Contains("different size"), rBig.Warning);
            var iniOff = Ini("triple-offset", 3);
            var offset = Row(); offset[1].Y = 50;
            var rOff = DisplayGuard.Run(Triple(iniOff), true, offset, false);
            Check("offset row: window uses the band all three share", Val(iniOff, "Display", "windowedYPos") == "50" && Val(iniOff, "Display", "windowedHeight") == "1030", Val(iniOff, "Display", "windowedYPos") + " / " + Val(iniOff, "Display", "windowedHeight"));
            // a staircase: left/middle and middle/right overlap, but nothing all three share
            var stair = new List<MonitorInfo> { M(0, "TSTA", 0, 0, 1920, 1080, 144, true), M(1, "TSTB", 1920, -400, 1440, 2560, 144, false), M(2, "TSTC", 3360, 1200, 1920, 1080, 144, false) };
            Check("a staircase of screens is not a row (no shared band)", DisplayGuard.TripleRows(stair).Count == 0, null);
            var iniStair = Ini("triple-stair", 3);
            var textStair = File.ReadAllText(iniStair);
            var stairCfg = new DisplayGuardConfig { Enabled = true, MonitorId = "TSTB", Width = 5280, Height = 1080, RefreshRate = 144, KeepFpsCap = false, IniPath = iniStair, Triple = new List<string> { "TSTA", "TSTB", "TSTC" } };
            var rStair = DisplayGuard.Run(stairCfg, true, stair, false);
            Check("staircase: skipped, the file is left alone", rStair.Skipped && File.ReadAllText(iniStair) == textStair, rStair.Message);
            // three identical screens plus a fourth of the same model turned to portrait on the right
            var plus = Row(); plus.Add(M(3, "TST0001", 3840, 0, 1080, 1920, 144, false));
            Check("a portrait fourth screen makes a second possible row", DisplayGuard.TripleRows(plus).Count == 2, DisplayGuard.TripleRows(plus).Count + " rows");
            var iniPlus = Ini("triple-plus", 3);
            var rPlus = DisplayGuard.Run(Triple(iniPlus), true, plus, false);
            Check("fourth screen: keeps the row that is the picked size", rPlus.Changed && Val(iniPlus, "Display", "windowedXPos") == "-1920" && Val(iniPlus, "Display", "windowedWidth") == "5760", rPlus.Message);

            // ---- a screen missing / switched off
            var iniMiss = Ini("triple-missing", 3);
            var textMiss = File.ReadAllText(iniMiss);
            var r2 = DisplayGuard.Run(Triple(iniMiss), true, Row().Take(2).ToList(), false);
            Check("triple, a screen off: skipped", r2.Skipped && !r2.Changed, r2.Message);
            Check("triple, a screen off: file unchanged", File.ReadAllText(iniMiss) == textMiss, null);

            // ---- the order matters: different models must sit left, centre, right as picked
            var mixedRow = new List<MonitorInfo> { M(0, "TSTL", -1920, 0, 1920, 1080, 144, false), M(1, "TSTC", 0, 0, 1920, 1080, 144, true), M(2, "TSTR", 1920, 0, 1920, 1080, 144, false) };
            var cfgLcr = Triple(Ini("triple-order", 3)); cfgLcr.Triple = new List<string> { "TSTL", "TSTC", "TSTR" };
            Check("triple, picked order matches: fixed", DisplayGuard.Run(cfgLcr, true, mixedRow, false).Changed, null);
            var iniSwap = Ini("triple-swapped", 3); var cfgSwap = Triple(iniSwap); cfgSwap.Triple = new List<string> { "TSTR", "TSTC", "TSTL" };
            var r3 = DisplayGuard.Run(cfgSwap, true, mixedRow, false);
            Check("triple, screens moved around: skipped", r3.Skipped && !r3.Changed, r3.Message);

            // ---- slow screen warning
            var slow = Row(); slow[1].Hz = 60;
            var r4 = DisplayGuard.Run(Triple(Ini("triple-slow", 3)), true, slow, false);
            Check("triple, one screen at 60 Hz: warns", r4.Warning != null && r4.Warning.Contains("60 Hz"), r4.Warning);

            // ---- iRacing running: waits
            var iniRun = Ini("triple-running", 3); var textRun = File.ReadAllText(iniRun);
            var r5 = DisplayGuard.Run(Triple(iniRun), true, Row(), true);
            Check("triple, iRacing running: waits", r5.Skipped && File.ReadAllText(iniRun) == textRun, r5.Message);

            // ---- one-screen check on a triple iRacing setup leaves it alone
            var iniOne = Ini("single-on-triple", 3); var textOne = File.ReadAllText(iniOne);
            var single = new DisplayGuardConfig { Enabled = true, MonitorId = "TST0001", Width = 1920, Height = 1080, RefreshRate = 144, KeepFpsCap = false, IniPath = iniOne };
            var oneRow = new List<MonitorInfo> { M(0, "TSTA", -1920, 0, 1920, 1080, 144, false), M(1, "TST0001", 0, 0, 1920, 1080, 144, true), M(2, "TSTB", 1920, 0, 1920, 1080, 144, false) };
            var r6 = DisplayGuard.Run(single, true, oneRow, false);
            Check("one screen picked, iRacing on 3: left alone", r6.Skipped && File.ReadAllText(iniOne) == textOne, r6.Message);
            Check("one screen picked, iRacing on 3: says why", r6.Message.Contains("set up for three screens"), r6.Message);
            var r6b = DisplayGuard.Run(single, true, new List<MonitorInfo> { M(0, "TSTA", 0, 0, 1920, 1080, 144, true) }, false);
            Check("one screen picked but unplugged, iRacing on 3: says it isn't connected", r6b.Skipped && r6b.Message.Contains("isn't connected") && File.ReadAllText(iniOne) == textOne, r6b.Message);
            var preview = DisplayGuard.Run(single.With(Ini("single-preview", 1)), false, oneRow, false);
            Check("preview wording", preview.Message.StartsWith("Would fix (your 1920"), preview.Message);
            // Surround / Eyefinity: one joined display is still guarded as one screen.
            var iniSur = Ini("surround", 3);
            var sur = new DisplayGuardConfig { Enabled = true, MonitorId = "TSTSUR", Width = 5760, Height = 1080, RefreshRate = 144, KeepFpsCap = false, IniPath = iniSur };
            var r7 = DisplayGuard.Run(sur, true, new List<MonitorInfo> { M(0, "TSTSUR", 0, 0, 5760, 1080, 144, true) }, false);
            Check("Surround display, iRacing on 3: guarded", r7.Changed && Val(iniSur, "Display", "windowedWidth") == "5760", r7.Message);
            // One screen on a one-screen iRacing setup behaves as before.
            var iniPlain = Ini("single", 1);
            var r8 = DisplayGuard.Run(single.With(iniPlain), true, oneRow, false);
            Check("one screen, iRacing on 1: guarded as before", !r8.Skipped && Val(iniPlain, "Display", "windowedWidth") == "1920", r8.Message);

            // ---- saved settings
            var js = new JavaScriptSerializer();
            var back = js.Deserialize<DisplayGuardConfig>(js.Serialize(Triple("x")));
            Check("config: three screens survive a save", back.IsTriple && back.Triple[2] == "TST0001", null);
            var old = js.Deserialize<DisplayGuardConfig>("{\"Enabled\":true,\"MonitorId\":\"TSTA\",\"Width\":1920,\"Height\":1080,\"RefreshRate\":144}");
            Check("config: an older config is one screen", !old.IsTriple, null);
            var bad = Triple("x"); bad.Triple = new List<string> { "A", "", "C" };
            Check("config: a blank id is not a triple", !bad.IsTriple, null);
            Check("signature: picking three screens counts as a change", DisplayGuard.Signature(Triple("x")) != DisplayGuard.Signature(single.With("x")), null);

            // ---- trial stand-in screens only under the test root
            var oldRoot = Environment.GetEnvironmentVariable("RACEMODE_TEST_ROOT");
            Environment.SetEnvironmentVariable("RACEMODE_TEST_MONITORS", "TSTL,-1920,0,1920,1080,144;TSTC,0,0,1920,1080,165,primary;TSTR,1920,0,1920,1080,144");
            Environment.SetEnvironmentVariable("RACEMODE_TEST_ROOT", null);
            Check("stand-in screens ignored outside a test root", !DisplayGuard.ListMonitors().Any(m => m.MonitorId == "TSTC"), null);
            Environment.SetEnvironmentVariable("RACEMODE_TEST_ROOT", Root);
            var fake = DisplayGuard.ListMonitors();
            Check("stand-in screens used under a test root", fake.Count == 3 && fake[1].Primary && fake[1].Hz == 165 && DisplayGuard.TripleRows(fake).Count == 1, string.Join(" | ", fake.Select(m => m.ToString()).ToArray()));
            Environment.SetEnvironmentVariable("RACEMODE_TEST_MONITORS", null);
            Environment.SetEnvironmentVariable("RACEMODE_TEST_ROOT", oldRoot);
        }
        catch (Exception ex) { Check("no crash", false, ex.ToString()); }
        finally { try { Directory.Delete(Root, true); } catch { } }
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}

static class CfgExt
{
    public static DisplayGuardConfig With(this DisplayGuardConfig c, string ini)
    {
        return new DisplayGuardConfig { Enabled = c.Enabled, MonitorId = c.MonitorId, Width = c.Width, Height = c.Height, RefreshRate = c.RefreshRate, KeepFpsCap = c.KeepFpsCap, IniPath = ini };
    }
}
