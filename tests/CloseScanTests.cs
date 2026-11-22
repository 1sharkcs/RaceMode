// Apps-to-close tests: the built-in app list is valid and generic, matching is strict, and a real scan of this PC
// never suggests anything protected. Read-only: nothing is closed or written.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RaceMode;

static class CloseScanTests
{
    static int pass, fail;
    static void Check(string name, bool ok, string detail = null)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail != null && !ok ? "  [" + detail + "]" : ""));
    }

    static int Main()
    {
        var apps = AppScan.Apps;
        Check("01 app list loads from the exe, 150+ apps, no error", AppScan.LoadError == null && apps.Count >= 150, AppScan.LoadError ?? apps.Count.ToString());

        var cats = new[] { "close", "keep", "careful" };
        var bad = apps.Where(a => !cats.Contains(a.category) || string.IsNullOrWhiteSpace(a.why) || a.why.Length > 80).Select(a => a.label).ToList();
        Check("02 every app has a category and a short reason", bad.Count == 0, string.Join(", ", bad.Take(5)));

        bad = apps.Where(a => a.process.Any(p => string.IsNullOrWhiteSpace(p) || p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || p.IndexOfAny(new[] { '\\', '/', '*' }) >= 0)).Select(a => a.label).ToList();
        Check("03 process names are plain names (no .exe, paths or wildcards)", bad.Count == 0, string.Join(", ", bad.Take(5)));

        var shared = new[] { "msedgewebview2", "node", "python", "java", "javaw", "svchost", "conhost", "dllhost", "RuntimeBroker", "explorer", "cmd", "powershell" };
        bad = apps.Where(a => a.process.Any(p => shared.Contains(p, StringComparer.OrdinalIgnoreCase))).Select(a => a.label).ToList();
        Check("04 no shared Windows or runtime names (they'd close other apps)", bad.Count == 0, string.Join(", ", bad));

        var dup = apps.SelectMany(a => a.process.Select(p => new { p = p.ToLowerInvariant(), a.label })).GroupBy(x => x.p).Where(g => g.Select(x => x.label).Distinct().Count() > 1).Select(g => g.Key).ToList();
        Check("05 each process name belongs to one app", dup.Count == 0, string.Join(", ", dup.Take(8)));

        bad = new List<string>();
        foreach (var a in apps)
            foreach (var rx in a.installed)
                try { new Regex(rx, RegexOptions.IgnoreCase); if (Regex.IsMatch("", rx) || Regex.IsMatch("Microsoft Visual C++ 2015-2022 Redistributable (x64)", rx, RegexOptions.IgnoreCase) && !a.label.Contains("Visual")) bad.Add(a.label + ": " + rx); }
                catch { bad.Add(a.label + ": " + rx); }
        Check("06 Settings > Apps patterns are valid and not catch-alls", bad.Count == 0, string.Join(", ", bad.Take(5)));

        bad = apps.Where(a => a.paths.Any(p => !p.StartsWith("%") || p.IndexOf(@"\Users\", StringComparison.OrdinalIgnoreCase) >= 0 || !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))).Select(a => a.label).ToList();
        Check("07 install paths use %folders% only and point at an exe (nothing personal)", bad.Count == 0, string.Join(", ", bad.Take(5)));

        Check("08 iRacing, Race Mode and Windows aren't in the list as closable",
            !apps.Any(a => a.Closable && a.process.Any(p => new[] { "iRacingUI", "iRacingSim64DX11", "RaceMode", "dwm", "audiodg" }.Contains(p, StringComparer.OrdinalIgnoreCase))));
        Check("09 racing helpers are never 'close'",
            new[] { "Discord", "SimHubWPF", "CrewChiefV4", "obs64", "HWiNFO64" }.All(n => apps.Any(a => a.process.Contains(n, StringComparer.OrdinalIgnoreCase) && !a.Closable)));
        var obs = apps.FirstOrDefault(a => a.process.Contains("obs64", StringComparer.OrdinalIgnoreCase));
        Check("09b OBS: kept while streaming, closable when not", obs != null && obs.streaming && !obs.ClosableWhen(true) && obs.ClosableWhen(false));
        Check("10 friendly names: chrome -> Google Chrome, unknown -> null", AppScan.LabelFor("chrome") == "Google Chrome" && AppScan.LabelFor("no-such-app-xyz") == null, AppScan.LabelFor("chrome"));

        // strict matching: a catalog name only counts from the app's own folder (or if the file says it's that product)
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var chrome = new KnownApp { label = "Google Chrome", category = "close", why = "x", process = new[] { "chrome" }, paths = new[] { @"%ProgramFiles%\Google\Chrome\Application\chrome.exe" }, installed = new string[0] };
        Check("11 matches the app in its own folder", AppScan.Matches(chrome, pf + @"\Google\Chrome\Application\chrome.exe"));
        Check("12 same name in another folder isn't that app", !AppScan.Matches(chrome, @"C:\Tools\SomethingElse\chrome.exe"));
        Check("13 app folders: Google\\Chrome, Spotify", AppScan.AppRoot(pf + @"\Google\Chrome\Application\chrome.exe").EndsWith(@"\Google\Chrome", StringComparison.OrdinalIgnoreCase)
            && AppScan.AppRoot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + @"\Spotify\Spotify.exe").EndsWith(@"\Roaming\Spotify", StringComparison.OrdinalIgnoreCase));

        // a real scan of this PC (read-only)
        var protectedNames = new[] { "explorer", "RaceMode", "iRacingUI", "Discord" };
        var t0 = DateTime.Now;
        var found = AppScan.Scan(TimeSpan.FromSeconds(1), n => protectedNames.Contains(n, StringComparer.OrdinalIgnoreCase), new[] { "steam" });
        var secs = (DateTime.Now - t0).TotalSeconds;
        Check("14 scan finishes in under 15 s", secs < 15, secs.ToString("0.0"));
        Check("15 nothing protected, already listed or shared is suggested",
            !found.Any(s => protectedNames.Contains(s.Process, StringComparer.OrdinalIgnoreCase) || string.Equals(s.Process, "steam", StringComparison.OrdinalIgnoreCase) || shared.Contains(s.Process, StringComparer.OrdinalIgnoreCase)),
            string.Join(", ", found.Select(s => s.Process)));
        Check("16 every suggestion has a name, a Close list name, and open ones come first",
            found.All(s => !string.IsNullOrEmpty(s.Label) && !string.IsNullOrEmpty(s.Process)) && !found.SkipWhile(s => s.Open).Any(s => s.Open));
        Check("17 known keep/careful apps are never suggested", !found.Any(s => s.Known && apps.Any(a => !a.Closable && a.process.Contains(s.Process, StringComparer.OrdinalIgnoreCase))));
        Check("18 one row per Close list name", found.Select(s => s.Process.ToLowerInvariant()).Distinct().Count() == found.Count);
        Console.WriteLine("  scan found: " + string.Join("; ", found.Select(s => s.Label + (s.Open ? " (open, " + (int)s.RamMB + " MB)" : " (installed)"))));

        Check("19 every suggestion's Close list names include its main process", found.All(x => x.Names != null && x.Names.Contains(x.Process, StringComparer.OrdinalIgnoreCase)));
        var box = new KnownApp { label = "Box", category = "close", why = "x", process = new[] { "Box" }, paths = new[] { @"%ProgramFiles%\Box\Box.exe" }, installed = new string[0] };
        Check("20 folder match is whole names only (Box isn't Boxcryptor)", AppScan.Matches(box, pf + @"\Box\Box.exe") && !AppScan.Matches(box, pf + @"\Boxcryptor\Box.exe"));
        Check("21 names not in the list close as before; unknown path is allowed", AppScan.BelongsTo("no-such-app-xyz", @"C:\x\y.exe") && AppScan.BelongsTo("chrome", null));
        Check("22 chrome from another folder doesn't count as Chrome when closing", !AppScan.BelongsTo("chrome", @"C:\Tools\SomethingElse\chrome.exe"));
        var bc = apps.FirstOrDefault(a => a.label == "NVIDIA Broadcast");
        Check("23 NVIDIA Broadcast is kept for everyone (mic noise removal)", bc != null && !bc.ClosableWhen(false) && !bc.ClosableWhen(true));
        var setupScan = AppScan.Scan(TimeSpan.FromSeconds(1), n => protectedNames.Contains(n, StringComparer.OrdinalIgnoreCase), new string[0], true);
        Check("24 with Streaming on, no streaming tool is suggested", !setupScan.Any(x => apps.Any(a => a.streaming && a.process.Contains(x.Process, StringComparer.OrdinalIgnoreCase))));
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
