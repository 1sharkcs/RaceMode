// Graphics presets - Race / Photo switch for iRacing's graphics.
//
// A preset is a plain ini file in RaceMode\graphics\ (race.ini, photo.ini) holding every key from iRacing's
// [Graphics Options] and [Replay Graphics] except the two the display guard owns (LimitFrameRate and
// DesiredFPSLimit, so the FPS cap is never touched by a preset). Applying writes only the values that differ,
// through IniFile (layout kept, backup, atomic write), never while the sim is running (iRacing rewrites the file
// from memory when it closes), and then re-reads the file to confirm every value.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace RaceMode
{
    public class PresetResult
    {
        public bool Ok, Pending, Failed;
        public string Mode = "", Message = "", Backup;
        public int Changed, Confirmed, Total;
        public List<string> Changes = new List<string>();
        public List<string> Mismatches = new List<string>();
    }

    public static class GraphicsPresets
    {
        public static readonly string[] Sections = { "Graphics Options", "Replay Graphics" };
        static readonly string[] GuardOwned = { "Graphics Options/LimitFrameRate", "Graphics Options/DesiredFPSLimit" };
        public static string Folder = Path.Combine(Shell.Data, "graphics");

        public static string Normalize(string mode) { return string.Equals(mode, "Photo", StringComparison.OrdinalIgnoreCase) ? "Photo" : "Race"; }
        public static string PresetPath(string mode) { return Path.Combine(Folder, Normalize(mode).ToLowerInvariant() + ".ini"); }
        public static bool Exists(string mode) { return File.Exists(PresetPath(mode)); }

        static bool Owned(string section, string key)
        {
            return GuardOwned.Contains(section + "/" + key, StringComparer.OrdinalIgnoreCase);
        }

        // Only the preset sections, minus the guard-owned keys.
        static Dictionary<string, Dictionary<string, string>> Filter(Dictionary<string, Dictionary<string, string>> map)
        {
            var o = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Sections)
            {
                Dictionary<string, string> keys;
                if (!map.TryGetValue(s, out keys)) continue;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in keys) if (!Owned(s, kv.Key)) d[kv.Key] = kv.Value;
                if (d.Count > 0) o[s] = d;
            }
            return o;
        }

        public static Dictionary<string, Dictionary<string, string>> Load(string mode)
        {
            return Filter(IniFile.Read(PresetPath(mode)));
        }

        static Dictionary<string, string> Copy(Dictionary<string, Dictionary<string, string>> map, string section)
        {
            Dictionary<string, string> keys;
            return map.TryGetValue(section, out keys) ? new Dictionary<string, string>(keys, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Saves the live iRacing graphics as a preset ("Save Current as Race/Photo"). Returns the key count.
        //  Race  = driving settings, with [Replay Graphics] mirrored to the same values so replays look exactly
        //          like racing (replays use their own [Replay Graphics] preset: iRacing support 31000149355).
        //  Photo = only [Replay Graphics], the high-quality replay look. Driving settings are never part of it,
        //          so Photo mode can't change how the car drives or frames per second while racing.
        // (ReplayRenderModes is NOT a "use replay settings" switch: it enables replay filters like grayscale/VHS,
        //  so it's mirrored like any other key and never forced on.)
        public static int SaveFromIni(string mode, string iniPath)
        {
            var live = Filter(IniFile.Read(iniPath));
            var preset = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var replay = Copy(live, "Replay Graphics");
            if (Normalize(mode) != "Photo")
            {
                var g = Copy(live, "Graphics Options");
                foreach (var k in replay.Keys.ToList()) { string v; if (g.TryGetValue(k, out v)) replay[k] = v; }
                preset["Graphics Options"] = g;
            }
            if (replay.Count > 0) preset["Replay Graphics"] = replay;
            return Write(mode, preset, Path.GetFileName(iniPath));
        }

        public static int Write(string mode, Dictionary<string, Dictionary<string, string>> map, string fromName)
        {
            var sb = new StringBuilder();
            sb.Append("; RaceMode ").Append(Normalize(mode)).Append(" graphics preset, saved ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append(" from ").Append(fromName).Append("\r\n");
            sb.Append("; Applied to iRacing only while the sim is closed. LimitFrameRate/DesiredFPSLimit belong to the display guard.\r\n");
            int n = 0;
            foreach (var s in Sections)
            {
                Dictionary<string, string> keys;
                if (!map.TryGetValue(s, out keys)) continue;
                sb.Append("\r\n[").Append(s).Append("]\r\n");
                foreach (var kv in keys) { sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n"); n++; }
            }
            Directory.CreateDirectory(Folder);
            var path = PresetPath(mode);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.ASCII);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            return n;
        }

        // First run: no Race preset yet, so the current settings become it (nothing in iRacing changes).
        // Photo mode needs Photo settings: made from iRacing's current replay settings at the chosen replay quality
        // (1 Race look, 2 Better, 3 Max), so all three choices work without saving anything by hand first.
        public static bool EnsurePhotoSeeded(string iniPath, int level)
        {
            try
            {
                if (Exists("Photo") || !File.Exists(iniPath)) return false;
                level = Math.Max(1, Math.Min(3, level));
                SaveFromIni("Photo", iniPath);
                Write("Photo", GraphicsCatalog.ComposePhoto(level, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Load("Photo")), "Replay quality " + GraphicsCatalog.PhotoNames[level]);
                return true;
            }
            catch (Exception ex) { Shell.LogCrash(ex); return false; }
        }

        public static void EnsureRaceSeeded(string iniPath)
        {
            try { if (!Exists("Race") && File.Exists(iniPath)) SaveFromIni("Race", iniPath); }
            catch (Exception ex) { Shell.LogCrash(ex); }
        }

        // Settings in iRacing that differ from the preset ("Graphics changed in iRacing").
        public static List<string> Differences(string mode, string iniPath)
        {
            var diff = new List<string>();
            var want = Load(mode);
            var live = IniFile.Read(iniPath);
            foreach (var s in want)
            {
                Dictionary<string, string> keys;
                live.TryGetValue(s.Key, out keys);
                foreach (var kv in s.Value)
                {
                    string v;
                    if (keys == null || !keys.TryGetValue(kv.Key, out v)) continue; // not in the file: nothing to compare
                    if (!IniFile.SameValue(v, kv.Value)) diff.Add(kv.Key + " " + kv.Value + " -> " + v + (s.Key == "Replay Graphics" ? " (replay)" : ""));
                }
            }
            return diff;
        }

        public static PresetResult Apply(string mode, string iniPath) { return Apply(mode, iniPath, null); }

        // Never throws. simRunningOverride is for tests; null = look at the real system.
        public static PresetResult Apply(string mode, string iniPath, bool? simRunningOverride)
        {
            var r = new PresetResult { Mode = Normalize(mode) };
            try
            {
                if (!Exists(r.Mode)) { r.Failed = true; r.Message = "No " + r.Mode + " settings saved yet. Set the graphics in iRacing, close it, then press Save Current as " + r.Mode; return r; }
                if (!File.Exists(iniPath)) { r.Failed = true; r.Message = "iRacing's graphics file wasn't found at " + iniPath; return r; }
                bool running;
                if (simRunningOverride.HasValue) running = simRunningOverride.Value;
                else { var ps = Process.GetProcessesByName(Monitor.SimName); running = ps.Length > 0; foreach (var p in ps) p.Dispose(); }
                if (running) { r.Pending = true; r.Message = r.Mode + " graphics will be set when you close iRacing"; return r; }

                var want = Load(r.Mode);
                var patch = IniFile.Patch(iniPath, want, true);
                if (patch.ReadOnly) { r.Failed = true; r.Message = "iRacing's graphics file is read-only, so " + r.Mode + " graphics couldn't be set. Right-click it, choose Properties and untick Read-only"; return r; }
                r.Changes = patch.Changes; r.Changed = patch.Changes.Count; r.Backup = patch.Backup;

                // Confirm: read the file back and check every preset value that the file has.
                var live = IniFile.Read(iniPath);
                foreach (var s in want)
                {
                    Dictionary<string, string> keys;
                    live.TryGetValue(s.Key, out keys);
                    foreach (var kv in s.Value)
                    {
                        string v;
                        if (keys == null || !keys.TryGetValue(kv.Key, out v)) continue;
                        r.Total++;
                        if (IniFile.SameValue(v, kv.Value)) r.Confirmed++; else r.Mismatches.Add(s.Key + "/" + kv.Key);
                    }
                }
                if (r.Total > 0 && r.Confirmed == r.Total)
                {
                    r.Ok = true;
                    r.Message = r.Mode + " graphics are set" + (r.Changed == 0 ? "" : " (" + r.Changed + " setting" + (r.Changed == 1 ? "" : "s") + " changed)") + ", and all " + r.Total + " checked in iRacing's file";
                }
                else if (r.Total == 0)
                {
                    // none of the preset's settings are in the file (a fresh install, or a file iRacing hasn't filled in
                    // yet): "only 0 of 0 settings match" said nothing useful
                    r.Failed = true;
                    r.Message = "iRacing's graphics file has none of the " + r.Mode + " settings yet. Start iRacing once so it writes them, then try again";
                }
                else
                {
                    r.Failed = true;
                    r.Message = r.Mode + " graphics didn't fully stick: only " + r.Confirmed + " of " + r.Total + " settings match" + (r.Mismatches.Count > 0 ? " (" + string.Join(", ", r.Mismatches.Take(5).ToArray()) + ")" : "");
                }
                return r;
            }
            catch (Exception ex)
            {
                Shell.LogCrash(ex);
                r.Failed = true; r.Ok = false;
                r.Message = "Couldn't set " + r.Mode + " graphics, so iRacing's file was left as it was: " + ex.Message;
                return r;
            }
        }
    }
}
