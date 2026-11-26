// Race Mode - putting the iRacing sim at High priority, without fighting iRacing's anti-cheat.
// iRacing's anti-cheat (EOS) can refuse OpenProcess on the sim ("Access is denied"). Race Mode then tries once,
// accepts the refusal and never tries to change that PID's priority again. A sim already at High (iRacing raises
// itself) is seen from the process snapshot's base priority, with no handle opened at all. (The session log still
// reads the sim's CPU time and priority while it samples; that is reading only, and not part of this rule.)
// Pure over delegates, so tests play refusals, successes and new PIDs without touching a real process.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RaceMode
{
    public enum SimPrioOutcome
    {
        LeftAlone,   // refused before: not touched again
        Refused,     // refused just now (first and only time for this PID): log it once
        Raised,      // set to High by Race Mode just now
        AlreadyHigh, // already at High: nothing written
        Gone         // the process exited while being looked at
    }

    public static class SimPriority
    {
        // The base priority Windows reports for the High class.
        public const int HighBase = 13;

        public const string RefusedTick = "iRacing manages its own priority; Race Mode left it alone";

        public static string RefusedEvent(int pid)
        {
            return "iRacing's anti-cheat didn't let Race Mode change the sim's priority (PID " + pid + "). That's fine: Race Mode leaves it alone.";
        }

        public static string HighEvent(int pid)
        {
            return "iRacing sim is at High priority (set by iRacing or another tool), PID " + pid;
        }

        // One sim process, once per check. basePriority: from the process snapshot (no handle; 0 when unknown).
        // raisedByUs: Race Mode set this PID to High earlier. refused and notedHigh are kept per PID by the caller.
        // read and write open the process; they are never called for a PID in refused, nor for one already at High.
        // noteHigh: true once per PID, when the sim is seen at High that Race Mode didn't set (log it).
        // was: the class before Race Mode raised it (only meaningful when the outcome is Raised).
        public static SimPrioOutcome Next(int pid, int basePriority, bool raisedByUs, HashSet<int> refused, HashSet<int> notedHigh,
                                          Func<ProcessPriorityClass> read, Action<ProcessPriorityClass> write,
                                          out bool noteHigh, out ProcessPriorityClass was)
        {
            was = ProcessPriorityClass.Normal;
            // Checked before the refused set, from the snapshot: a refused sim that iRacing raised itself is still noted.
            noteHigh = basePriority >= HighBase && !raisedByUs && notedHigh.Add(pid);
            if (refused.Contains(pid)) return SimPrioOutcome.LeftAlone;
            if (basePriority >= HighBase) return SimPrioOutcome.AlreadyHigh;
            try
            {
                was = read();
                if (was == ProcessPriorityClass.High)
                {
                    if (!raisedByUs && notedHigh.Add(pid)) noteHigh = true;
                    return SimPrioOutcome.AlreadyHigh;
                }
                write(ProcessPriorityClass.High);
                return SimPrioOutcome.Raised;
            }
            catch (InvalidOperationException) { return SimPrioOutcome.Gone; }
            catch (Exception)
            {
                // Win32Exception / UnauthorizedAccessException ("Access is denied"), or anything else: never again for this PID.
                refused.Add(pid);
                return SimPrioOutcome.Refused;
            }
        }
    }
}
