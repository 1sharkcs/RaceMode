// Graphics catalog - the data behind the Race quality slider, the Photo slider and the Advanced editor.
//
// assets\graphics-catalog.json (embedded in the exe; a graphics-catalog.json next to the exe overrides it for
// testing) describes every iRacing graphics key: plain label and description, CPU/GPU cost, valid values, and
// the value at each slider level. Composing never touches iRacing directly: the result is saved as the Race or
// Photo preset (GraphicsPresets.Write) and applied through the same pending/confirm path as before.
//
//  Race  level 1..10: [Graphics Options] from the levels; [Replay Graphics] mirrors racing (replays look like racing).
//  Photo level 1..3:  [Replay Graphics] only ("Race look", "Better", "Max").
//  Overrides (the Advanced editor) pin single keys and win over the slider.
//  Keys the slider doesn't own (user/machine scope, or unknown to the catalog) keep the base preset's value;
//  the display guard's keys (LimitFrameRate, DesiredFPSLimit) never appear in a preset at all.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace RaceMode
{
    public class CatalogValue { public string v; public string label; }
    public class CatalogRange { public double min; public double max; public List<double> steps; }
    public class CatalogCost { public int cpu; public int gpu; public string evidence; }
    public class CatalogRec { public string race; public string photo; }

    public class CatalogKey
    {
        public string key, group, label, desc, scope;
        public bool inReplay;
        public bool higherIsBetter = true;
        public CatalogCost cost;
        public List<CatalogValue> values;
        public CatalogRange range;
        public CatalogRec rec;
        public List<string> levels;        // 10 entries, slider scope only
        public List<string> photoLevels;   // 3 entries, keys in [Replay Graphics] only

        public bool IsSlider { get { return scope == "slider" && levels != null && levels.Count == 10; } }

        // Label for a value ("4" -> "4x"), or the value itself.
        public string LabelFor(string v)
        {
            if (values != null) foreach (var x in values) if (IniFile.SameValue(x.v, v)) return x.label;
            return v;
        }

        // True when v is one of the listed values, or a number inside the range.
        public bool IsValid(string v)
        {
            if (v == null) return false;
            if (values != null && values.Count > 0) return values.Any(x => IniFile.SameValue(x.v, v));
            if (range != null)
            {
                double d;
                return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= range.min - 1e-9 && d <= range.max + 1e-9;
            }
            return true;   // no rule known: accept (the catalog validator keeps these rare)
        }
    }

    public class CatalogFile { public int version; public List<CatalogKey> keys; }

    public static class GraphicsCatalog
    {
        public const string Race = "Graphics Options", Replay = "Replay Graphics";
        public static readonly string[] LevelNames = { "", "Lowest", "Very low", "Low", "Medium-low", "Medium", "Medium-high", "High", "Very high", "Ultra", "Maximum" };   // reads in order, no symbols
        public static readonly string[] PhotoNames = { "", "Race look", "Better", "Max" };

        static List<CatalogKey> _keys;
        static Dictionary<string, CatalogKey> _byKey;
        public static string LoadError;

        public static List<CatalogKey> Keys { get { Ensure(); return _keys; } }

        public static CatalogKey Find(string key)
        {
            Ensure();
            CatalogKey k; return key != null && _byKey.TryGetValue(key, out k) ? k : null;
        }

        static void Ensure()
        {
            if (_keys != null) return;
            _keys = new List<CatalogKey>(); _byKey = new Dictionary<string, CatalogKey>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string json = null;
                var file = Path.Combine(Shell.Home, "graphics-catalog.json");
                if (BuildInfo.Repo.Length == 0 && File.Exists(file)) json = File.ReadAllText(file, Encoding.UTF8);   // local builds only
                else
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("graphics-catalog.json"))
                        if (s != null) using (var r = new StreamReader(s, Encoding.UTF8)) json = r.ReadToEnd();
                if (json == null) { LoadError = "The graphics catalog is missing from this build"; return; }
                Load(json);
            }
            catch (Exception ex) { LoadError = "The graphics catalog couldn't be read: " + ex.Message; Shell.LogCrash(ex); }
        }

        // Public for tests.
        public static void Load(string json)
        {
            var f = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<CatalogFile>(json);
            _keys = new List<CatalogKey>(); _byKey = new Dictionary<string, CatalogKey>(StringComparer.OrdinalIgnoreCase);
            if (f == null || f.keys == null) { LoadError = "The graphics catalog is empty"; return; }
            foreach (var k in f.keys)
            {
                if (k == null || string.IsNullOrEmpty(k.key) || _byKey.ContainsKey(k.key)) continue;
                if (k.levels != null && k.levels.Count != 10) k.levels = null;            // malformed: treat as not on the slider
                if (k.photoLevels != null && k.photoLevels.Count != 3) k.photoLevels = null;
                _keys.Add(k); _byKey[k.key] = k;
            }
            LoadError = null;
        }

        static Dictionary<string, Dictionary<string, string>> Clone(Dictionary<string, Dictionary<string, string>> m)
        {
            var o = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (m != null) foreach (var s in m) o[s.Key] = new Dictionary<string, string>(s.Value, StringComparer.OrdinalIgnoreCase);
            return o;
        }

        static string Pick(Dictionary<string, string> overrides, CatalogKey k, string fromLevel)
        {
            string v;
            if (overrides != null && overrides.TryGetValue(k.key, out v) && k.IsValid(v)) return v;
            return fromLevel;
        }

        // Race preset for a slider level (1..10) plus pinned overrides, starting from the current Race preset.
        public static Dictionary<string, Dictionary<string, string>> ComposeRace(int level, Dictionary<string, string> overrides, Dictionary<string, Dictionary<string, string>> basePreset)
        {
            Ensure();
            level = Math.Max(1, Math.Min(10, level));
            var o = Clone(basePreset);
            if (!o.ContainsKey(Race)) o[Race] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var race = o[Race];
            foreach (var k in _keys)
            {
                if (k.scope == "machine" || k.scope == "guard") continue;
                string v = null;
                if (k.IsSlider) v = Pick(overrides, k, k.levels[level - 1]);
                else if (k.scope == "user") { string cur; race.TryGetValue(k.key, out cur); v = Pick(overrides, k, cur); }
                if (v == null || !race.ContainsKey(k.key) && !k.IsSlider) continue;
                race[k.key] = v;
            }
            // Replays look like racing: every key the replay section has takes the racing value.
            Dictionary<string, string> replay;
            if (o.TryGetValue(Replay, out replay))
                foreach (var key in replay.Keys.ToList()) { string v; if (race.TryGetValue(key, out v)) replay[key] = v; }
            return o;
        }

        // Photo preset for a level (1..3) plus pinned overrides: [Replay Graphics] only.
        public static Dictionary<string, Dictionary<string, string>> ComposePhoto(int level, Dictionary<string, string> overrides, Dictionary<string, Dictionary<string, string>> basePreset)
        {
            Ensure();
            level = Math.Max(1, Math.Min(3, level));
            var o = Clone(basePreset);
            o.Remove(Race);                               // Photo never changes how the car drives
            if (!o.ContainsKey(Replay)) o[Replay] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var replay = o[Replay];
            foreach (var k in _keys)
            {
                if (k.photoLevels == null || k.scope == "machine" || k.scope == "guard") continue;
                replay[k.key] = Pick(overrides, k, k.photoLevels[level - 1]);
            }
            return o;
        }

        // Which slider level a set of racing values is closest to (most matching slider keys).
        // exact = every slider key matches that level.
        public static int NearestLevel(Dictionary<string, string> raceValues, out bool exact, out int differing)
        {
            Ensure();
            int best = 6, bestMiss = int.MaxValue;
            var slider = _keys.Where(k => k.IsSlider).ToList();
            for (int lv = 1; lv <= 10; lv++)
            {
                int miss = 0;
                foreach (var k in slider)
                {
                    string v;
                    if (raceValues == null || !raceValues.TryGetValue(k.key, out v)) continue;
                    if (!IniFile.SameValue(v, k.levels[lv - 1])) miss++;
                }
                // Ties go to the level nearest 6 (the default), so a close call doesn't jump to an extreme.
                if (miss < bestMiss || miss == bestMiss && Math.Abs(lv - 6) < Math.Abs(best - 6)) { best = lv; bestMiss = miss; }
            }
            exact = bestMiss == 0; differing = bestMiss == int.MaxValue ? 0 : bestMiss;
            return best;
        }

        public static int NearestPhotoLevel(Dictionary<string, string> replayValues, out bool exact)
        {
            Ensure();
            int best = 3, bestMiss = int.MaxValue;
            for (int lv = 1; lv <= 3; lv++)
            {
                int miss = 0;
                foreach (var k in _keys.Where(x => x.photoLevels != null))
                {
                    string v;
                    if (replayValues == null || !replayValues.TryGetValue(k.key, out v)) continue;
                    if (!IniFile.SameValue(v, k.photoLevels[lv - 1])) miss++;
                }
                if (miss < bestMiss) { best = lv; bestMiss = miss; }
            }
            exact = bestMiss == 0;
            return best;
        }

        // Plain cost line for the editor ("CPU: high · GPU: low"); never colour alone.
        public static string CostText(CatalogKey k)
        {
            if (k.cost == null) return "";
            string[] w = { "none", "low", "medium", "high" };
            Func<int, string> t = n => w[Math.Max(0, Math.Min(3, n))];
            return "CPU: " + t(k.cost.cpu) + " · GPU: " + t(k.cost.gpu) + (k.cost.evidence == "unverified" || k.cost.evidence == "anecdote" ? " (estimate)" : "");
        }
    }
}
