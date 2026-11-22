using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace RaceMode
{
    // Finds where an app is actually installed instead of assuming the default folder.
    // Order: (1) the whole-PC registry (HKLM): Uninstall entries, 64- and 32-bit (DisplayIcon, InstallLocation), then
    // App Paths\<exe>; (2) the same for this user (HKCU), unless the lookup is machine-only; (3) the usual default
    // folders. The first path that exists on a local disk wins (network paths are never touched).
    // The registry is only read, never written. Reader and SubKeys can be swapped out (tests use a fake registry).
    public static class InstallFinder
    {
        // Reader(key, valueName) -> string value or null. Key starts with "HKLM\" or "HKCU\"; valueName "" = default value.
        public static Func<string, string, string> Reader = ReadRegistry;
        // SubKeys(key) -> names of the key's subkeys (empty if the key is missing).
        public static Func<string, string[]> SubKeys = ListSubKeys;

        // At most this many Uninstall entries are read under each Uninstall key.
        public const int MaxUninstallKeys = 4000;

        const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string UninstallWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

        // exeName: file name to look for ("obs64.exe"). displayPrefix: only Uninstall entries whose DisplayName starts
        // with this (null = any entry). relPaths: where the exe sits under InstallLocation (the exe name itself is always
        // tried too). defaults: today's default paths, tried last. Returns null if none of them exists.
        public static string Find(string exeName, string displayPrefix, string[] relPaths, string[] defaults)
        {
            return Find(exeName, displayPrefix, relPaths, defaults, false);
        }

        // machineOnly: read only HKLM, never HKCU. For an exe that's started elevated (through a UAC prompt), whose path
        // must not come from a place the signed-in user can write to.
        public static string Find(string exeName, string displayPrefix, string[] relPaths, string[] defaults, bool machineOnly)
        {
            var read = Reader; var subs = SubKeys;
            try
            {
                foreach (var hive in machineOnly ? new[] { "HKLM" } : new[] { "HKLM", "HKCU" })
                {
                    var roots = hive == "HKLM" ? new[] { @"HKLM\" + Uninstall, @"HKLM\" + UninstallWow } : new[] { hive + @"\" + Uninstall };
                    foreach (var root in roots)
                    {
                        var hit = FromUninstall(read, subs, root, exeName, displayPrefix, relPaths);
                        if (hit != null) return hit;
                    }
                    var p = CleanPath(Safe(read, hive + @"\" + AppPaths + @"\" + exeName, ""));
                    if (LocalFileExists(p)) return Full(p);
                }
            }
            catch { }
            foreach (var d in defaults ?? new string[0])
                if (LocalFileExists(d)) return d;
            return null;
        }

        static string FromUninstall(Func<string, string, string> read, Func<string, string[]> subs, string root, string exeName, string displayPrefix, string[] relPaths)
        {
            string[] names;
            try { names = subs(root) ?? new string[0]; } catch { return null; }
            int n = 0;
            foreach (var name in names)
            {
                if (++n > MaxUninstallKeys) break;
                var key = root + @"\" + name;
                if (displayPrefix != null)
                {
                    var dn = Safe(read, key, "DisplayName");
                    if (dn == null || !dn.TrimStart().StartsWith(displayPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                }
                var icon = CleanPath(Safe(read, key, "DisplayIcon"));
                if (icon != null && string.Equals(SafeFileName(icon), exeName, StringComparison.OrdinalIgnoreCase) && LocalFileExists(icon)) return Full(icon);
                var loc = CleanPath(Safe(read, key, "InstallLocation"));
                if (loc == null || IsNetworkPath(loc)) continue;
                var rels = new List<string>(relPaths ?? new string[0]); rels.Add(exeName);
                foreach (var rel in rels)
                {
                    string p; try { p = Path.Combine(loc, rel); } catch { continue; }
                    if (LocalFileExists(p)) return Full(p);
                }
            }
            return null;
        }

        // A UNC or other network path ("\\server\share\x.exe", "//server/x.exe"), or one on a mapped network drive.
        // Checked before File.Exists, so a lookup never reaches out to another machine.
        public static bool IsNetworkPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            var s = p.TrimStart();
            if (s.StartsWith(@"\\") || s.StartsWith("//") || s.StartsWith(@"\/") || s.StartsWith(@"/\")) return true;
            try
            {
                if (s.Length >= 2 && s[1] == ':' && char.IsLetter(s[0]))
                    return new DriveInfo(s.Substring(0, 1)).DriveType == DriveType.Network;
            }
            catch { }
            return false;
        }

        // File.Exists for a path on this PC's own disks; false for network paths, without touching them.
        public static bool LocalFileExists(string p)
        {
            if (string.IsNullOrEmpty(p) || IsNetworkPath(p)) return false;
            try { return File.Exists(p); } catch { return false; }
        }

        static string SafeFileName(string p) { try { return Path.GetFileName(p); } catch { return null; } }

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

        // HWiNFO is started through a UAC prompt (as administrator), so its path comes only from places that need
        // admin rights to change: HKLM Uninstall (64- and 32-bit), HKLM App Paths and the Program Files folders.
        public static string HwinfoExe()
        {
            return Cached("hwinfo", () => Find("HWiNFO64.EXE", "HWiNFO", new[] { "HWiNFO64.EXE" }, new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"HWiNFO64\HWiNFO64.EXE"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"HWiNFO64\HWiNFO64.EXE") }, true) ?? DefaultHwinfo);
        }
        public const string DefaultHwinfo = @"C:\Program Files\HWiNFO64\HWiNFO64.EXE";

        // Steam writes its own exe path (with forward slashes) to HKCU\Software\Valve\Steam\SteamExe. Only a path to a
        // steam.exe is taken from there.
        public static string SteamExe()
        {
            return Cached("steam", () =>
            {
                var p = CleanPath(Safe(Reader, @"HKCU\Software\Valve\Steam", "SteamExe"));
                if (p != null && string.Equals(SafeFileName(p), "steam.exe", StringComparison.OrdinalIgnoreCase) && LocalFileExists(p)) return Full(p);
                return Find("steam.exe", "Steam", new[] { "steam.exe" }, new[] { DefaultSteam }) ?? DefaultSteam;
            });
        }
        public const string DefaultSteam = @"C:\Program Files (x86)\Steam\steam.exe";

        // Lookups are read from a UI timer (HWiNFO hint) and a scan reads every Uninstall entry, so keep each answer
        // for 10 minutes (installing or moving an app is rare; a restart of Race Mode always looks again).
        static readonly Dictionary<string, KeyValuePair<DateTime, string>> _cache = new Dictionary<string, KeyValuePair<DateTime, string>>();
        public static void ClearCache() { lock (_cache) _cache.Clear(); }
        static string Cached(string key, Func<string> find)
        {
            lock (_cache)
            {
                KeyValuePair<DateTime, string> hit;
                if (_cache.TryGetValue(key, out hit) && (DateTime.UtcNow - hit.Key).TotalMinutes < 10) return hit.Value;
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
