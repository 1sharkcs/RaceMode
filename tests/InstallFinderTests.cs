// Tests for RaceMode.InstallFinder. The registry is a fake (in-memory) and every exe is an empty file in a temp
// folder, so nothing on this PC is changed. (The default Program Files paths are still checked for existence,
// read-only, so a few results depend on what's installed; those checks accept either outcome.)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RaceMode;

class InstallFinderTests
{
    static int pass, fail;
    static string Root;
    // key ("HKLM\SOFTWARE\...") -> value name -> value
    static Dictionary<string, Dictionary<string, string>> Reg = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    const string U = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    const string UW = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\";
    const string UC = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    const string AP = @"\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";

    static void Check(string name, bool ok, string detail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok || detail == null ? "" : "  -> " + detail));
    }

    static void Set(string key, string name, string value)
    {
        Dictionary<string, string> vals;
        if (!Reg.TryGetValue(key, out vals)) Reg[key] = vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        vals[name] = value;
    }
    static string Read(string key, string name)
    {
        Dictionary<string, string> vals; string v;
        return Reg.TryGetValue(key, out vals) && vals.TryGetValue(name, out v) ? v : null;
    }
    static string[] Subs(string key)
    {
        var pre = key.TrimEnd('\\') + @"\";
        return Reg.Keys.Where(k => k.StartsWith(pre, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Substring(pre.Length).Split('\\')[0]).Distinct().ToArray();
    }
    static void Reset() { Reg.Clear(); InstallFinder.ClearCache(); }

    static string Exe(string rel)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        File.WriteAllText(p, "");
        return p;
    }
    static bool Same(string a, string b) { return a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }

    static int Main()
    {
        Root = Path.Combine(Path.GetTempPath(), "RaceModeInstallFinder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        InstallFinder.Reader = Read;
        InstallFinder.SubKeys = Subs;
        try
        {
            var iracing = Exe(@"Games\iRacing\ui\iRacingUI.exe");
            var obs = Exe(@"Apps\OBS\bin\64bit\obs64.exe");
            var obsDefault = Exe(@"Default\obs-studio\bin\64bit\obs64.exe");
            var hw = Exe(@"Tools\HWiNFO64\HWiNFO64.EXE");
            var steam = Exe(@"Games\Steam\steam.exe");
            var missing = Path.Combine(Root, @"Nowhere\obs64.exe");

            // ---- Uninstall: InstallLocation
            Reset();
            Set(U + "{ABC-iRacing}", "DisplayName", "iRacing.com Racing Simulation");
            Set(U + "{ABC-iRacing}", "InstallLocation", Path.Combine(Root, @"Games\iRacing\"));
            Check("iRacing found via HKLM Uninstall InstallLocation", Same(InstallFinder.Find("iRacingUI.exe", "iRacing", new[] { @"ui\iRacingUI.exe" }, null), iracing), null);
            Check("IRacingUI() uses the registry", Same(InstallFinder.IRacingUI(), iracing), InstallFinder.IRacingUI());
            InstallFinder.ClearCache();
            Check("Config default IRacingUI uses the registry", Same(new Config().IRacingUI, iracing), new Config().IRacingUI);

            Reset();
            Set(U + "SomethingElse", "DisplayName", "Other Racing Tool");
            Set(U + "SomethingElse", "InstallLocation", Path.Combine(Root, @"Games\iRacing"));
            Check("Uninstall entry with another DisplayName is skipped", InstallFinder.Find("iRacingUI.exe", "iRacing", new[] { @"ui\iRacingUI.exe" }, null) == null, null);
            Check("IRacingUI() falls back to the default path", InstallFinder.IRacingUI() == InstallFinder.DefaultIRacingUI, InstallFinder.IRacingUI());

            // ---- Uninstall: DisplayIcon (quoted, with icon index) in WOW6432Node
            Reset();
            Set(UW + "HWiNFO64_is1", "DisplayName", "HWiNFO64 Version 8.10");
            Set(UW + "HWiNFO64_is1", "DisplayIcon", "\"" + hw + "\",0");
            Check("HWiNFO found via WOW6432Node DisplayIcon", Same(InstallFinder.Find("HWiNFO64.EXE", "HWiNFO", null, null), hw), null);
            Check("HwinfoExe() uses the registry", Same(InstallFinder.HwinfoExe(), hw), InstallFinder.HwinfoExe());

            Reset();
            Set(UW + "Other", "DisplayName", "HWiNFO extras");
            Set(UW + "Other", "DisplayIcon", obs);
            Check("DisplayIcon for a different exe is ignored", InstallFinder.Find("HWiNFO64.EXE", "HWiNFO", null, null) == null, null);

            // ---- Uninstall in HKCU, no DisplayName filter
            Reset();
            Set(UC + "OBS Studio", "InstallLocation", Path.Combine(Root, @"Apps\OBS"));
            Check("OBS found via HKCU Uninstall (relative bin path)", Same(InstallFinder.Find("obs64.exe", null, new[] { @"bin\64bit\obs64.exe" }, null), obs), null);

            // ---- App Paths
            Reset();
            Set("HKLM" + AP + "obs64.exe", "", obs);
            Check("OBS found via HKLM App Paths", Same(InstallFinder.Find("obs64.exe", "OBS Studio", null, null), obs), null);
            Reset();
            Set("HKCU" + AP + "obs64.exe", "", "\"" + obs + "\"");
            Check("OBS found via HKCU App Paths (quoted)", Same(InstallFinder.Find("obs64.exe", "OBS Studio", null, null), obs), null);

            // ---- Order and fallbacks
            Reset();
            Set(U + "OBS Studio", "DisplayName", "OBS Studio");
            Set(U + "OBS Studio", "DisplayIcon", obs);
            Set("HKLM" + AP + "obs64.exe", "", obsDefault);
            Check("Uninstall wins over App Paths", Same(InstallFinder.Find("obs64.exe", "OBS Studio", null, new[] { obsDefault }), obs), null);

            Reset();
            Set(U + "OBS Studio", "DisplayName", "OBS Studio");
            Set(U + "OBS Studio", "DisplayIcon", missing);
            Set("HKLM" + AP + "obs64.exe", "", missing);
            Check("registry paths that don't exist fall back to the default", Same(InstallFinder.Find("obs64.exe", "OBS Studio", null, new[] { missing, obsDefault }), obsDefault), null);
            Check("nothing installed -> null", InstallFinder.Find("obs64.exe", "OBS Studio", null, new[] { missing }) == null, null);

            Reset();
            InstallFinder.Reader = (k, n) => { throw new UnauthorizedAccessException("denied"); };
            InstallFinder.SubKeys = k => { throw new UnauthorizedAccessException("denied"); };
            string got = null; bool threw = false;
            try { got = InstallFinder.Find("obs64.exe", "OBS Studio", null, new[] { obsDefault }); } catch { threw = true; }
            Check("registry errors are ignored (default still found)", !threw && Same(got, obsDefault), got);
            InstallFinder.Reader = Read; InstallFinder.SubKeys = Subs;

            // ---- Steam
            Reset();
            Set(@"HKCU\Software\Valve\Steam", "SteamExe", steam.Replace('\\', '/').ToLowerInvariant());
            Check("Steam found via HKCU SteamExe (forward slashes)", Same(InstallFinder.SteamExe(), steam), InstallFinder.SteamExe());
            Reset();
            Set(@"HKCU\Software\Valve\Steam", "SteamExe", missing);
            Check("Steam falls back to the default path", InstallFinder.SteamExe() == InstallFinder.DefaultSteam || File.Exists(InstallFinder.SteamExe()), InstallFinder.SteamExe());

            // ---- Red-team fixes
            // HWiNFO is started through an admin prompt: a user-writable (HKCU) entry must never be used for it.
            Reset();
            var planted = Exe(@"user\HWiNFO64.EXE");
            Set(UC + "HWiNFO64_is1", "DisplayName", "HWiNFO64");
            Set(UC + "HWiNFO64_is1", "DisplayIcon", planted);
            Check("HWiNFO: a per-user registry entry is ignored", !Same(InstallFinder.HwinfoExe(), planted), InstallFinder.HwinfoExe());
            // Other apps: the machine-wide entry wins over a per-user one.
            Reset();
            var obsMachine = Exe(@"machine\obs-studio\bin\64bit\obs64.exe");
            Exe(@"user\obs-studio\bin\64bit\obs64.exe");
            Set(UC + "OBS Studio", "DisplayName", "OBS Studio");
            Set(UC + "OBS Studio", "InstallLocation", Path.Combine(Root, @"user\obs-studio"));
            Set(U + "OBS Studio", "DisplayName", "OBS Studio");
            Set(U + "OBS Studio", "InstallLocation", Path.Combine(Root, @"machine\obs-studio"));
            Check("machine-wide entry wins over a per-user one", Same(InstallFinder.ObsExe(), obsMachine), InstallFinder.ObsExe());
            // Steam's own key is only trusted when it points at steam.exe.
            Reset();
            var notSteam = Exe(@"user\evil.bat");
            Set(@"HKCU\Software\Valve\Steam", "SteamExe", notSteam.Replace('\\', '/'));
            Check("Steam: a SteamExe that isn't steam.exe is ignored", !Same(InstallFinder.SteamExe(), notSteam), InstallFinder.SteamExe());
            // Network paths are never touched (an unreachable share would freeze Race Mode and send a login hash).
            Check("network paths are recognised", InstallFinder.IsNetworkPath(@"\\host\share\x.exe") && InstallFinder.IsNetworkPath("//host/share/x.exe") && !InstallFinder.IsNetworkPath(@"C:\x.exe"), null);
            Check("a network path never counts as found", !InstallFinder.LocalFileExists(@"\\nohost.invalid\share\HWiNFO64.EXE"), null);
            Reset();
            Set(U + "OBS Studio", "DisplayName", "OBS Studio");
            Set(U + "OBS Studio", "InstallLocation", @"\\nohost.invalid\obs");
            var sw = System.Diagnostics.Stopwatch.StartNew(); var netObs = InstallFinder.ObsExe();
            Check("a network InstallLocation is skipped without waiting on it", (netObs == null || !InstallFinder.IsNetworkPath(netObs)) && sw.ElapsedMilliseconds < 5000, sw.ElapsedMilliseconds + " ms, " + netObs);

            // ---- Cache: a second lookup soon after doesn't re-read the registry (answers are kept 10 minutes)
            Reset();
            Set(UW + "HWiNFO64_is1", "DisplayName", "HWiNFO64");
            Set(UW + "HWiNFO64_is1", "DisplayIcon", hw);
            var first = InstallFinder.HwinfoExe();
            Reg.Clear();
            Check("lookups are cached briefly", Same(InstallFinder.HwinfoExe(), first) && Same(first, hw), InstallFinder.HwinfoExe());

            // ---- CleanPath
            Check("CleanPath strips quotes and icon index", InstallFinder.CleanPath("\"C:\\A B\\x.exe\",0") == @"C:\A B\x.exe", InstallFinder.CleanPath("\"C:\\A B\\x.exe\",0"));
            Check("CleanPath strips unquoted icon index", InstallFinder.CleanPath(@"C:\A\x.exe,1") == @"C:\A\x.exe", InstallFinder.CleanPath(@"C:\A\x.exe,1"));
            Check("CleanPath keeps commas in folder names", InstallFinder.CleanPath(@"C:\A,B\x.exe") == @"C:\A,B\x.exe", InstallFinder.CleanPath(@"C:\A,B\x.exe"));
            Check("CleanPath turns / into \\", InstallFinder.CleanPath("c:/a/steam.exe") == @"c:\a\steam.exe", null);
            Check("CleanPath empty -> null", InstallFinder.CleanPath("  ") == null && InstallFinder.CleanPath(null) == null, null);
        }
        catch (Exception ex) { Check("no crash", false, ex.ToString()); }
        finally { InstallFinder.ClearCache(); try { Directory.Delete(Root, true); } catch { } }
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
