using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace RaceMode
{
    // Finds where an app is actually installed instead of assuming the default folder.
    // Order: (1) Uninstall entries in HKLM, HKLM\WOW6432Node and HKCU (InstallLocation, DisplayIcon),
    // (2) App Paths\<exe> in HKLM and HKCU, (3) the usual default folders. The first path that exists wins.
    // The registry is only read, never written. Reader and SubKeys can be swapped out (tests use a fake registry).
    public static class InstallFinder
    {
        // Reader(key, valueName) -> string value or null. Key starts with "HKLM\" or "HKCU\"; valueName "" = default value.
        public static Func<string, string, string> Reader = ReadRegistry;
        // SubKeys(key) -> names of the key's subkeys (empty if the key is missing).
        public static Func<string, string[]> SubKeys = ListSubKeys;

        const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string UninstallWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

        // exeName: file name to look for ("obs64.exe"). displayPrefix: only Uninstall entries whose DisplayName starts
        // with this (null = any entry). relPaths: where the exe sits under InstallLocation (the exe name itself is always
        // tried too). defaults: today's default paths, tried last. Returns null if none of them exists.
        public static string Find(string exeName, string displayPrefix, string[] relPaths, string[] defaults)
        {
            var read = Reader; var subs = SubKeys;
            try
            {
                foreach (var root in new[] { @"HKLM\" + Uninstall, @"HKLM\" + UninstallWow, @"HKCU\" + Uninstall })
                {
                    string[] names;
                    try { names = subs(root) ?? new string[0]; } catch { continue; }
                    foreach (var name in names)
                    {
                        var key = root + @"\" + name;
                        if (displayPrefix != null)
                        {
                            var dn = Safe(read, key, "DisplayName");
                            if (dn == null || !dn.TrimStart().StartsWith(displayPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        var icon = CleanPath(Safe(read, key, "DisplayIcon"));
                        if (icon != null && string.Equals(Path.GetFileName(icon), exeName, StringComparison.OrdinalIgnoreCase) && File.Exists(icon)) return Full(icon);
                        var loc = CleanPath(Safe(read, key, "InstallLocation"));
                        if (loc != null)
                        {
                            var rels = new List<string>(relPaths ?? new string[0]); rels.Add(exeName);
                            foreach (var rel in rels)
                            {
                                string p; try { p = Path.Combine(loc, rel); } catch { continue; }
                                if (File.Exists(p)) return Full(p);
                            }
                        }
                    }
                }
                foreach (var hive in new[] { "HKLM", "HKCU" })
                {
                    var p = CleanPath(Safe(read, hive + @"\" + AppPaths + @"\" + exeName, ""));
                    if (p != null && File.Exists(p)) return Full(p);
                }
            }
            catch { }
            foreach (var d in defaults ?? new string[0])
                if (!string.IsNullOrEmpty(d) && File.Exists(d)) return d;
            return null;
        }

        // ---- The apps Race Mode looks for. Each falls back to today's default path (even if it isn't there) where noted.

        public static string IRacingUI()
        {
            return Cached("iracing", () => Find("iRacingUI.exe", "iRacing", new[] { @"ui\iRacingUI.exe" }, new[] { DefaultIRacingUI }) ?? DefaultIRacingUI);
        }
        public const string DefaultIRacingUI = @"C:\Program Files (x86)\iRacing\ui\iRacingUI.exe";

        // OBS Studio's obs64.exe, or null if it isn't installed.
        public static string ObsExe()
        {
            return Cached("obs", () => Find("obs64.exe", "OBS Studio", new[] { @"bin\64bit\obs64.exe" }, new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"obs-studio\bin\64bit\obs64.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Steam\steamapps\common\OBS Studio\bin\64bit\obs64.exe") }));
        }
        public const string DefaultObs = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";

        public static string HwinfoExe()
        {
            return Cached("hwinfo", () => Find("HWiNFO64.EXE", "HWiNFO", new[] { "HWiNFO64.EXE" }, new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"HWiNFO64\HWiNFO64.EXE"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"HWiNFO64\HWiNFO64.EXE") }) ?? DefaultHwinfo);
        }
        public const string DefaultHwinfo = @"C:\Program Files\HWiNFO64\HWiNFO64.EXE";

        // Steam writes its own exe path (with forward slashes) to HKCU\Software\Valve\Steam\SteamExe.
        public static string SteamExe()
        {
            return Cached("steam", () =>
            {
                var p = CleanPath(Safe(Reader, @"HKCU\Software\Valve\Steam", "SteamExe"));
                if (p != null && File.Exists(p)) return Full(p);
                return Find("steam.exe", "Steam", new[] { "steam.exe" }, new[] { DefaultSteam }) ?? DefaultSteam;
            });
        }
        public const string DefaultSteam = @"C:\Program Files (x86)\Steam\steam.exe";

        // Lookups are read from a UI timer (HWiNFO hint), so keep each answer for a short while.
        static readonly Dictionary<string, KeyValuePair<DateTime, string>> _cache = new Dictionary<string, KeyValuePair<DateTime, string>>();
        public static void ClearCache() { lock (_cache) _cache.Clear(); }
        static string Cached(string key, Func<string> find)
        {
            lock (_cache)
            {
                KeyValuePair<DateTime, string> hit;
                if (_cache.TryGetValue(key, out hit) && (DateTime.UtcNow - hit.Key).TotalSeconds < 30) return hit.Value;
            }
            var v = find();
            lock (_cache) _cache[key] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, v);
            return v;
        }

        // "\"C:\x\app.exe\",0" -> C:\x\app.exe ; "c:/x/app.exe" -> c:\x\app.exe ; expands %VARS%. Null if empty.
        public static string CleanPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = Environment.ExpandEnvironmentVariables(raw.Trim());
            if (s.StartsWith("\""))
            {
                var end = s.IndexOf('"', 1);
                s = end > 0 ? s.Substring(1, end - 1) : s.Substring(1);
            }
            else
            {
                var comma = s.LastIndexOf(',');
                if (comma > 0 && comma > s.LastIndexOf('\\') && comma > s.LastIndexOf('/')) s = s.Substring(0, comma);
            }
            s = s.Trim().Replace('/', '\\');
            return s.Length == 0 ? null : s;
        }

        static string Full(string p) { try { return Path.GetFullPath(p); } catch { return p; } }

        static string Safe(Func<string, string, string> read, string key, string name)
        {
            try { return read(key, name); } catch { return null; }
        }

        static RegistryKey Open(string key)
        {
            RegistryKey root;
            if (key.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)) root = Registry.LocalMachine;
            else if (key.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)) root = Registry.CurrentUser;
            else return null;
            return root.OpenSubKey(key.Substring(5), false);
        }

        static string ReadRegistry(string key, string name)
        {
            try
            {
                using (var k = Open(key))
                {
                    if (k == null) return null;
                    var v = k.GetValue(name);
                    return v == null ? null : v.ToString();
                }
            }
            catch { return null; }
        }

        static string[] ListSubKeys(string key)
        {
            try
            {
                using (var k = Open(key)) return k == null ? new string[0] : k.GetSubKeyNames();
            }
            catch { return new string[0]; }
        }
    }
}
