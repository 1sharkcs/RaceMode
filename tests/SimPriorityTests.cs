// Sim priority tests (SimPriority.cs): iRacing's anti-cheat refusing the sim's handle is tried once and never again,
// a success raises the sim, a sim already at High is noted once and never opened, and a new PID gets a fresh try.
// Pure: fake read and write delegates, no real process is opened.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using RaceMode;

static class SimPriorityTests
{
    static int pass, fail;
    static void Check(string name, bool ok, string detail = null)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail != null && !ok ? "  [" + detail + "]" : ""));
    }

    // A fake sim: what reading its class returns or throws, what writing throws, and how often each was called.
    class Fake
    {
        public ProcessPriorityClass Current = ProcessPriorityClass.Normal;
        public Exception ReadThrows, WriteThrows;
        public int Reads, Writes;
        public ProcessPriorityClass Read() { Reads++; if (ReadThrows != null) throw ReadThrows; return Current; }
        public void Write(ProcessPriorityClass c) { Writes++; if (WriteThrows != null) throw WriteThrows; Current = c; }
    }

    static HashSet<int> refused, noted;
    static bool lastNote;

    static SimPrioOutcome Step(int pid, int basePrio, bool raisedByUs, Fake f)
    {
        bool note; ProcessPriorityClass was;
        var r = SimPriority.Next(pid, basePrio, raisedByUs, refused, noted, f.Read, f.Write, out note, out was);
        lastNote = note;
        return r;
    }

    static void Reset() { refused = new HashSet<int>(); noted = new HashSet<int>(); }

    static int Main()
    {
        // ---- refusal: tried once, never again ----
        Reset();
        var denied = new Fake { WriteThrows = new Win32Exception(5, "Access is denied") };
        Check("01 a write refused by the anti-cheat is Refused", Step(100, 8, false, denied) == SimPrioOutcome.Refused);
        Check("02 the refused PID is remembered", refused.Contains(100));
        int reads = denied.Reads, writes = denied.Writes;
        bool allLeft = true;
        for (int i = 0; i < 600; i++) if (Step(100, 8, false, denied) != SimPrioOutcome.LeftAlone) allLeft = false;   // a 30-minute race at 3 s
        Check("03 every later check leaves it alone", allLeft);
        Check("04 and never opens it again: no read, no write", denied.Reads == reads && denied.Writes == writes, denied.Reads + " reads, " + denied.Writes + " writes");

        Reset();
        var noRead = new Fake { ReadThrows = new Win32Exception(5, "Access is denied") };
        Check("05 even reading the class refused: Refused", Step(101, 8, false, noRead) == SimPrioOutcome.Refused);
        Step(101, 8, false, noRead); Step(101, 8, false, noRead);
        Check("06 a refused read is tried once only, and nothing is written", noRead.Reads == 1 && noRead.Writes == 0, noRead.Reads + " reads, " + noRead.Writes + " writes");

        Reset();
        var unauth = new Fake { WriteThrows = new UnauthorizedAccessException() };
        Check("07 UnauthorizedAccessException is a refusal too", Step(102, 8, false, unauth) == SimPrioOutcome.Refused && refused.Contains(102));
        Step(102, 8, false, unauth);
        Check("08 and is not retried", unauth.Writes == 1);

        // ---- success ----
        Reset();
        var ok = new Fake();
        bool note0; ProcessPriorityClass was;
        var r = SimPriority.Next(200, 8, false, refused, noted, ok.Read, ok.Write, out note0, out was);
        Check("09 a Normal sim that allows it is Raised", r == SimPrioOutcome.Raised && ok.Current == ProcessPriorityClass.High);
        Check("10 Raised reports the class it had before", was == ProcessPriorityClass.Normal);
        Check("11 a raise is not noted as someone else's High", !note0 && !noted.Contains(200));
        Check("12 next check (snapshot now High, raised by us): already High, no handle", Step(200, 13, true, ok) == SimPrioOutcome.AlreadyHigh && ok.Reads == 1 && ok.Writes == 1 && !lastNote);

        // ---- already High ----
        Reset();
        var high = new Fake { Current = ProcessPriorityClass.High };
        Check("13 a sim at High in the snapshot is AlreadyHigh", Step(300, 13, false, high) == SimPrioOutcome.AlreadyHigh);
        Check("14 and is noted (set by iRacing or another tool)", lastNote);
        Check("15 without opening it", high.Reads == 0 && high.Writes == 0);
        Step(300, 13, false, high);
        Check("16 the note is once per PID", !lastNote);
        Reset();
        Check("17 a High class read from the handle is AlreadyHigh too", Step(301, 0, false, high) == SimPrioOutcome.AlreadyHigh && lastNote && high.Writes == 0);
        Reset();
        Check("18 Realtime in the snapshot is never lowered", Step(302, 24, false, high) == SimPrioOutcome.AlreadyHigh && high.Writes == 0);

        // refused first, then iRacing raises itself: the note isn't blocked by the refused set
        Reset();
        var later = new Fake { ReadThrows = new Win32Exception(5, "Access is denied") };
        Step(400, 8, false, later);
        Check("19 refused, later seen at High: still LeftAlone", Step(400, 13, false, later) == SimPrioOutcome.LeftAlone);
        Check("20 but the High note is written", lastNote);
        Check("21 still without opening it", later.Reads == 1 && later.Writes == 0);
        Step(400, 13, false, later);
        Check("22 and noted once only", !lastNote);

        // ---- a new PID gets a fresh try ----
        Reset();
        var first = new Fake { WriteThrows = new Win32Exception(5, "Access is denied") };
        Step(500, 8, false, first);
        var next = new Fake();
        Check("23 a new sim (new PID) after a refusal is tried again", Step(501, 8, false, next) == SimPrioOutcome.Raised && next.Writes == 1);
        Check("24 the old PID stays refused", Step(500, 8, false, first) == SimPrioOutcome.LeftAlone);

        // ---- exited ----
        Reset();
        var gone = new Fake { ReadThrows = new InvalidOperationException("No process is associated with this object.") };
        Check("25 a sim that exited is Gone, not refused", Step(600, 8, false, gone) == SimPrioOutcome.Gone && !refused.Contains(600));

        // ---- the text ----
        Check("26 the refusal line", SimPriority.RefusedEvent(7) == "iRacing's anti-cheat didn't let Race Mode change the sim's priority (PID 7). That's fine: Race Mode leaves it alone.", SimPriority.RefusedEvent(7));
        Check("27 the refusal status", SimPriority.RefusedTick == "iRacing manages its own priority; Race Mode left it alone");
        Check("28 the High line", SimPriority.HighEvent(7).StartsWith("iRacing sim is at High priority (set by iRacing or another tool)"), SimPriority.HighEvent(7));

        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
