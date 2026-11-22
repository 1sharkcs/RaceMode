using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace RaceMode
{
    // One app from assets\close-catalog.json: what it's called, whether it's usually fine to close before a race,
    // its process names (the first one is the name put on the Close list), default install paths and the names
    // it uses in Settings > Apps.
    public class KnownApp
    {
        public string label, category, why;
        public string[] process, paths, installed;
        public string[] launch;  // exe names (no .exe) worth starting before a race; empty: not offered for the Launch list
        public bool Launchable { get { return launch != null && launch.Length > 0; } }
        public bool streaming;   // a streaming tool: kept while Streaming is on, otherwise closable like anything else
        public bool Closable { get { return string.Equals(category, "close", StringComparison.OrdinalIgnoreCase); } }
        public bool ClosableWhen(bool streamingOn) { return Closable || (streaming && !streamingOn); }
        public string MainProcess { get { return process != null && process.Length > 0 ? process[0] : null; } }
    }

    // An app worth closing before a race, found on this PC.
    public class AppSuggestion
    {
        public string Label, Why, Process;   // Process: the app's main process name (shown, and used as its key)
        public string[] Names;               // every name that goes on the Close list for it (an app can run several)
        public bool Known;                   // false: a heavy app that isn't in the catalog ("check what it is")
        public bool Open, StartsWithWindows;
        public double RamMB, CpuPct;
        public int Processes;
        public double Score { get { return 3 * CpuPct + 10 * RamMB / 1024; } }
    }

    // Finds apps to suggest for the Close list: what's open now (measured for a few seconds), heavy apps that
    // aren't in the catalog, and catalog apps that are installed but not open. Read-only: nothing is closed here.
    public static class AppScan
    {
        static List<KnownApp> _apps;
        public static string LoadError;
        static readonly object _lock = new object();

        public static List<KnownApp> Apps { get { Load(); return _apps; } }

        static void Load()
        {
            lock (_lock)
            {
                if (_apps != null) return;
                _apps = new List<KnownApp>();
                try
                {
                    string json = null;
                    var file = Path.Combine(Shell.Home, "close-catalog.json");
                    if (BuildInfo.Repo.Length == 0 && File.Exists(file)) json = File.ReadAllText(file, Encoding.UTF8);   // local builds only
                    else
                        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("close-catalog.json"))
                            if (s != null) using (var r = new StreamReader(s, Encoding.UTF8)) json = r.ReadToEnd();
                    if (json == null) { LoadError = "The app list is missing from this build"; return; }
                    Load(json);
                }
                catch (Exception ex) { LoadError = "The app list couldn't be read: " + ex.Message; Shell.LogCrash(ex); }
            }
        }

        // Public for tests.
        public static void Load(string json)
        {
            var list = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Deserialize<List<KnownApp>>(json) ?? new List<KnownApp>();
            Func<string[], string[]> clean = x => (x ?? new string[0]).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray();
            foreach (var a in list.Where(a => a != null)) { a.process = clean(a.process); a.paths = clean(a.paths); a.installed = clean(a.installed); a.launch = clean(a.launch); }
            _apps = list.Where(a => a != null && !string.IsNullOrWhiteSpace(a.label) && a.process.Length > 0).ToList();
            _byName = null;
        }

        static Dictionary<string, List<KnownApp>> _byName;
        static Dictionary<string, List<KnownApp>> ByName()
        {
            var d = _byName;
            if (d != null) return d;
            d = new Dictionary<string, List<KnownApp>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in Apps)
                foreach (var n in a.process)
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    List<KnownApp> l; if (!d.TryGetValue(n, out l)) d[n] = l = new List<KnownApp>();
                    if (!l.Contains(a)) l.Add(a);
                }
            return _byName = d;
        }

        // The friendly name for a process name, if the catalog knows it ("chrome" -> "Google Chrome").
        public static string LabelFor(string process)
        {
            List<KnownApp> l;
            return process != null && ByName().TryGetValue(process, out l) && l.Count > 0 ? l[0].label : null;
        }

        // Shared helpers and runtimes: they belong to whatever app started them, so they're never suggested.
        static readonly HashSet<string> Helpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "msedgewebview2", "CefSharp.BrowserSubprocess", "QtWebEngineProcess", "crashpad_handler", "WerFault", "node", "python",
            "pythonw", "java", "javaw", "dotnet", "bash", "sh", "cmd", "powershell", "pwsh", "git", "conhost", "WindowsTerminal", "OpenConsole" };

        // ---------------- the scan ----------------

        class Proc { public int Id; public string Name, Path, Root; public double Cpu0, Cpu1; public double RamMB; public bool Window; }

        // Words in a program's product or company name that mark racing hardware, VR, voice or stream tools. An
        // unrecognised heavy app made by one of these is never suggested (it's probably needed for racing).
        static readonly string[] KeepWords = { "MOZA", "Simucube", "Granite Devices", "Asetek", "Fanatec", "Endor", "Simagic", "Thrustmaster",
            "Heusinkveld", "Cammus", "Conspit", "Sim-Lab", "Cube Controls", "Logitech", "SteamVR", "Valve", "Oculus", "Meta Platforms", "Pimax",
            "Varjo", "HTC", "Virtual Desktop", "Elgato", "Voicemeeter", "VB-Audio", "Discord", "TeamSpeak", "iRacing", "SimHub", "NVIDIA", "AMD", "Realtek" };

        public static List<AppSuggestion> Scan(TimeSpan sample, Func<string, bool> isProtected, ICollection<string> already)
        {
            return Scan(sample, isProtected, already, true);
        }

        // Measures what's open for `sample` (blocks: call it off the UI thread). isProtected: names Race Mode never
        // closes (iRacing, Windows, Launch apps, streaming apps while Streaming is on); it must be safe to call from
        // this thread (pass a snapshot, not a view of live UI lists). already: names already on the Close list.
        // streamingOn: Race Mode's Streaming setting. Off, streaming tools (OBS, Streamlabs...) can be suggested too.
        public static List<AppSuggestion> Scan(TimeSpan sample, Func<string, bool> isProtected, ICollection<string> already, bool streamingOn)
        {
            var result = new List<AppSuggestion>();
            int session; using (var me = Process.GetCurrentProcess()) session = me.SessionId;
            int myId; using (var me = Process.GetCurrentProcess()) myId = me.Id;
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
            Func<string, bool> prot = n => isProtected != null && isProtected(n);
            Func<string, bool> taken = n => already != null && already.Contains(n, StringComparer.OrdinalIgnoreCase);

            // 1. two snapshots of processor time, `sample` apart. Protected apps aren't measured, but their folders
            // are remembered: anything else in those folders belongs to them and is never suggested.
            var procs = new Dictionary<int, Proc>();
            var live = new List<Process>();
            var keepRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootsByName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                bool keep = false;
                try
                {
                    if (p.Id == myId || p.SessionId != session) continue;
                    allNames.Add(p.ProcessName);
                    if (Helpers.Contains(p.ProcessName)) continue;
                    var path = ImagePath(p.Id);
                    if (path == null || path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)) continue;   // part of Windows, or can't be read
                    var root = GroupKey(path);
                    HashSet<string> rs; if (!rootsByName.TryGetValue(p.ProcessName, out rs)) rootsByName[p.ProcessName] = rs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    rs.Add(root);
                    if (prot(p.ProcessName)) { keepRoots.Add(AppRoot(path)); continue; }
                    var r = new Proc { Id = p.Id, Name = p.ProcessName, Path = path, Root = root };
                    try { r.Cpu0 = p.TotalProcessorTime.TotalSeconds; } catch { r.Cpu0 = double.NaN; }
                    procs[p.Id] = r; live.Add(p); keep = true;
                }
                catch { }
                finally { if (!keep) p.Dispose(); }
            }
            var t0 = DateTime.UtcNow;
            Thread.Sleep(sample);
            double secs = Math.Max(0.5, (DateTime.UtcNow - t0).TotalSeconds);
            int cores = Math.Max(1, Environment.ProcessorCount);
            foreach (var p in live)
            {
                try
                {
                    Proc r; if (!procs.TryGetValue(p.Id, out r)) continue;
                    p.Refresh();
                    if (p.HasExited) { procs.Remove(p.Id); continue; }
                    try { r.Cpu1 = p.TotalProcessorTime.TotalSeconds; } catch { r.Cpu1 = r.Cpu0; }
                    r.RamMB = p.WorkingSet64 / 1048576.0;
                    try { r.Window = p.MainWindowHandle != IntPtr.Zero; } catch { }
                }
                catch { procs.Remove(p.Id); }
                finally { p.Dispose(); }
            }
            Func<Proc, double> cpuPct = r => double.IsNaN(r.Cpu0) ? 0 : Math.Max(0, 100 * (r.Cpu1 - r.Cpu0) / (secs * cores));
            // SteamVR runs through Steam: closing Steam would end the VR session, so Steam is never suggested then.
            bool steamVr = new[] { "vrserver", "vrmonitor", "vrcompositor", "vrdashboard" }.Any(allNames.Contains);
            Func<KnownApp, bool> closable = a => a.ClosableWhen(streamingOn) && !(steamVr && a.process.Contains("steam", StringComparer.OrdinalIgnoreCase));

            var startup = StartupNames();

            // 2. open apps the catalog knows. Apps to keep (and their folders) are never suggested.
            var byName = ByName();
            var known = new Dictionary<KnownApp, List<Proc>>();
            var rest = new List<Proc>();
            foreach (var r in procs.Values)
            {
                List<KnownApp> cands; KnownApp hit = null;
                if (byName.TryGetValue(r.Name, out cands)) hit = cands.FirstOrDefault(a => Matches(a, r.Path));
                if (hit == null) { rest.Add(r); continue; }
                List<Proc> l; if (!known.TryGetValue(hit, out l)) known[hit] = l = new List<Proc>();
                l.Add(r);
            }
            foreach (var kv in known)
            {
                var a = kv.Key;
                if (!closable(a) || a.process.Any(prot)) { foreach (var r in kv.Value) keepRoots.Add(AppRoot(r.Path)); continue; }
                var names = kv.Value.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Where(n => !taken(n)).ToArray();
                if (names.Length == 0) continue;
                var main = kv.Value.Where(r => !taken(r.Name)).OrderByDescending(r => r.Window).ThenByDescending(r => r.RamMB).First();
                result.Add(new AppSuggestion
                {
                    Label = a.label, Why = a.Closable ? a.why : "only needed when you stream", Process = main.Name, Names = names, Known = true, Open = true,
                    Processes = kv.Value.Count, RamMB = kv.Value.Sum(r => r.RamMB), CpuPct = kv.Value.Sum(cpuPct),
                    StartsWithWindows = a.process.Any(startup.Contains)
                });
            }

            // 3. heavy open apps the catalog doesn't know, grouped by their program folder. Left out: anything in a
            // protected or kept app's folder, an exe name another program also uses (the Close list closes by name),
            // and anything made by racing-hardware, VR, voice or stream-tool makers.
            foreach (var g in rest.GroupBy(r => r.Root, StringComparer.OrdinalIgnoreCase))
            {
                var key = g.Key;
                if (keepRoots.Any(k => SameOrInside(key, k) || SameOrInside(k, key))) continue;
                var list = g.ToList();
                var main = list.OrderByDescending(r => r.Window).ThenByDescending(r => r.RamMB).First();
                var mine = list.Where(r => string.Equals(r.Name, main.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                double ram = mine.Sum(r => r.RamMB), cpu = mine.Sum(cpuPct);
                if (ram < 300 && cpu < 2) continue;
                if (taken(main.Name) || prot(main.Name) || byName.ContainsKey(main.Name)) continue;
                HashSet<string> roots; if (rootsByName.TryGetValue(main.Name, out roots) && roots.Count > 1) continue;
                var maker = (ProductName(main.Path) ?? "") + " " + (Company(main.Path) ?? "");
                if (KeepWords.Any(w => maker.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                result.Add(new AppSuggestion
                {
                    Label = ProductName(main.Path) ?? main.Name, Why = null, Process = main.Name, Names = new[] { main.Name }, Known = false, Open = true,
                    Processes = mine.Count, RamMB = ram, CpuPct = cpu, StartsWithWindows = startup.Contains(main.Name)
                });
            }

            // 4. installed but not open now (so it's closed next time it is)
            var installedNames = InstalledNames();
            foreach (var a in Apps.Where(x => x.Closable))
            {
                if (known.ContainsKey(a) || a.process.Any(taken) || a.process.Any(prot) || !closable(a)) continue;
                bool there = a.paths.Any(p => { try { return File.Exists(Environment.ExpandEnvironmentVariables(p)); } catch { return false; } })
                          || a.installed.Any(rx => installedNames.Any(n => SafeMatch(n, rx)));
                if (!there) continue;
                result.Add(new AppSuggestion { Label = a.label, Why = a.why, Process = a.MainProcess, Names = a.process.ToArray(), Known = true, Open = false, StartsWithWindows = a.process.Any(startup.Contains) });
            }

            // one row per app
            return result.GroupBy(s => s.Process, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(s => s.Open).First())
                         .OrderByDescending(s => s.Open).ThenByDescending(s => s.Score).ThenBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // "a\b\c" is the same as or inside "a\b" (whole folder names only: "Program Files\Box" isn't inside "...\Boxcryptor").
        static bool SameOrInside(string path, string folder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
            var f = folder.TrimEnd('\\');
            return string.Equals(path.TrimEnd('\\'), f, StringComparison.OrdinalIgnoreCase) || path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // How unknown apps are grouped: by app folder, except in Temp and Steam's game folders (unrelated programs
        // share those), where each exe's own folder is used.
        static string GroupKey(string path)
        {
            var root = AppRoot(path);
            string dir; try { dir = Path.GetDirectoryName(path) ?? ""; } catch { dir = ""; }
            var temp = Path.GetTempPath().TrimEnd('\\');
            if (SameOrInside(dir, temp) || dir.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase) >= 0) return dir;
            return root;
        }

        // Before Race Mode closes a process by a catalog name, it checks the process really is that app (another
        // program can use the same exe name). Names not in the catalog: always true. Path unknown: true.
        public static bool BelongsTo(string name, string path)
        {
            List<KnownApp> l;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name) || !ByName().TryGetValue(name, out l)) return true;
            return l.Any(a => Matches(a, path));
        }

        // The catalog app a process name belongs to (null if none).
        public static KnownApp AppFor(string name)
        {
            List<KnownApp> l;
            return !string.IsNullOrEmpty(name) && ByName().TryGetValue(name, out l) && l.Count > 0 ? l[0] : null;
        }

        static bool SafeMatch(string s, string rx)
        {
            try { return Regex.IsMatch(s, rx, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); } catch { return false; }
        }

        // A process with a catalog name is that app only if it runs from the app's folder, or its file says it's that
        // product (a word of the app's name in the product or company name), or, for Windows' own apps, it's
        // Microsoft's and installed as a Windows app. "Agent" or "Code" from another vendor doesn't count.
        public static bool Matches(KnownApp a, string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            foreach (var p in a.paths)
            {
                string folder;
                try { folder = AppRoot(Environment.ExpandEnvironmentVariables(p)); } catch { continue; }
                if (folder.Length > 0 && SameOrInside(AppRoot(path), folder)) return true;
            }
            var company = Company(path) ?? "";
            var product = (ProductName(path) ?? "") + " " + company;
            var words = a.label.Split(new[] { ' ', '/', '(', ')', '-', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Where((w, i) => w.Length >= 4 || (i == 0 && w.Length >= 3)).Any(w => product.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return a.paths.Length == 0 && company.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0
                && (path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf(@"\SystemApps\", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // The app's own folder: "...\Program Files\Google\Chrome" for ...\Google\Chrome\Application\chrome.exe,
        // "...\AppData\Roaming\Spotify" for ...\Spotify\Spotify.exe; outside the usual folders, the exe's folder.
        public static string AppRoot(string exePath)
        {
            string dir;
            try { dir = Path.GetDirectoryName(exePath) ?? ""; } catch { return ""; }
            var bases = new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) };
            foreach (var b0 in bases)
            {
                if (string.IsNullOrEmpty(b0)) continue;
                var b = b0.TrimEnd('\\') + "\\";
                if (!(dir + "\\").StartsWith(b, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = dir.Length > b.Length ? dir.Substring(b.Length) : "";
                var seg = rel.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length == 0) return dir;
                int take = (seg.Length >= 3 || new[] { "WindowsApps", "Common Files", "Packages", "Microsoft", "Programs" }.Contains(seg[0], StringComparer.OrdinalIgnoreCase)) ? 2 : 1;
                return b + string.Join("\\", seg.Take(Math.Min(take, seg.Length)));
            }
            return dir;
        }

        static string ProductName(string path)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                foreach (var s in new[] { v.ProductName, v.FileDescription }) if (!string.IsNullOrWhiteSpace(s) && s.Trim().Length > 1) return s.Trim();
            }
            catch { }
            return null;
        }
        static string Company(string path) { try { return FileVersionInfo.GetVersionInfo(path).CompanyName; } catch { return null; } }

        // Full path of a process's exe without opening it for reading (works for most of the user's own processes).
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        public static string ImagePath(int pid)
        {
            var h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null; }
            finally { CloseHandle(h); }
        }

        // Names (exe names without .exe) of what starts with Windows: the Run lists (skipping ones switched off in
        // Task Manager > Startup apps) and the Startup folders (by shortcut name).
        static HashSet<string> StartupNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new[] { Registry.CurrentUser, Registry.LocalMachine };
            foreach (var root in roots)
                foreach (var sub in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" })
                {
                    try
                    {
                        using (var k = root.OpenSubKey(sub))
                        using (var ok = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + (sub.Contains("WOW6432Node") ? "Run32" : "Run")))
                        {
                            if (k == null) continue;
                            foreach (var v in k.GetValueNames())
                            {
                                var b = ok == null ? null : ok.GetValue(v) as byte[];
                                if (b != null && b.Length > 0 && (b[0] & 1) == 1) continue;   // switched off
                                var cmd = (k.GetValue(v) as string ?? "").Trim();
                                var m = Regex.Match(cmd, "^\"([^\"]+)\"|^(\\S+?\\.exe)", RegexOptions.IgnoreCase);
                                var exe = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                                if (exe.Length > 0) try { names.Add(Path.GetFileNameWithoutExtension(exe)); } catch { }
                            }
                        }
                    }
                    catch { }
                }
            foreach (var f in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
                try { foreach (var l in Directory.GetFiles(Environment.GetFolderPath(f), "*.lnk")) names.Add(Path.GetFileNameWithoutExtension(l)); } catch { }
            return names;
        }

        // Settings > Apps names (DisplayName in the Uninstall lists, for this user and the whole PC, 64- and 32-bit).
        static List<string> InstalledNames()
        {
            var names = new List<string>();
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                foreach (var sub in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    try
                    {
                        using (var k = root.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            foreach (var n in k.GetSubKeyNames())
                                try { using (var e = k.OpenSubKey(n)) { var d = e == null ? null : e.GetValue("DisplayName") as string; if (!string.IsNullOrEmpty(d)) names.Add(d); } } catch { }
                        }
                    }
                    catch { }
            return names;
        }
    }
}
