using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RaceMode
{
    // One program in Settings > Apps (an Uninstall registry entry): its name, icon (usually the exe) and folder.
    public class InstalledEntry
    {
        public string Name, Icon, Location;
    }

    // Finds race apps installed on this PC that are worth starting before a race (catalog apps with a "launch"
    // list in assets\close-catalog.json): Start menu shortcuts first (what the maker says to start), then
    // Settings > Apps entries, then the catalog's default paths. Read-only, and bounded in files and time.
    // Blocks: call it off the UI thread.
    public static class LaunchScan
    {
        public const int MaxShortcuts = 3000, MaxDepth = 5, MaxEntries = 4000;
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);

        // The common and per-user Start menu program folders.
        public static List<string> StartMenuDirs()
        {
            var l = new List<string>();
            foreach (var f in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
                try { var d = Environment.GetFolderPath(f); if (!string.IsNullOrEmpty(d)) l.Add(d); } catch { }
            return l;
        }

        // The real scan of this PC.
        public static List<LaunchItem> Find()
        {
            return Find(AppScan.Apps, StartMenuDirs(), ReadUninstall, DataDirs.ShortcutTarget, Budget);
        }

        // Public for tests: apps, folders to look for shortcuts in, the Settings > Apps reader, the shortcut reader
        // and the time allowed. One item per app, in catalog order, ticked; each points at an exe that exists.
        public static List<LaunchItem> Find(IEnumerable<KnownApp> apps, IEnumerable<string> menuDirs, Func<IEnumerable<InstalledEntry>> installed,
                                            Func<string, string> resolve, TimeSpan budget)
        {
            var clock = Stopwatch.StartNew();
            var list = (apps ?? new KnownApp[0]).Where(a => a != null && a.Launchable).ToList();
            var found = new Dictionary<KnownApp, string>();
            Func<KnownApp, string, int> rank = (a, exe) => Array.FindIndex(a.launch, n => string.Equals(n, ExeName(exe), StringComparison.OrdinalIgnoreCase));

            // 1. Start menu shortcuts. Several can point at one app: the one with its preferred exe wins.
            foreach (var lnk in Shortcuts(menuDirs, clock, budget))
            {
                if (clock.Elapsed > budget) break;
                string target; try { target = resolve == null ? null : resolve(lnk); } catch { target = null; }
                if (!IsExe(target) || !Exists(target)) continue;
                foreach (var a in list)
                {
                    int r = rank(a, target); if (r < 0) continue;
                    string had; if (found.TryGetValue(a, out had) && rank(a, had) <= r) continue;
                    found[a] = target;
                }
            }

            // 2. Settings > Apps: the entry's icon if it's the app's exe, else the exe in its folder (or one folder down).
            List<InstalledEntry> entries;
            try { entries = installed == null ? new List<InstalledEntry>() : (installed() ?? new InstalledEntry[0]).Where(e => e != null && !string.IsNullOrEmpty(e.Name)).Take(MaxEntries).ToList(); }
            catch { entries = new List<InstalledEntry>(); }
            foreach (var a in list)
            {
                if (found.ContainsKey(a) || a.installed == null || clock.Elapsed > budget) continue;
                foreach (var e in entries.Where(x => a.installed.Any(rx => SafeMatch(x.Name, rx))))
                {
                    var exe = FromEntry(a, e);
                    if (exe != null) { found[a] = exe; break; }
                }
            }

            // 3. the catalog's default install paths.
            foreach (var a in list)
            {
                if (found.ContainsKey(a)) continue;
                foreach (var p in a.paths ?? new string[0])
                {
                    string full; try { full = Environment.ExpandEnvironmentVariables(p); } catch { continue; }
                    if (IsExe(full) && rank(a, full) >= 0 && Exists(full)) { found[a] = full; break; }
                }
            }

            return list.Where(found.ContainsKey).Select(a => new LaunchItem { Name = a.label, Path = found[a], Args = "", ProcessName = ExeName(found[a]), Enabled = true }).ToList();
        }

        // Shortcut files under the folders, breadth first, at most MaxShortcuts and MaxDepth folders deep.
        static IEnumerable<string> Shortcuts(IEnumerable<string> dirs, Stopwatch clock, TimeSpan budget)
        {
            int n = 0;
            var queue = new Queue<KeyValuePair<string, int>>();
            foreach (var d in (dirs ?? new string[0]).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase)) queue.Enqueue(new KeyValuePair<string, int>(d, 0));
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (clock.Elapsed > budget) yield break;
                string[] files = new string[0], subs = new string[0];
                try { if (Directory.Exists(cur.Key)) { files = Directory.GetFiles(cur.Key, "*.lnk"); subs = Directory.GetDirectories(cur.Key); } } catch { }
                foreach (var f in files)
                {
                    if (++n > MaxShortcuts) yield break;
                    yield return f;
                }
                if (cur.Value < MaxDepth) foreach (var s in subs) queue.Enqueue(new KeyValuePair<string, int>(s, cur.Value + 1));
            }
        }

        static string FromEntry(KnownApp a, InstalledEntry e)
        {
            var icon = (e.Icon ?? "").Trim();
            int comma = icon.LastIndexOf(',');
            if (comma > 0 && !icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) icon = icon.Substring(0, comma);
            icon = icon.Trim().Trim('"');
            if (IsExe(icon) && a.launch.Contains(ExeName(icon), StringComparer.OrdinalIgnoreCase) && Exists(icon)) return icon;
            var dir = (e.Location ?? "").Trim().Trim('"');
            if (dir.Length == 0) return null;
            try
            {
                if (!Directory.Exists(dir)) return null;
                var folders = new List<string> { dir };
                folders.AddRange(Directory.GetDirectories(dir).Take(50));
                foreach (var n in a.launch)
                    foreach (var f in folders)
                    {
                        var p = Path.Combine(f, n + ".exe");
                        if (Exists(p)) return p;
                    }
            }
            catch { }
            return null;
        }

        // Settings > Apps entries for this user and the whole PC, 64- and 32-bit.
        public static List<InstalledEntry> ReadUninstall()
        {
            var list = new List<InstalledEntry>();
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                foreach (var sub in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    try
                    {
                        using (var k = root.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            foreach (var n in k.GetSubKeyNames())
                            {
                                if (list.Count >= MaxEntries) return list;
                                try
                                {
                                    using (var e = k.OpenSubKey(n))
                                    {
                                        var d = e == null ? null : e.GetValue("DisplayName") as string;
                                        if (string.IsNullOrEmpty(d)) continue;
                                        list.Add(new InstalledEntry { Name = d, Icon = e.GetValue("DisplayIcon") as string, Location = e.GetValue("InstallLocation") as string });
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
            return list;
        }

        // The catalog app a Launch item belongs to (by its process name or exe name), or null.
        public static KnownApp AppOf(IEnumerable<KnownApp> apps, LaunchItem it)
        {
            if (it == null) return null;
            var names = new[] { it.ProcessName, ExeName(it.Path) }.Where(n => !string.IsNullOrEmpty(n)).ToList();
            return (apps ?? new KnownApp[0]).FirstOrDefault(a => a != null && a.Launchable && names.Any(n =>
                a.launch.Contains(n, StringComparer.OrdinalIgnoreCase) || (a.process != null && a.process.Contains(n, StringComparer.OrdinalIgnoreCase))));
        }

        // Found apps that aren't on the list yet (same path, process name or app).
        public static List<LaunchItem> NotListed(IEnumerable<KnownApp> apps, IEnumerable<LaunchItem> found, IEnumerable<LaunchItem> current)
        {
            var cur = (current ?? new LaunchItem[0]).Where(c => c != null).ToList();
            var appList = (apps ?? new KnownApp[0]).ToList();
            var curApps = new HashSet<KnownApp>(cur.Select(c => AppOf(appList, c)).Where(a => a != null));
            return (found ?? new LaunchItem[0]).Where(f => f != null && !cur.Any(c =>
                       string.Equals(c.Path, f.Path, StringComparison.OrdinalIgnoreCase) ||
                       (!string.IsNullOrEmpty(c.ProcessName) && string.Equals(c.ProcessName, f.ProcessName, StringComparison.OrdinalIgnoreCase)))
                   && !curApps.Contains(AppOf(appList, f) ?? new KnownApp())).ToList();
        }

        static bool IsExe(string p) { return !string.IsNullOrEmpty(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase); }
        static bool Exists(string p) { try { return File.Exists(p); } catch { return false; } }
        static string ExeName(string p) { try { return Path.GetFileNameWithoutExtension(p ?? ""); } catch { return ""; } }
        static bool SafeMatch(string s, string rx)
        {
            try { return Regex.IsMatch(s, rx, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); } catch { return false; }
        }
    }
}
