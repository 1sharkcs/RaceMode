using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using Forms = System.Windows.Forms;

namespace RaceMode
{
    // The tray icon: closing the window keeps Race Mode running in the system tray (if "Keep running in the tray" is on).
    // Right-click: Open, Turn On/Off, Quit. The icon is created the first time it's needed, so a window that's
    // never closed never loads Windows Forms.
    public partial class Shell
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
        Forms.NotifyIcon _tray;
        Forms.ToolStripMenuItem _trayToggle;
        bool _quitting;

        // False in test and snapshot modes, which must really close. Set by Program.Run.
        public bool TrayAllowed;
        public bool TrayOnClose { get { return TrayAllowed && _cfg.TrayOnClose; } }

        // Hooked first in the constructor, so a cancelled close never reaches the other Closing handlers' cleanup.
        void HookTrayClose()
        {
            Window.Closing += (s, e) =>
            {
                if (_quitting || !TrayAllowed || !_cfg.TrayOnClose) return;
                e.Cancel = true;
                HideToTray(true);
            };
            Window.Closed += (s, e) => { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } };
            Get<ToggleButton>("Switch").Checked += (s, e) => UpdateTrayText();
            Get<ToggleButton>("Switch").Unchecked += (s, e) => UpdateTrayText();
        }

        // Quit for real: the normal shutdown (Race Mode turned off first if it's on, settings saved).
        public void PrepareQuit() { _quitting = true; }

        public void Quit()
        {
            _quitting = true;
            Window.Close();
        }

        public void HideToTray(bool hint)
        {
            EnsureTray();
            Window.Hide();
            // Hidden, the window draws nothing: let Windows page out what it used (it comes back when the window opens).
            try { GC.Collect(); GC.WaitForPendingFinalizers(); SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch { }
            if (hint && !_cfg.TrayHintShown)
            {
                _cfg.TrayHintShown = true;
                SaveConfig();
                try { _tray.ShowBalloonTip(5000, "Race Mode is still running", "It's in the system tray, next to the clock. Right-click its icon to turn it on or off, or to quit.", Forms.ToolTipIcon.Info); } catch { }
            }
        }

        public void ShowFromTray()
        {
            if (!Window.IsVisible) Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate(); Window.Topmost = true; Window.Topmost = false; Window.Focus();
        }

        void EnsureTray()
        {
            if (_tray != null) return;
            _tray = new Forms.NotifyIcon();
            try { _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); } catch { _tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new Forms.ContextMenuStrip();
            var open = new Forms.ToolStripMenuItem("Open Race Mode");
            open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
            open.Click += (s, e) => Window.Dispatcher.BeginInvoke(new Action(ShowFromTray));
            _trayToggle = new Forms.ToolStripMenuItem("Turn On");
            _trayToggle.Click += (s, e) => Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var sw = Get<ToggleButton>("Switch");
                if (sw != null && sw.IsEnabled) sw.IsChecked = sw.IsChecked != true;
            }));
            var quit = new Forms.ToolStripMenuItem("Quit Race Mode");
            quit.Click += (s, e) => Window.Dispatcher.BeginInvoke(new Action(Quit));
            menu.Items.Add(open);
            menu.Items.Add(_trayToggle);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(quit);
            menu.Opening += (s, e) => UpdateTrayText();
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => Window.Dispatcher.BeginInvoke(new Action(ShowFromTray));
            UpdateTrayText();
            _tray.Visible = true;
        }

        void UpdateTrayText()
        {
            if (_tray == null) return;
            bool on = Get<ToggleButton>("Switch").IsChecked == true;
            _tray.Text = on ? "Race Mode: on" : "Race Mode: off";
            if (_trayToggle != null) _trayToggle.Text = on ? "Turn Off" : "Turn On";
        }
    }
}
