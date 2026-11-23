// Turn-on / close flow and install-version tests: which pids may be force-closed, the setup's "going back" wording,
// and the post-update check on a normal start. Nothing real is closed; files are only made next to the test's own
// RaceMode.exe. For test 09-10, run from a folder laid out as <root>\LocalAppData\RaceMode (RaceMode.exe in it).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RaceMode;

static class CloseFlowTests
{
    static int pass, fail;
    static void Check(string name, bool ok, string detail = null)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail != null && !ok ? "  [" + detail + "]" : ""));
    }

    static string L(List<int> l) { return string.Join(",", l.Select(i => i.ToString()).ToArray()); }

    static int Main()
    {
        var pids = new Dictionary<int, string> { { 1, "Word" }, { 2, "Word" }, { 3, "Discord" }, { 4, "Discord" }, { 5, "Helper" }, { 6, "Spotify" } };
        var st = new Dictionary<int, Shell.PidState> {
            { 1, Shell.PidState.Window }, { 2, Shell.PidState.Close },     // Word: one window still open -> keep both
            { 3, Shell.PidState.Close }, { 4, Shell.PidState.Close },      // Discord: nothing open -> kill both
            { 5, Shell.PidState.Admin },                                   // admin: never
            { 6, Shell.PidState.Gone } };
        var k = Shell.PidsToKill(pids, st);
        Check("01 an app with a pid still showing a window keeps all its pids", !k.Contains(1) && !k.Contains(2), L(k));
        Check("02 an app with nothing left open is force-closed", k.Contains(3) && k.Contains(4), L(k));
        Check("03 admin and gone pids are never killed", !k.Contains(5) && !k.Contains(6), L(k));
        pids[7] = "word"; st[7] = Shell.PidState.Close;
        k = Shell.PidsToKill(pids, st);
        Check("04 the hold ignores name case", !k.Contains(7), L(k));
        st[1] = Shell.PidState.Admin;
        k = Shell.PidsToKill(pids, st);
        Check("05 one admin pid holds the rest of its app", !k.Contains(2) && !k.Contains(7), L(k));

        // Never force-closed (GentleClose.cs): OBS recording from the tray was killed 5 s in (found live 2026-11-23)
        Check("11 OBS, its browser sources, browsers and mail are never force-closed",
            GentleClose.NeverForce("obs64") && GentleClose.NeverForce("OBS64") && GentleClose.NeverForce("obs-browser-page") &&
            GentleClose.NeverForce("chrome") && GentleClose.NeverForce("msedge") && GentleClose.NeverForce("OUTLOOK"));
        Check("12 tray apps that are fine to force-close still are", !GentleClose.NeverForce("Discord") && !GentleClose.NeverForce("steam") &&
            !GentleClose.NeverForce("SimHubWPF") && !GentleClose.NeverForce("") && !GentleClose.NeverForce(null));
        var w = GentleClose.WithoutHelpers(new[] { "obs64", "obs-browser-page", "steam", "steamwebhelper", "Discord" });
        Check("13 helpers are left to their main app when it's on the list too", string.Join(",", w.ToArray()) == "obs64,steam,Discord", string.Join(",", w.ToArray()));
        w = GentleClose.WithoutHelpers(new[] { "obs-browser-page", "steamwebhelper", "Discord" });
        Check("14 a helper alone on the list is still asked", string.Join(",", w.ToArray()) == "obs-browser-page,steamwebhelper,Discord", string.Join(",", w.ToArray()));
        w = GentleClose.WithoutHelpers(new[] { "OBS64", "OBS-Browser-Page" });
        Check("15 helper matching ignores case", string.Join(",", w.ToArray()) == "OBS64", string.Join(",", w.ToArray()));
        Check("16 asking a process with no windows asks nothing and doesn't throw", GentleClose.AskHidden(-1, "OBS ") == 0 && GentleClose.AskHidden(0, "OBS ") == 0 && GentleClose.AskHidden(-1, "") == 0);
        Check("17 only OBS's real main window title counts", GentleClose.MainTitle("OBS 33.0.1 - Profile: Untitled - Scenes: Untitled", "OBS ")
            && GentleClose.MainTitle("OBS 27.2.4 (64-bit, windows) - Profile: A", "OBS ") && !GentleClose.MainTitle("Settings", "OBS ")
            && !GentleClose.MainTitle("OBS Studio Docks", "OBS ") && !GentleClose.MainTitle("", "OBS ") && !GentleClose.MainTitle(null, "OBS ") && !GentleClose.MainTitle("OBS ", "OBS "));
        Check("18 every part of OBS is never force-closed", GentleClose.NeverForce("obs-ffmpeg-mux") && GentleClose.NeverForce("obs-nvenc-test") && GentleClose.NeverForce("obs32"));
        w = GentleClose.WithoutHelpers(new[] { "obs64", "obs-ffmpeg-mux", "obs-browser-page" });
        Check("19 OBS's muxer and browser sources end with OBS, never on their own", string.Join(",", w.ToArray()) == "obs64", string.Join(",", w.ToArray()));

        Check("06 test version -> normal release is announced",
            Installer.GoingBackNote("1.0.2-rc.1", "1.0.1") == "Race Mode 1.0.2-rc.1 (a test version) is installed. Installing goes back to 1.0.1, the latest normal release; your settings are kept.",
            Installer.GoingBackNote("1.0.2-rc.1", "1.0.1"));
        Check("07 no note for an upgrade, the same version, or an unknown/local version",
            Installer.GoingBackNote("1.0.1", "1.0.2") == null && Installer.GoingBackNote("1.0.1", "1.0.1") == null &&
            Installer.GoingBackNote("local", "1.0.1") == null && Installer.GoingBackNote("1.0.2", "local") == null && Installer.GoingBackNote(null, "1.0.1") == null);
        Check("08 finished-page wording",
            Installer.DoneLine("1.0.2-rc.1", "1.0.1") == "Went back to Race Mode 1.0.1 (from 1.0.2-rc.1)" &&
            Installer.DoneLine("1.0.1", "1.1.0-rc.1") == "Updated Race Mode from 1.0.1 to 1.1.0-rc.1" &&
            Installer.DoneLine("1.0.1", "1.0.1") == "Reinstalled Race Mode 1.0.1" &&
            Installer.DoneLine(null, "local").StartsWith("Updated Race Mode local in ", StringComparison.Ordinal),
            Installer.DoneLine("1.0.2-rc.1", "1.0.1") + " | " + Installer.DoneLine("1.0.1", "1.1.0-rc.1"));

        // Post-update check on a normal start: RaceMode.exe.old next to the installed exe.
        var exe = typeof(Updater).Assembly.Location;
        var dir = Path.GetDirectoryName(exe);
        var parent = Path.GetDirectoryName(dir);
        if (string.Equals(Path.GetFileName(dir), "RaceMode", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileName(parent), "LocalAppData", StringComparison.OrdinalIgnoreCase))
        {
            var old = exe + ".old";
            File.WriteAllText(old, "previous version");
            Environment.SetEnvironmentVariable("RACEMODE_TEST_ROOT", Path.Combine(Path.GetTempPath(), "racemode-not-installed-" + Guid.NewGuid().ToString("N")));
            Updater.NoticeEarlierUpdate();
            Check("09 a copy that isn't the installed one ignores a .old", !Updater.ConfirmSwap() && File.Exists(old));
            Environment.SetEnvironmentVariable("RACEMODE_TEST_ROOT", Path.GetDirectoryName(parent));
            Updater.NoticeEarlierUpdate();
            Check("10 the installed copy treats a .old as a pending update and removes it once up", Updater.ConfirmSwap() && !File.Exists(old));
            try { File.Delete(old); } catch { }
        }
        else Console.WriteLine("SKIP 09-10 (run from <root>\\LocalAppData\\RaceMode)");

        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
