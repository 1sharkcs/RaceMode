// Preset tests on COPIES only: the fixture presets and a fixture ini are copied into a temp folder; nothing real is written.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RaceMode;

class PresetTests
{
    static int pass, fail;
    static void Check(string n, bool ok, string d) { if (ok) pass++; else fail++; Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (ok || d == null ? "" : "  -> " + d)); }
    static string V(string ini, string sec, string key) { var m = IniFile.Read(ini); Dictionary<string, string> k; string v; return m.TryGetValue(sec, out k) && k.TryGetValue(key, out v) ? v : null; }

    static void Main()
    {
        string realIni = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"iRacing\rendererDX11Monitor.ini");
        string realPresets = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");   // copies of graphics\race.ini and photo.ini
        var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "presetcases");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var gfx = Path.Combine(root, "graphics"); Directory.CreateDirectory(gfx);
        File.Copy(Path.Combine(realPresets, "race.ini"), Path.Combine(gfx, "race.ini"));
        File.Copy(Path.Combine(realPresets, "photo.ini"), Path.Combine(gfx, "photo.ini"));
        GraphicsPresets.Folder = gfx;
        var before = File.Exists(realIni) ? File.ReadAllBytes(realIni) : null;   // no iRacing on the build server

        // Fixed input: the user's live file from just before the Race preset was first applied (51 differences).
        var ini = Path.Combine(root, "live.ini"); File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"fixtures\live-before-race-preset.ini"), ini);
        string capBefore = V(ini, "Graphics Options", "DesiredFPSLimit"), limBefore = V(ini, "Graphics Options", "LimitFrameRate");

        var diff = GraphicsPresets.Differences("Race", ini);
        Console.WriteLine("     differences Race vs live copy: " + diff.Count);
        Check("01 Differences finds the 51 known differences", diff.Count == 51, diff.Count.ToString());

        var pend = GraphicsPresets.Apply("Race", ini, true);
        Check("02 sim running -> pending, file unchanged", pend.Pending && !pend.Ok && File.ReadAllBytes(ini).SequenceEqual(File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"fixtures\live-before-race-preset.ini"))), pend.Message);

        var r = GraphicsPresets.Apply("Race", ini, false);
        Check("03 Race applied and confirmed key for key", r.Ok && r.Confirmed == r.Total && r.Total > 100, r.Message);
        Check("04 7 driving keys now match the preset", V(ini, "Graphics Options", "MSAASamples") == "4" && V(ini, "Graphics Options", "ShaderQuality") == "2" && V(ini, "Graphics Options", "LODMinFPSTarget") == "150", null);
        Check("05 guard keys and the FPS cap untouched", V(ini, "Graphics Options", "DesiredFPSLimit") == capBefore && V(ini, "Graphics Options", "LimitFrameRate") == limBefore, capBefore);
        Check("06 backup written next to the copy", r.Backup != null && File.Exists(r.Backup), r.Backup);
        Check("06b first-ever backup kept separately (never pruned)", File.Exists(ini + ".racemode.first.bak") && File.ReadAllBytes(ini + ".racemode.first.bak").SequenceEqual(File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"fixtures\live-before-race-preset.ini"))), null);
        // Seven older backups are already there (as after seven earlier changes), then one more real change:
        // the newest five stay, the two real ones among them, and the very first backup is never pruned.
        // (Placed by hand instead of rewriting the file seven times: the rotation is what is being checked, and
        // a test that rewrites, renames and deletes the same file over and over looks like ransomware to an antivirus.)
        for (int i = 1; i <= 7; i++) File.WriteAllText(ini + ".racemode-20200101-00000" + i + "-000.bak", "older backup " + i);
        var newest = GraphicsPresets.Apply("Photo", ini, false);
        var kept = Directory.GetFiles(root, "live.ini.racemode-*.bak").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Check("06c 9 backups after one more write: first backup still there, newest 5 kept", File.Exists(ini + ".racemode.first.bak") && kept.Count == 5
              && File.Exists(r.Backup) && newest.Backup != null && File.Exists(newest.Backup)
              && kept[0] == "live.ini.racemode-20200101-000005-000.bak" && !File.Exists(ini + ".racemode-20200101-000004-000.bak"), string.Join(", ", kept.ToArray()));
        GraphicsPresets.Apply("Race", ini, false);
        Check("07 Differences after apply = 0", GraphicsPresets.Differences("Race", ini).Count == 0, null);

        var r2 = GraphicsPresets.Apply("Race", ini, false);
        Check("08 re-apply changes nothing", r2.Ok && r2.Changed == 0, r2.Message);

        var p = GraphicsPresets.Apply("Photo", ini, false);
        Check("09 Photo applied and confirmed", p.Ok && p.Confirmed == p.Total, p.Message);
        Check("10 Photo leaves driving MSAA alone", V(ini, "Graphics Options", "MSAASamples") == "4", null);
        var back = GraphicsPresets.Apply("Race", ini, false);
        Check("11 back to Race confirmed", back.Ok && GraphicsPresets.Differences("Race", ini).Count == 0, back.Message);

        var ro = Path.Combine(root, "ro.ini"); File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"fixtures\live-before-race-preset.ini"), ro); File.SetAttributes(ro, FileAttributes.ReadOnly);
        var roBefore = File.ReadAllBytes(ro);
        var rr = GraphicsPresets.Apply("Race", ro, false);
        Check("12 read-only ini -> clean failure, file intact", rr.Failed && File.ReadAllBytes(ro).SequenceEqual(roBefore), rr.Message);
        File.SetAttributes(ro, FileAttributes.Normal);

        var missing = GraphicsPresets.Apply("Race", Path.Combine(root, "nope.ini"), false);
        Check("13 missing ini -> clean failure", missing.Failed, missing.Message);

        // 15-19: "Save Current as Race" keeps a copy of the preset it replaces
        {
            var gfx2 = Path.Combine(root, "graphics-save"); Directory.CreateDirectory(gfx2);
            GraphicsPresets.Folder = gfx2;
            var race = GraphicsPresets.PresetPath("Race");
            var src = Path.Combine(root, "renderer.ini");
            File.WriteAllText(src, "[Graphics Options]\r\nShadowMaps=1\r\n\r\n[Replay Graphics]\r\nShadowMaps=1\r\n");
            GraphicsPresets.SaveFromIni("Race", src);
            Check("15 first save: the preset is written and there is nothing to back up", File.Exists(race) && IniFile.Backups(race).Count == 0, IniFile.Backups(race).Count.ToString());
            string firstSave = File.ReadAllText(race);
            File.WriteAllText(src, "[Graphics Options]\r\nShadowMaps=0\r\n\r\n[Replay Graphics]\r\nShadowMaps=0\r\n");
            GraphicsPresets.SaveFromIni("Race", src);
            var backs = IniFile.Backups(race);
            Check("16 second save: the old preset is kept as a backup", backs.Count == 1 && File.ReadAllText(backs[0]) == firstSave, backs.Count + " backup(s)");
            Check("17 the new preset holds the new values, no temp file left", File.ReadAllText(race).Contains("ShadowMaps=0") && !File.Exists(race + ".tmp"), null);
            var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            map["Graphics Options"] = new Dictionary<string, string> { { "ShadowMaps", "2" } };
            GraphicsPresets.Write("Race", map, "a quality level");
            Check("18 a quality-level write (not the racer's save) makes no backup", IniFile.Backups(race).Count == 1 && File.ReadAllText(race).Contains("ShadowMaps=2"), null);
            // a backup that can't be made leaves the preset as it was
            string held = File.ReadAllText(race);
            bool threw = false;
            using (var lockIt = new FileStream(race, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { GraphicsPresets.SaveFromIni("Race", src); } catch { threw = true; }
            }
            Check("19 the preset is locked (no copy possible): the save fails and the preset is unchanged", threw && File.ReadAllText(race) == held, "threw " + threw);
            GraphicsPresets.Folder = gfx;
        }

        Check("14 real ini untouched", before == null || File.ReadAllBytes(realIni).SequenceEqual(before), null);
        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
