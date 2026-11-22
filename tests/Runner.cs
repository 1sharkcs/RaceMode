// One test program for every suite: Tests.exe runs them all, Tests.exe <Suite> runs one.
//
// Why one program: the suites used to be built as seven separate exes on every run. A burst of freshly compiled,
// unsigned programs that then rewrite and rename files is what a ransomware monitor is built to stop, and one
// (Bitdefender's Advanced Threat Defense) did stop them, killing the whole process tree. One exe per run, started
// once per suite, does the same checks with one new program on disk instead of seven.
//
// Build (next to a built RaceMode.exe, with tests\fixtures copied to .\fixtures):
//   csc /nologo /main:TestRunner /r:RaceMode.exe /r:System.Core.dll /r:System.Web.Extensions.dll /out:Tests.exe tests\*.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

static class TestRunner
{
    // Every class in this program whose name ends in "Tests" and has its own static Main is a suite.
    static List<Type> Suites()
    {
        return typeof(TestRunner).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Tests", StringComparison.Ordinal) && Entry(t) != null)
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    static MethodInfo Entry(Type t)
    {
        return t.GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
    }

    static int Main(string[] args)
    {
        var suites = Suites();
        if (args.Length >= 1)
        {
            // one suite, in this process (a suite ends the process itself with its own exit code)
            var suite = suites.FirstOrDefault(t => string.Equals(t.Name, args[0], StringComparison.OrdinalIgnoreCase));
            if (suite == null) { Console.WriteLine("No suite named " + args[0] + ". Suites: " + string.Join(", ", suites.Select(t => t.Name).ToArray())); return 2; }
            try
            {
                var result = Entry(suite).Invoke(null, null);
                return result is int ? (int)result : 0;
            }
            catch (TargetInvocationException ex) { Console.WriteLine("FAIL " + suite.Name + " stopped: " + ex.InnerException); return 1; }
        }

        // every suite, each in its own run of this same program, so one can't leave state behind for the next
        var failed = new List<string>();
        string me = Process.GetCurrentProcess().MainModule.FileName;
        foreach (var suite in suites)
        {
            Console.WriteLine("== " + suite.Name);
            using (var p = Process.Start(new ProcessStartInfo(me, suite.Name) { UseShellExecute = false }))
            {
                p.WaitForExit();
                if (p.ExitCode != 0) failed.Add(suite.Name);
            }
        }
        Console.WriteLine(failed.Count == 0 ? "All " + suites.Count + " suites passed" : failed.Count + " of " + suites.Count + " suites failed: " + string.Join(", ", failed.ToArray()));
        return failed.Count == 0 ? 0 : 1;
    }
}
