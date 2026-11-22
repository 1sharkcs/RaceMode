using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RaceMode
{
    // Where Race Mode keeps its own files (config.json, graphics\, logs\, crash.log).
    public static class DataDirs
    {
        // RACEMODE_TEST_ROOT (tests only) moves these under the test folder too, like Installer's paths.
        static string TestRoot { get { var r = Environment.GetEnvironmentVariable("RACEMODE_TEST_ROOT"); return string.IsNullOrEmpty(r) ? null : r; } }

        public static string DefaultData
        {
            get { return Path.Combine(TestRoot != null ? Path.Combine(TestRoot, "LocalAppData") : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"RaceMode\data"); }
        }

        // Order: --data <dir>, then portable.txt next to the exe, then (local builds only) a config.json already next
        // to the exe, for a developer's own working folder; then %LOCALAPPDATA%\RaceMode\data. Released builds never
        // adopt a folder just because some other program's config.json happens to sit beside the exe.
        public static string Resolve(string home, string[] args)
        {
            string dir = null;
            for (int i = 1; args != null && i + 1 < args.Length; i++)
                if (args[i] == "--data") dir = args[i + 1];
            bool localBuild = BuildInfo.Repo.Length == 0;
            if (dir == null && (File.Exists(Path.Combine(home, "portable.txt")) || (localBuild && File.Exists(Path.Combine(home, "config.json")))))
                dir = home;
            if (dir == null) dir = DefaultData;
            try { dir = Path.GetFullPath(dir); Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        // First start of an installed copy: copy (never move) the config and graphics from the old
        // Documents\RaceMode folder. Logs stay where they are. Returns a line for events.log, or null.
        public static string MigrateIfNeeded(string data)
        {
            try
            {
                if (!string.Equals(Path.GetFullPath(data).TrimEnd('\\'), Path.GetFullPath(DefaultData).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
                if (File.Exists(Path.Combine(data, "config.json"))) return null;
                var old = Path.Combine(TestRoot != null ? Path.Combine(TestRoot, "Documents") : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RaceMode");
                var oldCfg = Path.Combine(old, "config.json");
                if (!File.Exists(oldCfg)) return null;
                File.Copy(oldCfg, Path.Combine(data, "config.json"), false);
                int n = 0;
                var g = Path.Combine(old, "graphics");
                if (Directory.Exists(g))
                {
                    var dst = Path.Combine(data, "graphics");
                    Directory.CreateDirectory(dst);
                    foreach (var f in Directory.GetFiles(g, "*.ini"))
                    {
                        var to = Path.Combine(dst, Path.GetFileName(f));
                        if (!File.Exists(to)) { File.Copy(f, to, false); n++; }
                    }
                }
                File.WriteAllText(Path.Combine(data, "migrated-from.txt"), "Copied config.json and " + n + " graphics files from " + old + " on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ". The old folder was left as it was.");
                return "Copied your settings from " + old + " (the old folder was left as it was)";
            }
            catch (Exception ex) { return "Couldn't copy your old settings: " + ex.Message; }
        }

        // A Windows shortcut (.lnk) to exe, made with the shell's own COM object (no PowerShell).
        public static void CreateShortcut(string lnk, string exe, string workDir)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(exe);
                link.SetWorkingDirectory(workDir);
                link.SetIconLocation(exe, 0);
                link.SetDescription("Race Mode: one switch to get your PC ready for iRacing");
                ((IPersistFile)link).Save(lnk, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }

        // Where a shortcut points, or null if it can't be read.
        public static string ShortcutTarget(string lnk)
        {
            object o = null;
            try
            {
                o = new ShellLink();
                ((IPersistFile)o).Load(lnk, 0);
                var sb = new StringBuilder(1024);
                ((IShellLinkW)o).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.ToString();
            }
            catch { return null; }
            finally { if (o != null) Marshal.ReleaseComObject(o); }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurSel([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }
}
