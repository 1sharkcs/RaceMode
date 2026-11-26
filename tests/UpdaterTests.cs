// Tests for Updater.cs version handling and release parsing. No network: releases are built in memory.
// Build next to RaceMode.exe: csc -r:RaceMode.exe -r:System.Web.Extensions.dll UpdaterTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using RaceMode;

class UpdaterTests
{
    static int pass, fail;
    static void Check(string n, bool ok, string d) { if (ok) pass++; else fail++; Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (ok || d == null ? "" : "  -> " + d)); }

    static Dictionary<string, object> Asset(string name, string url) { return new Dictionary<string, object> { { "name", name }, { "browser_download_url", url } }; }
    static Dictionary<string, object> Rel(string tag, bool pre, params Dictionary<string, object>[] assets)
    {
        return new Dictionary<string, object> { { "tag_name", tag }, { "prerelease", pre }, { "draft", false }, { "html_url", "x" }, { "assets", (object[])assets } };
    }

    static void Main()
    {
        Check("01 parse stable", Updater.Parse("1.2.3") != null && Updater.Parse("v1.2.3")[2] == 3, null);
        Check("02 parse release candidate", Updater.Parse("1.2.3-rc.4") != null && Updater.Parse("1.2.3-rc.4")[3] == 4 && Updater.Parse("1.2.3-dev.4") == null, null);
        Check("03 reject junk", Updater.Parse("local") == null && Updater.Parse("1.2") == null && Updater.Parse("1.2.3-beta.1") == null && Updater.Parse("") == null && Updater.Parse(null) == null, null);
        Check("04 stable > its release candidates", Updater.Compare("1.2.3", "1.2.3-rc.9") > 0, null);
        Check("05 rc.10 > rc.9 (numeric)", Updater.Compare("1.2.3-rc.10", "1.2.3-rc.9") > 0, null);
        Check("06 1.10.0 > 1.9.0 (numeric)", Updater.Compare("1.10.0", "1.9.0") > 0, null);
        Check("07 next release candidate > previous stable", Updater.Compare("1.3.0-rc.1", "1.2.3") > 0, null);
        Check("08 equal", Updater.Compare("v2.0.0", "2.0.0") == 0, null);
        Check("09 local build never newer", Updater.Compare("local", "0.0.1") < 0 && Updater.Compare("9.9.9", "local") > 0, null);
        Check("10 local build has updates off, release build on", Updater.Current == "local" ? !Updater.Enabled : Updater.Enabled, Updater.Current);

        var ok = "https://github.com/" + Updater.Repo + "/releases/download/v1.0.0/";
        var u = Updater.FromRelease(Rel("v1.0.0", false, Asset("RaceMode.zip", ok + "RaceMode.zip"), Asset("RaceMode.zip.sha256", ok + "RaceMode.zip.sha256")));
        Check("11 release with zip + checksum", u != null && u.Version == "1.0.0" && !u.Prerelease, null);
        u = Updater.FromRelease(Rel("v1.0.0", false, Asset("RaceMode.zip", ok + "RaceMode.zip")));
        Check("12 no checksum file -> not offered", u == null, null);
        u = Updater.FromRelease(Rel("v1.0.0", false, Asset("RaceMode.zip", "https://evil.example/RaceMode.zip"), Asset("RaceMode.zip.sha256", ok + "RaceMode.zip.sha256")));
        Check("13 zip hosted elsewhere -> not offered", u == null, null);
        u = Updater.FromRelease(Rel("nightly", false, Asset("RaceMode.zip", ok + "RaceMode.zip"), Asset("RaceMode.zip.sha256", ok + "RaceMode.zip.sha256")));
        Check("14 bad tag -> not offered", u == null, null);
        Check("15 no rollback without a fresh update", !Updater.RollBackIfFreshUpdate() && !Updater.ConfirmSwap(), null);

        // 16-20: the exe an update replaced is kept one version back (temp folder only)
        string root = Path.Combine(Path.GetTempPath(), "racemode-updater-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            var exe = Path.Combine(root, "RaceMode.exe");
            File.WriteAllText(exe, "new"); File.WriteAllText(exe + ".old", "one back");
            Check("16 the confirmed update keeps the old exe as .previous", Updater.KeepPrevious(exe) && !File.Exists(exe + ".old") && File.ReadAllText(Updater.PreviousExe(exe)) == "one back" && File.ReadAllText(exe) == "new", null);
            File.WriteAllText(exe + ".old", "newer one back");
            Check("17 the next update replaces .previous (one version back, never a pile)", Updater.KeepPrevious(exe) && File.ReadAllText(Updater.PreviousExe(exe)) == "newer one back" && Directory.GetFiles(root, "RaceMode.exe*").Length == 2, null);
            Check("18 no .old: nothing to keep, .previous untouched", !Updater.KeepPrevious(exe) && File.ReadAllText(Updater.PreviousExe(exe)) == "newer one back", null);
            File.WriteAllText(exe + ".old", "third");
            File.SetAttributes(Updater.PreviousExe(exe), FileAttributes.ReadOnly);
            Check("19 a read-only .previous is still replaced", Updater.KeepPrevious(exe) && File.ReadAllText(Updater.PreviousExe(exe)) == "third", null);
            File.WriteAllText(exe + ".old", "fourth");
            using (var lockIt = new FileStream(Updater.PreviousExe(exe), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Check("20 .previous can't be replaced: .old still goes, so the next start isn't taken for a fresh update", !Updater.KeepPrevious(exe) && !File.Exists(exe + ".old"), null);
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
