using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Windows;

namespace RaceMode
{
    // OBS Studio integration, part 1: its built-in remote control (obs-websocket) settings.
    // OBS keeps them in %APPDATA%\obs-studio\plugin_config\obs-websocket\config.json and rewrites that file when
    // it exits, so Race Mode only edits it while OBS is closed, keeps a backup, and never changes the password
    // if one is already set. Race Mode reads the password from that same file when it connects (it never stores
    // its own copy), and never writes it to a log.
    public static partial class Obs
    {
        public static string ExePath
        {
            get
            {
                // Registry (Uninstall / App Paths) first, then the usual Program Files and Steam folders. Null if not installed.
                return InstallFinder.ObsExe();
            }
        }

        public static bool Installed { get { return ExePath != null; } }

        public static string ConfigPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"obs-studio\plugin_config\obs-websocket\config.json"); }
        }

        public static bool Running()
        {
            var ps = Process.GetProcessesByName("obs64");
            bool r = ps.Length > 0; foreach (var p in ps) p.Dispose(); return r;
        }

        public class WsSettings { public bool Found, Enabled, AuthRequired, HasPassword; public int Port = 4455; }

        public static WsSettings ReadSettings()
        {
            var s = new WsSettings();
            try
            {
                if (!File.Exists(ConfigPath)) return s;
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigPath)) as Dictionary<string, object>;
                if (d == null) return s;
                s.Found = true;
                object v;
                if (d.TryGetValue("server_enabled", out v) && v is bool) s.Enabled = (bool)v;
                if (d.TryGetValue("auth_required", out v) && v is bool) s.AuthRequired = (bool)v;
                if (d.TryGetValue("server_port", out v) && v is int) s.Port = (int)v;
                if (d.TryGetValue("server_password", out v) && v is string) s.HasPassword = ((string)v).Length > 0;
            }
            catch { }
            return s;
        }

        // The password OBS asks for (null if none). Read when connecting; never kept or logged.
        internal static string ReadPassword()
        {
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigPath)) as Dictionary<string, object>;
                object v;
                return d != null && d.TryGetValue("server_password", out v) ? v as string : null;
            }
            catch { return null; }
        }

        // Switches OBS's remote control on (port 4455, password required). Returns null when done, or why not.
        public static string EnableRemoteControl() { return EnableRemoteControl(ConfigPath, Running()); }

        public static string EnableRemoteControl(string path, bool obsRunning)
        {
            if (obsRunning) return "Close OBS first: it saves its settings when it closes, which would undo this";
            try
            {
                Dictionary<string, object> d = null;
                if (File.Exists(path))
                {
                    d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                    if (d == null) return "OBS's remote control settings file couldn't be read, so it was left alone";
                    if (!File.Exists(path + ".racemode.bak")) File.Copy(path, path + ".racemode.bak");   // keep the user's original, not our own later edit
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    d = new Dictionary<string, object> { { "alerts_enabled", false }, { "first_load", false }, { "server_port", 4455 } };
                }
                object pw;
                if (!d.TryGetValue("server_password", out pw) || !(pw is string) || ((string)pw).Length == 0) d["server_password"] = NewPassword();
                d["server_enabled"] = true;
                d["auth_required"] = true;
                if (!d.ContainsKey("server_port")) d["server_port"] = 4455;
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(d));
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                return null;
            }
            catch (Exception ex) { return "Couldn't change OBS's settings: " + ex.Message; }
        }

        static string NewPassword()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            var b = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return new string(b.Select(x => chars[x % chars.Length]).ToArray());
        }

        // First-run "Helpers" page: the OBS part (null when OBS isn't installed).
        public static Func<Shell, UIElement> SetupStatus()
        {
            if (!Installed) return null;
            return shell =>
            {
                var s = ReadSettings();
                if (s.Enabled)
                    return SetupFlow.Status("OBS shares its stream stats", "Race Mode can show dropped frames and bitrate while you stream.", true);
                string err = null;
                var panel = SetupFlow.Stack(SetupFlow.Status("Let Race Mode read OBS's stream stats?",
                    "This switches on OBS's built-in remote control, protected by a long random password. Other devices on your network can reach it only with that password; Race Mode itself connects from this PC. It's how Race Mode sees dropped frames and bitrate. OBS must be closed.", null));
                panel.Children.Add(SetupFlow.Button("Turn It On", () =>
                {
                    err = EnableRemoteControl();
                    Installer.Log("obs", new[] { err ?? "OBS remote control switched on" });
                    MessageBox.Show(err ?? "Done. OBS's remote control is on.", "Race Mode", MessageBoxButton.OK, err == null ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }));
                return panel;
            };
        }

        public static string UninstallNote()
        {
            var s = ReadSettings();
            return s.Enabled ? "\n\nOBS's remote control is still on. To switch it off: in OBS, Tools > WebSocket Server Settings, untick Enable WebSocket server." : "";
        }
    }
}
