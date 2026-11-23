// Apps Race Mode must never force-close, and a polite close for apps hidden in the tray.
//
// Found live on 2026-11-23: OBS recording while minimized to the tray has no main window, so it never got the polite
// close (CloseMainWindow needs a visible main window), and 5 s later all its processes were force-closed mid-recording.
// The recording was cut off, and OBS showed its "safe mode" question the next time it started.
//
// Now: apps that save or record as they close (OBS, browsers, mail) are only ever asked. If they're in the tray, the
// ask goes to their hidden main window, so OBS gets its normal close and, while recording, asks "are you sure?". If
// they're still running after the wait, they're left open and Race Mode says so; they are never killed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace RaceMode
{
    public static class GentleClose
    {
        // Process names (no .exe) that are never force-closed.
        static readonly string[] Never = {
            "obs64", "obs32", "obs", "obs-browser-page", "Streamlabs Desktop", "Streamlabs OBS", "XSplit.Core", "PRISMLiveStudio",
            "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "OUTLOOK", "olk", "thunderbird",
            "WINWORD", "EXCEL", "POWERPNT", "Resolve", "Premiere Pro", "AfterFX", "Photoshop", "blender" };

        public static bool NeverForce(string name)
        {
            return !string.IsNullOrEmpty(name) && (ObsGuard.IsObs(name) || Never.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
        }

        // Helpers that end with their main app, so asking or killing them separately only does harm (OBS's browser
        // sources). Keyed by the helper; the value is the app that ends it.
        static readonly Dictionary<string, string> HelperOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "obs-browser-page", "obs64" }, { "obs-ffmpeg-mux", "obs64" }, { "obs-amf-test", "obs64" }, { "obs-nvenc-test", "obs64" },
            { "obs-qsv-test", "obs64" }, { "steamwebhelper", "steam" }
        };

        // The names to ask, without helpers whose main app is asked too (it takes them down itself).
        public static List<string> WithoutHelpers(IEnumerable<string> names)
        {
            var list = names.ToList();
            return list.Where(n => { string main; return !(HelperOf.TryGetValue(n, out main) && list.Any(x => string.Equals(x, main, StringComparison.OrdinalIgnoreCase))); }).ToList();
        }

        const uint WM_CLOSE = 0x0010;
        const int GWL_STYLE = -16;
        const uint WS_CAPTION = 0x00C00000;
        const uint GW_OWNER = 4;
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        // Whether a process really runs as administrator: true / false, or null if Windows won't say.
        // Found live on 2026-11-23: the old test (can Race Mode open the process fully?) also failed for Discord's
        // sandboxed helper processes, so Race Mode said "Discord runs as administrator" and then "closed after all".
        public static bool? Elevated(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5 ? (bool?)true : null;   // access denied: an elevated process
            try
            {
                IntPtr tok;
                if (!OpenProcessToken(h, 0x0008 /* TOKEN_QUERY */, out tok)) return null;
                try
                {
                    int elevated, len;
                    if (!GetTokenInformation(tok, 20 /* TokenElevation */, out elevated, 4, out len)) return null;
                    return elevated != 0;
                }
                finally { CloseHandle(tok); }
            }
            finally { CloseHandle(h); }
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);

        // Asks a process with no visible main window (in the tray) to close: WM_CLOSE to its one real main window,
        // found by its title ("OBS 33.0.1 - Profile: ..."). Only that window: OBS keeps about 55 hidden titled windows
        // (docks, dialogs), and a close sent to all of them was found live on 2026-11-23. Returns how many it asked (0/1).
        public static int AskHidden(int pid, string titlePrefix)
        {
            if (string.IsNullOrEmpty(titlePrefix)) return 0;
            int asked = 0;
            try
            {
                EnumWindows((h, l) =>
                {
                    if (asked > 0) return false;
                    uint p; GetWindowThreadProcessId(h, out p);
                    if (p != (uint)pid || GetWindow(h, GW_OWNER) != IntPtr.Zero || ((uint)GetWindowLong(h, GWL_STYLE) & WS_CAPTION) != WS_CAPTION) return true;
                    var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, sb.Capacity);
                    if (MainTitle(sb.ToString(), titlePrefix) && PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) asked++;
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return asked;
        }
        // OBS's main window title: "OBS 33.0.1 - Profile: X - Scenes: Y" (or "OBS 33.0.1 (64-bit, windows) - ..." on older ones)
        public static bool MainTitle(string title, string prefix)
        {
            return !string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(prefix) && title.StartsWith(prefix, StringComparison.Ordinal)
                && title.Length > prefix.Length && char.IsDigit(title[prefix.Length]);
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int max);
    }
}
