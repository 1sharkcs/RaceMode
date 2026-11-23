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
        Check("03 updating 1.0.1 to 1.1.0 shows it", Shell.ShouldShowWhatsNew("1.1.0", null, false), null);
        Check("04 a later 1.1 fix still counts as unseen until shown", Shell.ShouldShowWhatsNew("1.1.2", "1.0", false), null);
        Check("05 shown once: not again for the same series", !Shell.ShouldShowWhatsNew("1.1.0", "1.1", false) && !Shell.ShouldShowWhatsNew("1.1.3", "1.1", false), null);
        Check("06 never after first-run setup", !Shell.ShouldShowWhatsNew("1.1.0", null, true), null);
        Check("07 test versions show it too (once per series)", Shell.ShouldShowWhatsNew("1.1.0-rc.5", null, false) && !Shell.ShouldShowWhatsNew("1.1.0-rc.6", "1.1", false), null);
        Check("08 never for local builds", !Shell.ShouldShowWhatsNew("local", null, false), null);
        Check("09 nothing for a series without pages", !Shell.ShouldShowWhatsNew("1.0.2", null, false) && !Shell.ShouldShowWhatsNew("9.4.0", null, false), null);
        Check("10 a rollback below what was seen shows nothing", !Shell.ShouldShowWhatsNew("1.1.0", "1.2", false), null);
        Check("11 1.10 sorts after 1.9 (numbers, not text)", !Shell.ShouldShowWhatsNew("1.1.0", "1.10", false), null);
        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
