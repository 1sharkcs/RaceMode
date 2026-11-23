using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RaceMode
{
    // Maximised, a window with its own title bar (WindowStyle None) would cover the whole screen, taskbar
    // included. Windows asks every window how big "maximised" is (WM_GETMINMAXINFO): the answer here is the free
    // area of the monitor the window is on, so the taskbar stays visible wherever it is (bottom, top, left, right,
    // any monitor). An auto-hidden taskbar gets a 2 px strip, so moving the mouse to that edge still shows it.
    public partial class Shell
    {
        [StructLayout(LayoutKind.Sequential)] struct PointI { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RectI { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo { public PointI Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfoEx { public int Size; public RectI Monitor, Work; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] struct AppBarData { public int Size; public IntPtr Hwnd; public uint Message, Edge; public RectI Rect; public IntPtr Param; }

        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
        [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

        const int WmGetMinMaxInfo = 0x0024;

        // The free height (taskbar left out) of the screen the window is on, in the window's own units; 0 if unknown.
        // The layout test sets TestRoomH to try a short screen on any PC.
        internal static double TestRoomH;
        double ScreenRoomH()
        {
            if (TestRoomH > 0) return TestRoomH;
            try
            {
                var hwnd = new WindowInteropHelper(Window).Handle;
                if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea.Height;
                var monitor = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
                var mi = new MonitorInfoEx { Size = Marshal.SizeOf(typeof(MonitorInfoEx)) };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref mi)) return SystemParameters.WorkArea.Height;
                var src = PresentationSource.FromVisual(Window);
                double scale = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice.M22 : 1;
                return (mi.Work.Bottom - mi.Work.Top) * scale;
            }
            catch { return 0; }
        }

        void HookMaximiseFit()
        {
            Window.SourceInitialized += (s, e) =>
            {
                var src = HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
                if (src != null) src.AddHook(FitMaximised);
            };
        }

        // Fills in the maximised size and position only; WPF still sets the minimum size (MinWidth/MinHeight) after us.
        static IntPtr FitMaximised(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmGetMinMaxInfo) return IntPtr.Zero;
            try
            {
                var monitor = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
                var mi = new MonitorInfoEx { Size = Marshal.SizeOf(typeof(MonitorInfoEx)) };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref mi)) return IntPtr.Zero;
                var work = mi.Work;
                if (work.Left == mi.Monitor.Left && work.Top == mi.Monitor.Top && work.Right == mi.Monitor.Right && work.Bottom == mi.Monitor.Bottom)
                {
                    // no room reserved: the taskbar may be auto-hidden on this monitor's edge
                    var abd = new AppBarData { Size = Marshal.SizeOf(typeof(AppBarData)) };
                    bool autoHide = ((long)SHAppBarMessage(4, ref abd) & 1) != 0;   // ABM_GETSTATE, ABS_AUTOHIDE
                    abd = new AppBarData { Size = Marshal.SizeOf(typeof(AppBarData)) };
                    if (autoHide && SHAppBarMessage(5, ref abd) != IntPtr.Zero)   // ABM_GETTASKBARPOS
                    {
                        var r = abd.Rect;
                        bool onThisMonitor = r.Right > mi.Monitor.Left && r.Left < mi.Monitor.Right && r.Bottom > mi.Monitor.Top && r.Top < mi.Monitor.Bottom;
                        if (onThisMonitor)
                            switch (abd.Edge) { case 0: work.Left += 2; break; case 1: work.Top += 2; break; case 2: work.Right -= 2; break; default: work.Bottom -= 2; break; }
                    }
                }
                var mmi = (MinMaxInfo)Marshal.PtrToStructure(lParam, typeof(MinMaxInfo));
                mmi.MaxPosition.X = work.Left - mi.Monitor.Left;
                mmi.MaxPosition.Y = work.Top - mi.Monitor.Top;
                mmi.MaxSize.X = work.Right - work.Left;
                mmi.MaxSize.Y = work.Bottom - work.Top;
                // On a monitor bigger than the primary one, Windows takes a maximised size larger than the primary
                // screen to be meant for the primary screen and adds the difference: 2560 wide became 3200, spilling
                // onto the next monitor. The most the window may be dragged or maximised to caps that: no smaller
                // than this monitor's free area (so it can still fill it), no bigger than the size just asked for.
                mmi.MaxTrackSize.X = mmi.MaxSize.X;
                mmi.MaxTrackSize.Y = mmi.MaxSize.Y;
                Marshal.StructureToPtr(mmi, lParam, false);
            }
            catch { }
            return IntPtr.Zero;
        }
    }
}
