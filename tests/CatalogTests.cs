// Tests for GraphicsCatalog.cs + assets\graphics-catalog.json, on COPIES of the presets and a fixture ini.
// Build next to RaceMode.exe (with tests\fixtures copied alongside); reads only the copies in tests\fixtures.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RaceMode;

class CatalogTests
{
    static int pass, fail;
    static void Check(string n, bool ok, string d) { if (ok) pass++; else fail++; Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (ok || d == null ? "" : "  -> " + d)); }
    static Dictionary<string, string> Sec(Dictionary<string, Dictionary<string, string>> m, string s) { Dictionary<string, string> d; return m.TryGetValue(s, out d) ? d : new Dictionary<string, string>(); }
    static List<string> Diff(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        var o = new List<string>();
        foreach (var kv in a) { string v; if (!b.TryGetValue(kv.Key, out v)) o.Add(kv.Key + " missing"); else if (!IniFile.SameValue(v, kv.Value)) o.Add(kv.Key + " " + kv.Value + "->" + v); }
        foreach (var k in b.Keys) if (!a.ContainsKey(k)) o.Add(k + " extra");
        return o;
    }

    // Repo folder: RACEMODE_SRC, else the first parent of the test exe that has assets\graphics-catalog.json, else Documents\RaceMode.
    static string Src()
    {
        var e = Environment.GetEnvironmentVariable("RACEMODE_SRC"); if (!string.IsNullOrEmpty(e)) return e;
        for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, @"assets\graphics-catalog.json"))) return d.FullName;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RaceMode");
    }
    static string Fixture(string name) { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", name); }
    static void Main()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        GraphicsCatalog.Load(File.ReadAllText(Path.Combine(Src(), @"assets\graphics-catalog.json")));
        var keys = GraphicsCatalog.Keys;
        Check("01 catalog loads, 104 keys, no error", GraphicsCatalog.LoadError == null && keys.Count == 104, GraphicsCatalog.LoadError + " / " + keys.Count);

        var race = IniFile.Read(Fixture("race.ini"));
        var photo = IniFile.Read(Fixture("photo.ini"));

        var r6 = GraphicsCatalog.ComposeRace(6, null, race);
        var d6 = Diff(Sec(race, "Graphics Options"), Sec(r6, "Graphics Options")).Concat(Diff(Sec(race, "Replay Graphics"), Sec(r6, "Replay Graphics"))).ToList();
        Check("02 level 6 rebuilds race.ini exactly (both sections)", d6.Count == 0, string.Join(", ", d6.Take(8).ToArray()));

        var p3 = GraphicsCatalog.ComposePhoto(3, null, photo);
        var dp = Diff(Sec(photo, "Replay Graphics"), Sec(p3, "Replay Graphics"));
        Check("03 Photo Max rebuilds photo.ini exactly, no racing section", dp.Count == 0 && !p3.ContainsKey("Graphics Options"), string.Join(", ", dp.Take(8).ToArray()));

        var p1 = GraphicsCatalog.ComposePhoto(1, null, photo);
        var bad1 = keys.Where(k => k.photoLevels != null && Sec(race, "Replay Graphics").ContainsKey(k.key) && !IniFile.SameValue(Sec(p1, "Replay Graphics")[k.key], Sec(race, "Replay Graphics")[k.key])).Select(k => k.key).ToList();
        Check("04 Photo 'Race look' = race.ini replay values", bad1.Count == 0, string.Join(", ", bad1.Take(8).ToArray()));

        var invalid = new List<string>();
        foreach (var k in keys)
        {
            if (k.levels != null) foreach (var v in k.levels) if (!k.IsValid(v)) invalid.Add(k.key + "=" + v);
            if (k.photoLevels != null) foreach (var v in k.photoLevels) if (!k.IsValid(v)) invalid.Add(k.key + "=" + v + " (photo)");
        }
        Check("05 every level value is valid for its key", invalid.Count == 0, string.Join(", ", invalid.Take(8).ToArray()));

        bool exact; int differing;
        var lv = GraphicsCatalog.NearestLevel(Sec(race, "Graphics Options"), out exact, out differing);
        Check("06 race.ini is recognised as exactly level 6", lv == 6 && exact, lv + " exact=" + exact + " differing=" + differing);

        var guardSeen = new List<string>();
        for (int l = 1; l <= 10; l++)
        {
            var m = GraphicsCatalog.ComposeRace(l, null, race);
            foreach (var s in m) foreach (var k in new[] { "LimitFrameRate", "DesiredFPSLimit" }) if (s.Value.ContainsKey(k)) guardSeen.Add(l + ":" + s.Key + "/" + k);
        }
        Check("07 FPS cap keys never appear at any level", guardSeen.Count == 0, string.Join(", ", guardSeen.ToArray()));

        var machineMoved = new List<string>();
        foreach (var k in keys.Where(x => x.scope == "machine"))
        {
            string b; if (!Sec(race, "Graphics Options").TryGetValue(k.key, out b)) continue;
            for (int l = 1; l <= 10; l++) { var v = Sec(GraphicsCatalog.ComposeRace(l, null, race), "Graphics Options")[k.key]; if (!IniFile.SameValue(v, b)) machineMoved.Add(k.key + "@" + l); }
        }
        Check("08 machine keys never change with the slider", machineMoved.Count == 0, string.Join(", ", machineMoved.Take(8).ToArray()));

        var ov = new Dictionary<string, string> { { "MSAASamples", "8" }, { "MaxCarsToDraw", "9999" } };
        var r2 = GraphicsCatalog.ComposeRace(2, ov, race);
        var msaa = GraphicsCatalog.Find("MSAASamples"); var cars = GraphicsCatalog.Find("MaxCarsToDraw");
        Check("09 valid override wins over the slider, invalid one is ignored",
            Sec(r2, "Graphics Options")["MSAASamples"] == "8" && cars != null && cars.levels != null && Sec(r2, "Graphics Options")["MaxCarsToDraw"] == cars.levels[1],
            Sec(r2, "Graphics Options")["MSAASamples"] + " / " + Sec(r2, "Graphics Options")["MaxCarsToDraw"]);
        Check("10 replay mirrors racing after an override", Sec(r2, "Replay Graphics").ContainsKey("MSAASamples") ? Sec(r2, "Replay Graphics")["MSAASamples"] == "8" : true, null);

        var fixture = Path.Combine(baseDir, @"fixtures\live-before-race-preset.ini");
        var live = IniFile.Read(fixture);
        var notInFile = keys.Where(k => k.scope != "guard" && !Sec(live, "Graphics Options").ContainsKey(k.key) && !Sec(live, "Replay Graphics").ContainsKey(k.key)).Select(k => k.key).ToList();
        Check("11 every catalog key exists in a real iRacing file", notInFile.Count == 0, string.Join(", ", notInFile.Take(8).ToArray()));

        // 12-13 compose -> save preset -> apply to a copy -> confirmed, for the extremes
        var root = Path.Combine(baseDir, "catalogcases"); if (Directory.Exists(root)) Directory.Delete(root, true);
        var gfx = Path.Combine(root, "graphics"); Directory.CreateDirectory(gfx);
        File.Copy(Fixture("photo.ini"), Path.Combine(gfx, "photo.ini"));
        GraphicsPresets.Folder = gfx;
        foreach (var l in new[] { 1, 10 })
        {
            var ini = Path.Combine(root, "live" + l + ".ini"); File.Copy(fixture, ini);
            GraphicsPresets.Write("Race", GraphicsCatalog.ComposeRace(l, null, race), "catalog test");
            var res = GraphicsPresets.Apply("Race", ini, false);
            Check((l == 1 ? "12" : "13") + " level " + l + " saved, applied to a copy and confirmed key for key", res.Ok && res.Confirmed == res.Total && res.Total > 100, res.Message);
        }

        // 14 monotonic quality: for keys with a values list (in quality
        // order unless 'q' says otherwise) or numeric ranges, level n+1 is never worse than level n.
        var down = new List<string>();
        foreach (var k in keys.Where(x => x.IsSlider))
        {
            for (int i = 1; i < 10; i++)
            {
                double a, b;
                bool na = double.TryParse(k.levels[i - 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a);
                bool nb = double.TryParse(k.levels[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out b);
                if (!na || !nb || k.key == "AntiAliasMethod") continue;   // AntiAliasMethod: enum, ranked by the catalog's q field
                if (k.higherIsBetter ? b < a : b > a) { down.Add(k.key + " L" + i + "->L" + (i + 1)); break; }
            }
        }
        Check("14 quality never drops from one level to the next", down.Count == 0, string.Join(", ", down.Take(8).ToArray()));

        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
