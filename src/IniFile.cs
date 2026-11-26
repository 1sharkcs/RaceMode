// IniFile - careful, layout-preserving edits of iRacing's ini files (shared by DisplayGuard and GraphicsPresets).
//
// iRacing's ini lines look like  key=value<spaces><tab>; comment . Edits replace only the value, keep it padded to
// the same column, keep the tab + comment, the line endings and any BOM, and only touch keys inside the named
// sections (the same key can appear in several sections, e.g. [Graphics Options] and [Replay Graphics]).
// Writes back the file up first (newest 5 *.racemode-*.bak kept, never read-only) and are atomic
// (.racemode.tmp + File.Replace; the temp file is removed if anything fails).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RaceMode
{
    public class IniPatchResult
    {
        public List<string> Changes = new List<string>();   // "key old -> new"
        public List<string> Missing = new List<string>();   // "Section/key" wanted but not in the file
        public string Backup;
        public bool Written, ReadOnly;
    }

    public static class IniFile
    {
        static readonly Regex KeyLine = new Regex(@"^(?<k>\s*(?<name>[A-Za-z0-9_]+)\s*=[ \t]*)(?<v>[^\s;]*)(?<sp> *)(?<rest>.*)$", RegexOptions.Compiled);

        static Dictionary<string, Dictionary<string, string>> NewMap()
        {
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }

        // Section -> key -> value (first occurrence of a key within a section wins).
        public static Dictionary<string, Dictionary<string, string>> Read(string path)
        {
            return Parse(Decode(File.ReadAllBytes(path)));
        }

        public static Dictionary<string, Dictionary<string, string>> Parse(string text)
        {
            var map = NewMap();
            string section = "";
            foreach (var line in SplitLines(text))
            {
                var t = line.Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { section = t.Substring(1, t.Length - 2); continue; }
                var m = KeyLine.Match(line);
                if (!m.Success) continue;
                Dictionary<string, string> keys;
                if (!map.TryGetValue(section, out keys)) { keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); map[section] = keys; }
                var name = m.Groups["name"].Value;
                if (!keys.ContainsKey(name)) keys[name] = m.Groups["v"].Value;
            }
            return map;
        }

        // Puts the wanted values into the file. With apply=false it only reports what would change.
        // Throws on I/O errors (callers turn that into a "left alone" message); the temp file never survives.
        public static IniPatchResult Patch(string path, Dictionary<string, Dictionary<string, string>> want, bool apply)
        {
            var r = new IniPatchResult();
            var bytes = File.ReadAllBytes(path);
            var enc = DetectEncoding(bytes);
            var text = Decode(bytes);
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split(new[] { nl }, StringSplitOptions.None);

            string section = "";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { section = t.Substring(1, t.Length - 2); continue; }
                Dictionary<string, string> keys;
                if (!want.TryGetValue(section, out keys)) continue;
                var m = KeyLine.Match(lines[i]);
                if (!m.Success) continue;
                var name = m.Groups["name"].Value;
                string newVal;
                if (!keys.TryGetValue(name, out newVal)) continue;
                if (!seen.Add(section + "/" + name)) continue; // only the first occurrence in a section
                var oldVal = m.Groups["v"].Value;
                if (SameValue(oldVal, newVal)) continue;
                // Keep iRacing's layout: value padded to the same column, then its tab + comment.
                int pad = Math.Max(1, oldVal.Length + m.Groups["sp"].Value.Length - newVal.Length);
                lines[i] = m.Groups["k"].Value + newVal + new string(' ', pad) + m.Groups["rest"].Value;
                r.Changes.Add(name + " " + oldVal + " -> " + newVal);
            }
            r.Missing = want.SelectMany(s => s.Value.Keys.Select(k => s.Key + "/" + k)).Where(k => !seen.Contains(k)).ToList();
            if (r.Changes.Count == 0 || !apply) return r;

            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) { r.ReadOnly = true; return r; }

            string tmp = null;
            try
            {
                r.Backup = Backup(path);
                tmp = path + ".racemode.tmp";
                var body = enc.GetBytes(string.Join(nl, lines));
                bool hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                File.WriteAllBytes(tmp, hadBom ? Concat(new byte[] { 0xEF, 0xBB, 0xBF }, body) : body);
                File.Replace(tmp, path, null);
                tmp = null;
                r.Written = true;
                return r;
            }
            finally
            {
                if (tmp != null) { try { File.Delete(tmp); } catch { } }
            }
        }

        // Copies the file to a timed backup next to it before a write, and returns the backup's path. The newest 5
        // timed backups are kept; the one just made is never pruned, whatever the other names say. Throws if the copy
        // can't be made: callers must not write without it.
        public static string Backup(string path)
        {
            // Unique per write: two writes in the same second (screen check, then graphics) must not overwrite each other's backup.
            // Two writes can land in the same millisecond: then the backup gets a number instead of failing the write.
            var stamp = path + ".racemode-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var backup = stamp + ".bak";
            for (int n = 2; File.Exists(backup); n++) backup = stamp + "-" + n + ".bak";
            File.Copy(path, backup, false);
            File.SetAttributes(backup, FileAttributes.Normal); // a backup must never be undeletable
            // The very first file Race Mode ever changed is kept for good: a few Photo/Race flips would
            // otherwise rotate the user's original settings out of the newest-5 backups.
            // (".racemode.first" isn't a timed backup name, so pruning never removes it.)
            var first = path + ".racemode.first.bak";
            if (!File.Exists(first)) { File.Copy(path, first, false); File.SetAttributes(first, FileAttributes.Normal); }
            PruneBackups(path, 5, backup);
            return backup;
        }

        // Race Mode's timed backups of this file, newest first (full paths). Only names that are exactly one of its
        // backups: the folder search also finds lookalikes ("...racemode-notes.bak", and "...bak1" through Windows'
        // 3-letter-extension match), which are never listed or pruned.
        public static List<string> Backups(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var file = Path.GetFileName(full);
                var found = new DirectoryInfo(Path.GetDirectoryName(full)).GetFiles(file + ".racemode-*.bak")
                    .Where(f => IsTimedBackupName(file, f.Name)).Select(f => f.FullName);
                return NewestFirst(found, DateTime.Now);
            }
            catch { return new List<string>(); }
        }

        // Timed backup names, newest first: by the time in the name, then its -n. Not by the name as text ("...-123.bak"
        // is after "...-123-2.bak" as text). A name more than a minute later than now was made while the clock was wrong
        // (a flat CMOS battery, a wrong time zone, the hour after the clocks go back): it sorts after every other one, so
        // a backup made now is never pruned for a "newer" one from then. Names that aren't timed backups are left out.
        public static List<string> NewestFirst(IEnumerable<string> backups, DateTime now)
        {
            var limit = now.AddMinutes(1);
            return (backups ?? new string[0]).Where(b => b != null && BackupTime(b).HasValue)
                .OrderBy(b => BackupTime(b).Value > limit ? 1 : 0)
                .ThenByDescending(b => BackupTime(b).Value)
                .ThenByDescending(b => BackupNumber(b))
                .ThenByDescending(b => SafeName(b), StringComparer.Ordinal).ToList();
        }

        const string BackupName = @"\.racemode-(\d{8}-\d{6}-\d{3})(?:-(\d+))?\.bak$";

        // name is exactly <file>.racemode-yyyyMMdd-HHmmss-fff[-n].bak, with a real date (any letter case).
        static bool IsTimedBackupName(string file, string name)
        {
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(name) || !name.StartsWith(file + ".racemode-", StringComparison.OrdinalIgnoreCase)) return false;
            return Regex.IsMatch(name.Substring(file.Length), "^" + BackupName, RegexOptions.IgnoreCase) && BackupTime(name).HasValue;
        }

        // The file name part of a path, or "" for a path Windows can't read (invalid characters): never throws.
        static string SafeName(string path)
        {
            try { return Path.GetFileName(path ?? ""); } catch { return ""; }
        }

        static int BackupNumber(string backup)
        {
            var m = Regex.Match(SafeName(backup), BackupName, RegexOptions.IgnoreCase);
            int n;
            return m.Success && m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out n) ? n : 1;
        }

        // When a timed backup was made, from its name; null if it isn't one.
        public static DateTime? BackupTime(string backup)
        {
            var m = Regex.Match(SafeName(backup), BackupName, RegexOptions.IgnoreCase);
            DateTime t;
            if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
            return null;
        }

        public static bool SameValue(string a, string b)
        {
            double x, y;
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return Math.Abs(x - y) < 0.0005;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static string Decode(byte[] bytes)
        {
            var text = DetectEncoding(bytes).GetString(bytes);
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            return text;
        }

        static string[] SplitLines(string text)
        {
            return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        static Encoding DetectEncoding(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new UTF8Encoding(true);
            return Encoding.Default; // iRacing writes plain ANSI/ASCII
        }

        static byte[] Concat(byte[] a, byte[] b) { var c = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, c, 0, a.Length); Buffer.BlockCopy(b, 0, c, a.Length, b.Length); return c; }

        // made: the backup this write has just made, never removed.
        static void PruneBackups(string path, int keep, string made)
        {
            try
            {
                var keepMade = made == null ? null : Path.GetFullPath(made);
                foreach (var f in Backups(path).Skip(keep).Where(f => !string.Equals(f, keepMade, StringComparison.OrdinalIgnoreCase)))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }
    }
}
