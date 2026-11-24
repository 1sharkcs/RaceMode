// Tests for Setup.cs (install, start with Windows, uninstall safety) and Obs.cs (OBS remote-control settings edit).
// Everything runs inside a throwaway folder: RACEMODE_TEST_ROOT redirects the install folder, Start menu and Desktop,
// and the registry names become "RaceModeTest". Build next to RaceMode.exe:
//   csc -r:RaceMode.exe -r:System.Web.Extensions.dll SetupTests.cs
using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using Microsoft.Win32;
using RaceMode;

class SetupTests
{
    static int pass, fail;
    static void Check(string n, bool ok, string d) { if (ok) pass++; else fail++; Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (ok || d == null ? "" : "  -> " + d)); }
    static string Reg(string key, string name) { using (var k = Registry.CurrentUser.OpenSubKey(key)) return k == null ? null : k.GetValue(name) as string; }
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [STAThread]
    static void Main()
    {
        var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setupcases");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("RACEMODE_TEST_ROOT", root);
        try
        {
            Check("01 paths are inside the test folder", Installer.InstallDir.StartsWith(root) && Installer.StartMenuLnk.StartsWith(root) && Installer.DesktopLnk.StartsWith(root) && Installer.UninstallKey.EndsWith("RaceModeTest"), Installer.InstallDir);

            var done = Installer.Install(true, true);
            Check("02 exe copied", File.Exists(Installer.InstalledExe) && new FileInfo(Installer.InstalledExe).Length == new FileInfo(typeof(Installer).Assembly.Location).Length, null);
            Check("03 data folder made", Directory.Exists(Installer.DataDir), null);
            Check("04 Start menu shortcut points at the installed exe", string.Equals(DataDirs.ShortcutTarget(Installer.StartMenuLnk), Installer.InstalledExe, StringComparison.OrdinalIgnoreCase), DataDirs.ShortcutTarget(Installer.StartMenuLnk));
            Check("05 desktop shortcut too", string.Equals(DataDirs.ShortcutTarget(Installer.DesktopLnk), Installer.InstalledExe, StringComparison.OrdinalIgnoreCase), null);
            var un = Reg(Installer.UninstallKey, "UninstallString");
            Check("06 Settings > Apps entry with a quoted --uninstall command", un == "\"" + Installer.InstalledExe + "\" --uninstall", un);
            Check("07 entry has a name and version", Reg(Installer.UninstallKey, "DisplayName") == "Race Mode" && Reg(Installer.UninstallKey, "DisplayVersion") == Updater.Current, null);
            var run = Reg(RunKey, "RaceModeTest");
            Check("08 start with Windows: quoted path + --minimized", run == "\"" + Installer.InstalledExe + "\" --minimized", run);
            Check("09 setup.log written, no password-like text", File.Exists(Path.Combine(Installer.DataDir, "setup.log")) && File.ReadAllText(Path.Combine(Installer.DataDir, "setup.log")).IndexOf("password", StringComparison.OrdinalIgnoreCase) < 0, null);

            File.WriteAllText(Path.Combine(Installer.DataDir, "config.json"), "{\"keep\":true}");
            Installer.Install(false, false);
            Check("10 reinstall keeps the user's data", File.ReadAllText(Path.Combine(Installer.DataDir, "config.json")) == "{\"keep\":true}", null);
            Check("11 reinstall with autostart off removes the Run entry", Reg(RunKey, "RaceModeTest") == null, null);
            Check("12 no RaceMode.exe.old left behind", !File.Exists(Installer.InstalledExe + ".old"), null);

            // Uninstall from a copy that isn't the installed one: registry and shortcuts go, files are never deleted.
            string err = null;
            try { Installer.Uninstall(true); } catch (Exception ex) { err = ex.Message; }
            Check("13 uninstall from a non-installed copy refuses to delete files", err != null && File.Exists(Installer.InstalledExe) && Directory.Exists(Installer.DataDir), err);
            Check("14 ...but removed the Settings > Apps entry and shortcuts", Reg(Installer.UninstallKey, "UninstallString") == null && !File.Exists(Installer.StartMenuLnk) && !File.Exists(Installer.DesktopLnk), null);

            // A shortcut with the same name that points somewhere else is never removed.
            Directory.CreateDirectory(Path.GetDirectoryName(Installer.DesktopLnk));
            DataDirs.CreateShortcut(Installer.DesktopLnk, Environment.ExpandEnvironmentVariables(@"%WINDIR%\notepad.exe"), root);
            try { Installer.Uninstall(true); } catch { }
            Check("15 someone else's 'Race Mode.lnk' is left alone", File.Exists(Installer.DesktopLnk), null);

            // ---- OBS remote-control settings (on copies only) ----
            var obsDir = Path.Combine(root, "obs"); Directory.CreateDirectory(obsDir);
            var cfg = Path.Combine(obsDir, "config.json");
            Check("16 OBS running -> refuses to edit", Obs.EnableRemoteControl(cfg, true) != null && !File.Exists(cfg), null);
            Check("17 no settings file yet -> created, on, with a password", Obs.EnableRemoteControl(cfg, false) == null, null);
            var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(cfg));
            Check("18 ...enabled, auth on, port 4455, 24-char password", (bool)d["server_enabled"] && (bool)d["auth_required"] && (int)d["server_port"] == 4455 && ((string)d["server_password"]).Length == 24, null);
            File.WriteAllText(cfg, "{\"alerts_enabled\":false,\"auth_required\":true,\"first_load\":false,\"server_enabled\":false,\"server_password\":\"keepMe123\",\"server_port\":4460}");
            Check("19 existing settings -> switched on", Obs.EnableRemoteControl(cfg, false) == null, null);
            d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(cfg));
            Check("20 ...password and port kept, backup made", (string)d["server_password"] == "keepMe123" && (int)d["server_port"] == 4460 && (bool)d["server_enabled"] && File.Exists(cfg + ".racemode.bak"), null);
            File.WriteAllText(cfg, "{not json");
            Check("21 unreadable settings -> left alone", Obs.EnableRemoteControl(cfg, false) != null && File.ReadAllText(cfg) == "{not json", null);
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(Installer.UninstallKey, false); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue("RaceModeTest", false); } catch { }
        }
        // 22-25 HWiNFO sharing switched on in its per-user settings (a test key under RACEMODE_TEST_ROOT)
        Check("22 test key, never HWiNFO's real settings", HwinfoSetup.KeyPath.StartsWith(@"Software\RaceModeTest"), null);
        if (HwinfoSetup.KeyPath.StartsWith(@"Software\RaceModeTest"))
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\RaceModeTest", false);
            using (var pre = Registry.CurrentUser.CreateSubKey(HwinfoSetup.KeyPath + @"\Sensors\E0002000_0\Temp0"))
            { pre.SetValue("InVSB", 1, RegistryValueKind.DWord); pre.SetValue("VSBidx", 7, RegistryValueKind.DWord); }
            int addedFirst = HwinfoSetup.Apply(), addedAgain = HwinfoSetup.Apply();
            Check("23 sharing on, 5 CPU readings (Intel and AMD) added once", HwinfoSetup.SharingOn() && addedFirst == 5 && addedAgain == 0, addedFirst + "/" + addedAgain);
            var idx = new List<int>();
            foreach (var k in new[] { @"F0000400_0\Temp510", @"F0000400_0\Temp511", @"F0002A00_0\Temp4", @"F0000501_0\Temp0", @"F0000501_0\Temp3" })
                using (var rk = Registry.CurrentUser.OpenSubKey(HwinfoSetup.KeyPath + @"\Sensors\" + k))
                    if (rk != null && (int)rk.GetValue("InVSB", 0) == 1) idx.Add((int)rk.GetValue("VSBidx", -1));
            Check("24 new readings are placed after the ones already shared", idx.Count == 5 && idx.All(i => i > 7) && idx.Distinct().Count() == 5, string.Join(",", idx.Select(i => i.ToString()).ToArray()));
            using (var keep = Registry.CurrentUser.OpenSubKey(HwinfoSetup.KeyPath + @"\Sensors\E0002000_0\Temp0"))
                Check("25 readings already shared are left as they were", keep != null && (int)keep.GetValue("VSBidx", -1) == 7, null);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\RaceModeTest", false);
        }

        // 25a-26 test copies get their own lock names per test folder, so a soak test copy can't be mistaken for this run's
        {
            bool created;
            var lockName = @"Local\RaceMode.Instance" + Installer.NameSuffix;
            Check("25a the test lock is this test folder's own (never the real app's, nor another test run's)",
                Installer.NameSuffix.StartsWith(".Test.") && Installer.NameSuffix.Length == 14 && Installer.TestSuffix(null) == ""
                && Installer.TestSuffix(@"C:\a") != Installer.TestSuffix(@"C:\b") && Installer.TestSuffix(@"C:\A\") == Installer.TestSuffix(@"c:\a"), Installer.NameSuffix);
            using (var open = new System.Threading.Mutex(true, lockName, out created))
            {
                Check("26 an open Race Mode is seen", created && Installer.RaceModeRunning(), created ? null : "the test lock already existed");
                if (created) open.ReleaseMutex();
            }
        }
        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
