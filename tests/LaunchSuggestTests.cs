// Launch-list suggestion tests for RaceMode.LaunchScan: race apps found from Start menu shortcuts, Settings > Apps
// entries and default paths. Shortcuts and programs are fakes in a temp folder (empty .exe files, never run); the
// Settings > Apps list is a fake reader. The only real-PC check is a read-only scan. Nothing is started or written
// outside the temp folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RaceMode;

class LaunchSuggestTests
{
    static int pass, fail;
    static string Root;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok || detail == null ? "" : "  -> " + detail));
    }

    static string Exe(string rel)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        File.WriteAllBytes(p, new byte[0]);
        return p;
    }
    static void Lnk(string rel, string target) { DataDirs.CreateShortcut(Path.Combine(Root, rel), target, Path.GetDirectoryName(target)); }

    static KnownApp App(string label, string[] launch, string[] paths, string[] installed, params string[] process)
    {
        return new KnownApp { label = label, category = "keep", why = "test app", process = process, paths = paths ?? new string[0], installed = installed ?? new string[0], launch = launch ?? new string[0] };
    }
    static string Show(IEnumerable<LaunchItem> l) { return string.Join(" | ", l.Select(i => i.Name + "=" + i.Path).ToArray()); }

    static int Main()
    {
        Root = Path.Combine(Path.GetTempPath(), "RaceModeLaunchTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        try
        {
            // ---- the real catalog
            var real = AppScan.Apps;
            var launchable = real.Where(a => a.Launchable).ToList();
            Check("catalog: loads with launchable race apps", AppScan.LoadError == null && launchable.Count >= 10, AppScan.LoadError ?? launchable.Count.ToString());
            foreach (var want in new[] { "SimHub", "Crew Chief", "Trading Paints", "Garage 61", "Logitech G HUB", "Simagic SimPro", "Fanatec software", "iOverlay", "RaceLab" })
                Check("catalog: " + want + " can be suggested", launchable.Any(a => a.label == want), null);
            // Red-team fixes: unverified exe names are not suggested; the G HUB tray app counts as G HUB.
            Check("catalog: Simucube is not suggested (its exe name is unverified)", !launchable.Any(a => a.label == "Simucube"), null);
            var simagic = real.FirstOrDefault(a => a.label == "Simagic SimPro");
            Check("catalog: Simagic starts simpro3 only, not its background daemon", simagic != null && simagic.launch != null && simagic.launch.Length == 1 && simagic.launch[0] == "simpro3", null);
            var ghub = real.FirstOrDefault(a => a.label == "Logitech G HUB");
            Check("catalog: G HUB tray app is one of G HUB's processes", ghub != null && ghub.process.Contains("lghub_system_tray"), null);
            var bad = launchable.Where(a => a.launch.Any(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || n.IndexOfAny(new[] { '\\', '/', '*', '%' }) >= 0)).Select(a => a.label).ToList();
            Check("catalog: launch names are plain exe names", bad.Count == 0, string.Join(", ", bad.ToArray()));
            bad = launchable.Where(a => a.category != "keep").Select(a => a.label).ToList();
            Check("catalog: launch apps are all kept-open apps (never closed)", bad.Count == 0, string.Join(", ", bad.ToArray()));
            Check("catalog: iRacing itself and browsers are not offered", !launchable.Any(a => a.label == "iRacing" || a.Closable), null);

            // ---- fakes
            Environment.SetEnvironmentVariable("RM_LAUNCHTEST", Root);
            var simhub = Exe(@"PF86\SimHub\SimHubWPF.exe");
            var unins = Exe(@"PF86\SimHub\unins000.exe");
            var tray = Exe(@"PF\LGHUB\system_tray\lghub_system_tray.exe");
            var lghub = Exe(@"PF\LGHUB\lghub.exe");
            var crew = Exe(@"PF86\Britton IT Ltd\CrewChiefV4\CrewChiefV4.exe");
            var simpro = Exe(@"PF86\Simagic\Simpro3\bin\simpro3.exe");
            var paints = Exe(@"PF86\Rhinode LLC\Trading Paints\Trading Paints.exe");
            var g61launcher = Exe(@"Roaming\garage61-install\garage61-launcher.exe");
            var g61agent = Exe(@"Roaming\garage61-install\garage61-agent.exe");
            var discord = Exe(@"Local\Discord\Discord.exe");
            Lnk(@"Common\Programs\SimHub\SimHub.lnk", simhub);
            Lnk(@"Common\Programs\SimHub\Uninstall SimHub.lnk", unins);
            Lnk(@"User\Programs\Logitech\Logitech G HUB.lnk", tray);
            Lnk(@"User\Programs\Garage 61 Telemetry Agent.lnk", g61agent);
            Lnk(@"User\Programs\Garage 61 Launcher.lnk", g61launcher);
            Lnk(@"User\Programs\Discord.lnk", discord);
            var ghost = Exe(@"PF\Gone\iOverlay.exe");
            Lnk(@"User\Programs\iOverlay.lnk", ghost);
            File.Delete(ghost);   // a shortcut left behind by an uninstall
            Lnk(@"User\Programs\a\b\c\d\e\f\g\Deep SimHub.lnk", simhub);
            var menus = new[] { Path.Combine(Root, @"Common\Programs"), Path.Combine(Root, @"User\Programs"), Path.Combine(Root, "Missing") };

            var entries = new List<InstalledEntry> {
                new InstalledEntry { Name = "CrewChiefV4", Icon = "\"" + crew + "\",0" },
                new InstalledEntry { Name = "Simpro3", Location = Path.Combine(Root, @"PF86\Simagic\Simpro3") },
                new InstalledEntry { Name = "Logitech G HUB", Icon = lghub },
                new InstalledEntry { Name = "Something Else", Icon = paints },
                null };

            var apps = new List<KnownApp> {
                App("SimHub", new[] { "SimHubWPF" }, null, new[] { "^SimHub version" }, "SimHubWPF"),
                App("Crew Chief", new[] { "CrewChiefV4" }, null, new[] { "^CrewChiefV4$" }, "CrewChiefV4"),
                App("Trading Paints", new[] { "Trading Paints" }, new[] { @"%RM_LAUNCHTEST%\PF86\Rhinode LLC\Trading Paints\Trading Paints.exe" }, new[] { "^Trading Paints$" }, "Trading Paints"),
                App("Garage 61", new[] { "garage61-launcher", "garage61-agent" }, null, null, "garage61-agent", "garage61-launcher"),
                App("Simagic SimPro", new[] { "simpro3", "simdaemon" }, null, new[] { "^Simpro3?" }, "simpro3", "simdaemon"),
                App("Logitech G HUB", new[] { "lghub", "lghub_system_tray" }, null, new[] { "^Logitech G HUB$" }, "lghub"),
                App("iOverlay", new[] { "iOverlay" }, null, new[] { "^iOverlay" }, "iOverlay"),
                App("Discord", null, new[] { @"%RM_LAUNCHTEST%\Local\Discord\Discord.exe" }, new[] { "^Discord$" }, "Discord") };
            Func<IEnumerable<InstalledEntry>> reader = () => entries;

            var found = LaunchScan.Find(apps, menus, reader, DataDirs.ShortcutTarget, TimeSpan.FromSeconds(20));
            Console.WriteLine("  found: " + Show(found));
            Func<string, LaunchItem> get = n => found.FirstOrDefault(i => i.Name == n);
            Check("shortcut: SimHub found from its Start menu shortcut", get("SimHub") != null && get("SimHub").Path == simhub && get("SimHub").ProcessName == "SimHubWPF", Show(found));
            Check("shortcut: an uninstaller shortcut is not offered", !found.Any(i => i.Path == unins), null);
            Check("shortcut: the Start menu target wins over Settings > Apps (G HUB tray app)", get("Logitech G HUB") != null && get("Logitech G HUB").Path == tray && get("Logitech G HUB").ProcessName == "lghub_system_tray", Show(found));
            Check("shortcut: several shortcuts for one app give one item, the preferred exe", found.Count(i => i.Name == "Garage 61") == 1 && get("Garage 61").Path == g61launcher, Show(found));
            Check("shortcut: a shortcut to a missing exe is not offered", get("iOverlay") == null, null);
            Check("registry: Crew Chief found from its icon (quotes and ,0 stripped)", get("Crew Chief") != null && get("Crew Chief").Path == crew, Show(found));
            Check("registry: SimPro found in its install folder, one folder down", get("Simagic SimPro") != null && get("Simagic SimPro").Path == simpro, Show(found));
            Check("paths: Trading Paints found at its default path", get("Trading Paints") != null && get("Trading Paints").Path == paints, Show(found));
            Check("apps without a launch list (Discord) are never offered", get("Discord") == null, null);
            Check("items are ticked, in catalog order, one per app", found.All(i => i.Enabled) && found.Select(i => i.Name).SequenceEqual(apps.Where(a => found.Any(i => i.Name == a.label)).Select(a => a.label)) && found.Select(i => i.Name).Distinct().Count() == found.Count, Show(found));
            Check("every item points at an exe that exists", found.All(i => File.Exists(i.Path) && i.Installed), null);

            // ---- bounds and failures
            var none = LaunchScan.Find(apps, menus, reader, DataDirs.ShortcutTarget, TimeSpan.Zero);
            Check("no time left: shortcuts and Settings > Apps are skipped, default paths still checked", none.Count == 1 && none[0].Name == "Trading Paints", Show(none));
            var throwing = LaunchScan.Find(apps, menus, () => { throw new InvalidOperationException("registry"); }, l => { throw new IOException("lnk"); }, TimeSpan.FromSeconds(5));
            Check("a failing reader or shortcut doesn't crash the scan", throwing.Count == 1 && throwing[0].Name == "Trading Paints", Show(throwing));
            var nulls = LaunchScan.Find(null, null, null, null, TimeSpan.FromSeconds(1));
            Check("no catalog, folders or readers: nothing found, no crash", nulls.Count == 0, null);
            var deepOnly = LaunchScan.Find(apps.Take(1), new[] { Path.Combine(Root, @"User\Programs\a") }, null, DataDirs.ShortcutTarget, TimeSpan.FromSeconds(5));
            Check("folders deeper than the limit are not walked", deepOnly.Count == 0, Show(deepOnly));
            Check("the scan has caps on files, depth and time", LaunchScan.MaxShortcuts > 0 && LaunchScan.MaxShortcuts <= 5000 && LaunchScan.MaxDepth <= 8 && LaunchScan.Budget <= TimeSpan.FromSeconds(10), null);

            // ---- suggestions leave out what's listed
            var current = new List<LaunchItem> {
                new LaunchItem { Name = "My SimHub", Path = @"D:\Other\SimHubWPF.exe", ProcessName = "SimHubWPF", Enabled = false },
                new LaunchItem { Name = "SimPro Manager", Path = @"C:\x\simdaemon.exe", ProcessName = "", Enabled = true },
                new LaunchItem { Name = "Paints", Path = paints, ProcessName = "", Enabled = true } };
            var offer = LaunchScan.NotListed(apps, found, current);
            Console.WriteLine("  offered: " + Show(offer));
            Check("suggest: an app already listed (same process) is not offered", !offer.Any(i => i.Name == "SimHub"), Show(offer));
            Check("suggest: an app listed under another of its exes is not offered", !offer.Any(i => i.Name == "Simagic SimPro"), Show(offer));
            Check("suggest: an app listed by the same path is not offered", !offer.Any(i => i.Name == "Trading Paints"), Show(offer));
            Check("suggest: the rest are offered", offer.Any(i => i.Name == "Crew Chief") && offer.Any(i => i.Name == "Logitech G HUB") && offer.Any(i => i.Name == "Garage 61"), Show(offer));
            Check("suggest: an empty list offers everything found", LaunchScan.NotListed(apps, found, null).Count == found.Count, null);
            Check("suggest: a listed item maps to its catalog app", LaunchScan.AppOf(apps, current[1]) == apps[4] && LaunchScan.AppOf(apps, new LaunchItem { Path = @"C:\a\unknown.exe" }) == null, null);

            // ---- the defaults use the detection, with the fixed paths as fallback
            var detected = new List<LaunchItem> {
                new LaunchItem { Name = "SimHub", Path = simhub, ProcessName = "SimHubWPF", Enabled = true },
                new LaunchItem { Name = "Gone", Path = ghost, ProcessName = "iOverlay", Enabled = true } };
            var known = Config.KnownRaceApps(detected);
            Console.WriteLine("  defaults: " + Show(known));
            Check("defaults: detected apps come first", known.Count >= 1 && known[0].Path == simhub, Show(known));
            Check("defaults: a detected exe that's gone is dropped", !known.Any(i => i.Path == ghost), Show(known));
            Check("defaults: a fixed path never adds a second item for a detected app", known.Count(i => string.Equals(i.ProcessName, "SimHubWPF", StringComparison.OrdinalIgnoreCase)) == 1, Show(known));
            Check("defaults: every item exists", known.All(i => File.Exists(i.Path)), Show(known));
            var cfg = Config.Defaults(detected);
            Check("defaults: a fresh config lists them ticked", cfg.Launch.Count == known.Count && cfg.Launch.All(i => i.Enabled) && cfg.SetupDone == false, Show(cfg.Launch));
            var fallback = Config.KnownRaceApps(null);
            Check("defaults: with nothing detected, only fixed paths that exist", fallback.All(i => File.Exists(i.Path)), Show(fallback));

            // ---- red-team fix: only apps in trusted folders are ticked for you on a fresh install
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var trustedItem = new LaunchItem { Name = "SimHub", Path = Path.Combine(pf, @"SimHub\SimHubWPF.exe"), ProcessName = "SimHubWPF", Enabled = true };
            var tempItem = new LaunchItem { Name = "SimHub", Path = Path.Combine(Path.GetTempPath(), @"x\SimHubWPF.exe"), ProcessName = "SimHubWPF", Enabled = true };
            Check("auto-tick: an app in Program Files can be ticked for you", LaunchScan.AutoTick(real, trustedItem), trustedItem.Path);
            Check("auto-tick: the same exe name in a temp folder is only offered", !LaunchScan.AutoTick(real, tempItem), tempItem.Path);

            // ---- a real, read-only scan of this PC (informational: what it finds depends on the PC, so it isn't printed)
            var mine = LaunchScan.Find();
            Console.WriteLine("  this PC: " + mine.Count + " race app(s) found");
            Check("real scan: only existing exes of launchable catalog apps", mine.All(i => File.Exists(i.Path) && launchable.Any(a => a.label == i.Name)), mine.Count + " found");
        }
        catch (Exception ex) { Check("no crash", false, ex.ToString()); }
        finally { try { Directory.Delete(Root, true); } catch { } Environment.SetEnvironmentVariable("RM_LAUNCHTEST", null); }
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
