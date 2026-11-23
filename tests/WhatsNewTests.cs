// Tests for when What's new opens (WhatsNew.cs). No window: only the rules.
using System;
using RaceMode;

class WhatsNewTests
{
    static int pass, fail;
    static void Check(string n, bool ok, string d) { if (ok) pass++; else fail++; Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (ok || d == null ? "" : "  -> " + d)); }

    static void Main()
    {
        Check("01 series of a version", Shell.VersionSeries("1.1.0") == "1.1" && Shell.VersionSeries("v1.1.0-rc.3") == "1.1" && Shell.VersionSeries("2.10.4") == "2.10", null);
        Check("02 no series for a local build", Shell.VersionSeries("local") == null && Shell.VersionSeries("") == null && Shell.VersionSeries(null) == null, null);
        Check("03 updating from 1.0.1 (no version saved yet) shows it", Shell.ShouldShowWhatsNew("1.1.0", null, false), null);
        Check("04 every update shows it, test versions too", Shell.ShouldShowWhatsNew("1.1.0-rc.6", "1.1.0-rc.5", false) && Shell.ShouldShowWhatsNew("1.1.0", "1.1.0-rc.6", false), null);
        Check("05 an ordinary restart of the same version doesn't", !Shell.ShouldShowWhatsNew("1.1.0", "1.1.0", false), null);
        Check("06 never after first-run setup", !Shell.ShouldShowWhatsNew("1.1.0", null, true), null);
        Check("07 going back to an older version shows nothing", !Shell.ShouldShowWhatsNew("1.1.0-rc.5", "1.1.0", false), null);
        Check("08 never for local builds", !Shell.ShouldShowWhatsNew("local", null, false), null);
        Check("09 nothing for a series without pages", !Shell.ShouldShowWhatsNew("1.0.2", null, false) && !Shell.ShouldShowWhatsNew("9.4.0", "9.3.0", false), null);
        Check("10 numbers, not text: 1.1.10 is after 1.1.9", Shell.ShouldShowWhatsNew("1.1.10", "1.1.9", false), null);
        Check("11 an unreadable saved version counts as unknown, not newer", !Shell.ShouldShowWhatsNew("1.1.0", "garbage", false), null);
        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
