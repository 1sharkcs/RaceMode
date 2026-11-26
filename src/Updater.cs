using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace RaceMode
{
    public class UpdateInfo
    {
        public string Version = "", ZipUrl = "", ShaUrl = "", PageUrl = "";
        public bool Prerelease;
    }

    // Update check and install from this app's GitHub releases (BuildInfo.Repo).
    // - Checking only reads the releases list. Nothing is downloaded until the user clicks Update.
    // - Install: download RaceMode.zip + RaceMode.zip.sha256, check the hash, check the exe inside is Race Mode
    //   at the expected version, rename the running exe to RaceMode.exe.old, move the new one in, start it with
    //   --after-update <pid>. The new copy waits for this one to quit, and puts .old back if it can't start.
    // - The hash proves the download is intact, not who published it (releases are unsigned).
    public static class Updater
    {
        const long MaxDownload = 32L * 1024 * 1024;
        static bool _fresh;

        public static string Current
        {
            get
            {
                var a = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(Updater).Assembly, typeof(AssemblyInformationalVersionAttribute));
                return a == null || string.IsNullOrEmpty(a.InformationalVersion) ? "local" : a.InformationalVersion;
            }
        }

        // Builds from build.cmd (not the release workflow) never update themselves.
        public static bool Enabled { get { return Parse(Current) != null && !string.IsNullOrEmpty(BuildInfo.Repo); } }

        public static string Repo { get { return BuildInfo.Repo; } }

        static string ExePath { get { return typeof(Updater).Assembly.Location; } }

        // ---------------- versions ----------------
        // "1.2.3" or "1.2.3-rc.4" (an optional leading v). A stable release sorts after its own release candidates.
        public static long[] Parse(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            v = v.Trim(); if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
            var dash = v.IndexOf('-');
            var core = dash < 0 ? v : v.Substring(0, dash);
            var parts = core.Split('.');
            if (parts.Length != 3) return null;
            var r = new long[4];
            for (int i = 0; i < 3; i++) if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out r[i])) return null;
            if (dash < 0) r[3] = long.MaxValue;
            else
            {
                var pre = v.Substring(dash + 1);
                if (!pre.StartsWith("rc.", StringComparison.Ordinal) || !long.TryParse(pre.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out r[3])) return null;
            }
            return r;
        }

        public static int Compare(string a, string b)
        {
            var x = Parse(a); var y = Parse(b);
            if (x == null || y == null) return x == null ? (y == null ? 0 : -1) : 1;
            for (int i = 0; i < 4; i++) if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
            return 0;
        }

        // ---------------- check ----------------
        // Blocking (run it off the UI thread). Returns the newer release, or null if up to date.
        public static UpdateInfo Check(bool testVersions)
        {
            if (!Enabled) return null;
            var api = "https://api.github.com/repos/" + BuildInfo.Repo + "/releases" + (testVersions ? "?per_page=15" : "/latest");
            string text;
            try { text = GetText(api); }
            catch (WebException ex)
            {
                // GitHub answers 404 when the repo has no published release yet: same as "up to date".
                var hr = ex.Response as HttpWebResponse;
                if (hr != null && hr.StatusCode == HttpStatusCode.NotFound) return null;
                throw;
            }
            var json = new JavaScriptSerializer().DeserializeObject(text);
            var list = new List<Dictionary<string, object>>();
            if (json is object[]) list.AddRange(((object[])json).OfType<Dictionary<string, object>>());
            else if (json is Dictionary<string, object>) list.Add((Dictionary<string, object>)json);
            UpdateInfo best = null;
            foreach (var rel in list)
            {
                if (Bool(rel, "draft")) continue;
                var u = FromRelease(rel);
                if (u == null || (u.Prerelease && !testVersions)) continue;
                if (best == null || Compare(u.Version, best.Version) > 0) best = u;
            }
            return best != null && Compare(best.Version, Current) > 0 ? best : null;
        }

        public static UpdateInfo FromRelease(Dictionary<string, object> rel)
        {
            var tag = Str(rel, "tag_name");
            if (Parse(tag) == null) return null;
            var u = new UpdateInfo { Version = tag.TrimStart('v', 'V'), Prerelease = Bool(rel, "prerelease"), PageUrl = Str(rel, "html_url") };
            var prefix = "https://github.com/" + BuildInfo.Repo + "/releases/download/";
            object assets;
            if (rel.TryGetValue("assets", out assets) && assets is object[])
                foreach (var a in ((object[])assets).OfType<Dictionary<string, object>>())
                {
                    var name = Str(a, "name"); var url = Str(a, "browser_download_url");
                    if (!url.StartsWith(prefix, StringComparison.Ordinal)) continue;   // only this repo's own release files
                    if (name == "RaceMode.zip") u.ZipUrl = url;
                    else if (name == "RaceMode.zip.sha256") u.ShaUrl = url;
                }
            return u.ZipUrl.Length > 0 && u.ShaUrl.Length > 0 ? u : null;
        }

        // ---------------- install ----------------
        // Blocking. Downloads, verifies and swaps the exe. Returns null when the new exe is in place
        // (the caller then calls StartNewAndQuit), or a plain message saying what went wrong.
        public static string Install(UpdateInfo u, string dataDir)
        {
            var dir = Path.Combine(dataDir, "updates");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                var zip = Path.Combine(dir, "RaceMode.zip");
                Download(u.ZipUrl, zip);
                var expected = (GetText(u.ShaUrl).Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "").ToLowerInvariant();
                string actual;
                using (var sha = SHA256.Create()) using (var fs = File.OpenRead(zip))
                    actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (expected.Length != 64 || actual != expected) return "The download didn't match its checksum, so nothing was changed";

                var fresh = Path.Combine(dir, "RaceMode.exe");
                using (var z = ZipFile.OpenRead(zip))
                {
                    var entry = z.Entries.FirstOrDefault(e => e.FullName == "RaceMode.exe");
                    if (entry == null) return "The download doesn't contain RaceMode.exe, so nothing was changed";
                    if (entry.Length > 50L * 1024 * 1024) return "The download is far bigger than Race Mode, so nothing was changed";
                    // Copy with a running total: the zip's stated size can lie, so stop at 50 MB whatever it says.
                    using (var src = entry.Open()) using (var dst = File.Create(fresh))
                    {
                        var buf = new byte[81920]; long total = 0; int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            total += n;
                            if (total > 50L * 1024 * 1024) { dst.Close(); File.Delete(fresh); return "The download is far bigger than Race Mode, so nothing was changed"; }
                            dst.Write(buf, 0, n);
                        }
                    }
                }
                bool isRaceMode;
                try { isRaceMode = AssemblyName.GetAssemblyName(fresh).Name == "RaceMode"; }
                catch { isRaceMode = false; }
                if (!isRaceMode) return "The download isn't Race Mode, so nothing was changed";
                var pv = FileVersionInfo.GetVersionInfo(fresh).ProductVersion ?? "";
                if (Compare(pv, u.Version) != 0) return "The download is version " + pv + ", not " + u.Version + ", so nothing was changed";

                var exe = ExePath; var old = exe + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                try { File.Move(fresh, exe); }
                catch { File.Move(old, exe); throw; }
                return null;
            }
            catch (Exception ex) { return "The update didn't install: " + ex.Message; }
            finally { try { foreach (var f in Directory.GetFiles(dir)) File.Delete(f); } catch { } }   // the zip and any unpacked exe that failed a check
        }

        public static void StartNew()
        {
            ObsDock.Stop();   // let go of the dock's port before the new copy starts (it listens there next)
            Process.Start(new ProcessStartInfo(ExePath, "--after-update " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture))
            { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath) });
        }

        // New copy, at start: wait (up to 20 s) for the old copy to quit so the single-instance lock is free.
        public static void FinishSwap(string oldPid)
        {
            _fresh = File.Exists(ExePath + ".old");
            int pid;
            if (!int.TryParse(oldPid, NumberStyles.None, CultureInfo.InvariantCulture, out pid)) return;
            try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(20000); } catch { }
        }

        // A normal start (no --after-update) of the installed copy that finds RaceMode.exe.old next to it: an update
        // was installed while Race Mode was on and starts only now. Treat this start like the restart after an update,
        // so a new version that can't start still rolls back, and the old exe goes once this one is up.
        public static void NoticeEarlierUpdate()
        {
            if (File.Exists(ExePath + ".old") && Installer.IsInstalledCopy) _fresh = true;
        }

        // New copy, once its window is up: the update worked, so the old exe can go.
        public static bool ConfirmSwap()
        {
            if (!_fresh) return false;
            _fresh = false;
            // Only the installed copy keeps one version back (uninstall removes it). A copy run from any other folder
            // would leave an exe-sized file there for good, so its old exe is removed as before.
            if (Installer.IsInstalledCopy) KeepPrevious(ExePath);
            else { try { File.Delete(ExePath + ".old"); } catch { } }
            return true;
        }

        // The exe the update replaced is kept as RaceMode.exe.previous (one version back, replaced at the next update), so
        // going back a version by hand is possible. It leaves the ".old" name, which means "an update not yet confirmed".
        public static string PreviousExe(string exe) { return exe + ".previous"; }

        public static bool KeepPrevious(string exe)
        {
            var old = exe + ".old"; var prev = PreviousExe(exe);
            try
            {
                if (!File.Exists(old)) return false;
                if (File.Exists(prev)) { File.SetAttributes(prev, FileAttributes.Normal); File.Delete(prev); }
                File.Move(old, prev);
                return true;
            }
            catch
            {
                // Couldn't keep it: the ".old" name must still go, or every later start would look like a fresh update.
                try { File.Delete(old); } catch { }
                return false;
            }
        }

        // New copy failed to start: put the previous exe back and start it. True if it did.
        public static bool RollBackIfFreshUpdate()
        {
            if (!_fresh) return false;
            try
            {
                var exe = ExePath; var old = exe + ".old"; var bad = exe + ".failed";
                if (!File.Exists(old)) return false;
                if (File.Exists(bad)) File.Delete(bad);
                File.Move(exe, bad);
                File.Move(old, exe);
                ObsDock.Stop();
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
                return true;
            }
            catch { return false; }
        }

        // ---------------- http ----------------
        static HttpWebRequest Request(string url)
        {
            // Modern TLS only: 1.2, plus 1.3 where Windows has it (12288 = Tls13, named in .NET 4.8). Never SSL3/TLS 1.0/1.1.
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
            catch (NotSupportedException) { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "RaceMode-Updater";
            r.Accept = url.Contains("api.github.com") ? "application/vnd.github+json" : "*/*";
            if (url.Contains("api.github.com")) r.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            r.Timeout = 30000; r.ReadWriteTimeout = 30000;
            r.AllowAutoRedirect = true;
            return r;
        }

        static string GetText(string url)
        {
            using (var resp = (HttpWebResponse)Request(url).GetResponse())
            using (var s = resp.GetResponseStream())
            using (var rd = new StreamReader(s))
            {
                var buf = new char[1024 * 1024];
                int n = rd.ReadBlock(buf, 0, buf.Length);
                return new string(buf, 0, n);
            }
        }

        static void Download(string url, string path)
        {
            using (var resp = (HttpWebResponse)Request(url).GetResponse())
            {
                if (resp.ContentLength > MaxDownload) throw new IOException("the download is too big");
                using (var s = resp.GetResponseStream()) using (var fs = File.Create(path))
                {
                    var buf = new byte[81920]; long total = 0; int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > MaxDownload) throw new IOException("the download is too big");
                        fs.Write(buf, 0, n);
                    }
                }
            }
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
        static bool Bool(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
    }
}
