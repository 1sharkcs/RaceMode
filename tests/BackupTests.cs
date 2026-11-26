// Tests for Race Mode's backups of iRacing's ini (IniFile.cs): a clock that was wrong, lookalike file names, quick
// writes in a row. Every file is a temp copy of the public fixture ini; nothing real is opened. Few writes per file on
// purpose (an antivirus reads a burst of rewrites and deletes as ransomware).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RaceMode;

class BackupTests
{
    static int pass, fail;
    static void Check(string n, bool ok, string d) { if (ok) pass++; else fail++; Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (ok || d == null ? "" : "  -> " + d)); }

    static string Root, Fixture;
    static string Copy(string dir, string name)
    {
        var d = Path.Combine(Root, dir); Directory.CreateDirectory(d);
        var p = Path.Combine(d, name); File.Copy(Fixture, p); return p;
    }
    static Dictionary<string, Dictionary<string, string>> Width(int w)
    {
        return new Dictionary<string, Dictionary<string, string>> { { "Display", new Dictionary<string, string> { { "windowedWidth", w.ToString() } } } };
    }
    static string Names(IEnumerable<string> paths) { return string.Join(" | ", paths.Select(Path.GetFileName).ToArray()); }
    static string Stamp(DateTime t) { return t.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture); }
    static bool Same(string a, string b) { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }

    static int Main()
    {
        Fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"fixtures\live-before-race-preset.ini");
        Root = Path.Combine(Path.GetTempPath(), "racemode-backup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Root);
        try
        {
            ClockWasWrong();
            Lookalikes();
            QuickWrites();
        }
        catch (Exception ex) { Check("no crash", false, ex.ToString()); }
        finally { try { Directory.Delete(Root, true); } catch { } }
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    // Backups named while the clock was wrong (a flat CMOS battery said 2099, a wrong time zone, the hour after the
    // clocks go back) must never push out the backup Race Mode has just made.
    static void ClockWasWrong()
    {
        var ini = Copy("clock", "rendererDX11Monitor.ini");
        var future = new List<string>();
        for (int i = 1; i <= 5; i++) { var f = ini + ".racemode-20990101-00000" + i + "-000.bak"; File.WriteAllText(f, "from 2099 " + i); future.Add(f); }
        var r = IniFile.Patch(ini, Width(2560), true);
        var timed = IniFile.Backups(ini);
        Check("01 five backups from a clock set to 2099: the one just made is kept", r.Written && r.Backup != null && File.Exists(r.Backup), r.Backup);
        Check("02 ...and listed first (newest), five timed ones kept", timed.Count == 5 && Same(timed[0], r.Backup), Names(timed));
        Check("03 ...the 2099 one pruned is the earliest of them, the rest stay in their own order",
            !File.Exists(future[0]) && future.Skip(1).All(File.Exists) && Names(timed.Skip(1)) == Names(future.Skip(1).Reverse()), Names(timed));
        var r2 = IniFile.Patch(ini, Width(2561), true);
        timed = IniFile.Backups(ini);
        Check("04 a second write: both real backups kept, newest first", File.Exists(r.Backup) && File.Exists(r2.Backup) && timed.Count == 5
            && Same(timed[0], r2.Backup) && Same(timed[1], r.Backup), Names(timed));

        // the order itself, with a fixed "now"
        var b = "x.ini.racemode-";
        var now = new DateTime(2026, 10, 25, 1, 11, 0);
        var early = b + "20261025-013000-000.bak";   // made at 01:30 summer time
        var late = b + "20261025-011000-000.bak";    // made after the clocks went back, at 01:10
        Check("05 clocks went back: the backup made at 01:10 (now 01:11) is newer than the 01:30 one", IniFile.NewestFirst(new[] { early, late }, now).SequenceEqual(new[] { late, early }), null);
        Check("06 once 01:30 has passed again the names are trusted again", IniFile.NewestFirst(new[] { late, early }, now.AddHours(1)).SequenceEqual(new[] { early, late }), null);
        string at = b + Stamp(now.AddMinutes(1)) + ".bak", over = b + Stamp(now.AddMinutes(1).AddMilliseconds(1)) + ".bak", older = b + Stamp(now.AddMinutes(-5)) + ".bak";
        Check("07 up to a minute ahead is still trusted (clock drift); more is from a wrong clock",
            IniFile.NewestFirst(new[] { older, at }, now).SequenceEqual(new[] { at, older }) && IniFile.NewestFirst(new[] { over, older }, now).SequenceEqual(new[] { older, over }), null);
        Check("08 a clock that is now far behind (2001): today's backups made then still order among themselves",
            IniFile.NewestFirst(new[] { b + "20261123-010000-000.bak", b + "20261123-020000-000.bak" }, new DateTime(2001, 1, 1)).SequenceEqual(new[] { b + "20261123-020000-000.bak", b + "20261123-010000-000.bak" }), null);
        Check("08a the same millisecond: the numbered one (-2) is the newer", IniFile.NewestFirst(new[] { b + "20261123-010000-000.bak", b + "20261123-010000-000-2.bak" }, now.AddYears(1)).SequenceEqual(new[] { b + "20261123-010000-000-2.bak", b + "20261123-010000-000.bak" }), null);
        Check("09 odd input: null, empty, spaces, names that aren't backups: left out, no throw",
            IniFile.NewestFirst(null, now).Count == 0 && IniFile.NewestFirst(new[] { null, "", "x.ini", b + "notes.bak", b + "20261399-010000-000.bak", "  " }, now).Count == 0, null);
        string oddErr = null;
        try { IniFile.BackupTime("x|y.ini.racemode-20261123-011408-123.bak"); IniFile.NewestFirst(new[] { "a|b" + b + "20261123-010000-000.bak" }, now); }
        catch (Exception ex) { oddErr = ex.GetType().Name + ": " + ex.Message; }
        Check("10 odd input: names with characters Windows doesn't allow never throw", oddErr == null, oddErr);
    }

    // Names the folder search finds that aren't Race Mode's backups are never listed or pruned.
    static void Lookalikes()
    {
        var ini = Copy("lookalike", "rendererDX11Monitor.ini");
        var fake = new[]
        {
            ini + ".racemode-notes.bak",
            ini + ".racemode-20261123-011408-123.bak1",                      // Windows' 3-letter-extension match finds this one
            ini + ".racemode-notes.racemode-20261123-011408-123.bak",        // a backup-looking tail after other text
            ini + ".racemode-20261399-011408-123.bak",                       // no such date
            ini + ".racemode-20261123-011408-123-undo.bak",                  // a tail Race Mode never writes
            ini + ".racemode- 20261123-011408-123.bak",                      // a space
        };
        foreach (var f in fake) File.WriteAllText(f, "the user's own file");
        Check("11 lookalike names next to the file: none is listed", IniFile.Backups(ini).Count == 0, Names(IniFile.Backups(ini)));
        for (int i = 1; i <= 5; i++) File.WriteAllText(ini + ".racemode-20200101-00000" + i + "-000.bak", "older " + i);
        var r = IniFile.Patch(ini, Width(2560), true);
        var listed = IniFile.Backups(ini);
        Check("12 a write with five real backups there prunes a real one, never a lookalike", fake.All(File.Exists) && File.Exists(r.Backup)
            && !File.Exists(ini + ".racemode-20200101-000001-000.bak") && File.Exists(ini + ".racemode-20200101-000002-000.bak"), Names(Directory.GetFiles(Path.GetDirectoryName(ini))));
        Check("13 ...the list is the five real timed backups; the very first backup is kept beside them", listed.Count == 5 && Same(listed[0], r.Backup) && File.Exists(ini + ".racemode.first.bak"), Names(listed));
        Check("14 no such file or folder: an empty list, no throw", IniFile.Backups(Path.Combine(Root, "none", "x.ini")).Count == 0, null);
    }

    // A write that changes nothing; quick writes in a row; a backup made by hand (IniFile.Backup).
    static void QuickWrites()
    {
        var ini = Copy("quick", "rendererDX11Monitor.ini");
        var dir = Path.GetDirectoryName(ini);
        var before = Directory.GetFiles(dir).Length;
        var none = IniFile.Patch(ini, Width(1920), true);   // the fixture already has 1920
        Check("15 a write that changes nothing makes no backup and no file", !none.Written && none.Backup == null && none.Changes.Count == 0 && Directory.GetFiles(dir).Length == before, Names(Directory.GetFiles(dir)));
        var all = new[] { IniFile.Patch(ini, Width(2560), true).Backup, IniFile.Patch(ini, Width(2561), true).Backup, IniFile.Patch(ini, Width(2562), true).Backup, IniFile.Patch(ini, Width(2563), true).Backup };
        Check("16 four quick writes in a row: four different backups, all kept, newest first", all.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4 && all.All(File.Exists)
            && IniFile.Backups(ini).Take(4).Select(Path.GetFileName).SequenceEqual(all.Reverse().Select(Path.GetFileName)), Names(IniFile.Backups(ini)));
        var text = File.ReadAllText(ini);
        var made = IniFile.Backup(ini);
        Check("17 a backup made on its own: a copy of the file as it is, listed first, the file untouched", File.ReadAllText(made) == text && File.ReadAllText(ini) == text && Same(IniFile.Backups(ini)[0], made), made);
        bool threw = false;
        try { IniFile.Backup(Path.Combine(dir, "missing.ini")); } catch { threw = true; }
        Check("18 no file to copy: the error reaches the caller (no write goes ahead without a backup)", threw, null);
    }
}
