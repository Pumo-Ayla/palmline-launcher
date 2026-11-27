using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

internal static class Backdrop
{
    [StructLayout(LayoutKind.Sequential)]
    struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Auto)]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    static Size FrameSize(Size work, Size design) {
        // The reference window occupies 2030 x 1190 pixels of a 3840 x 2076 work area.
        double scale = Math.Min(work.Width * (2030.0 / 3840) / design.Width,
                                work.Height * (1190.0 / 2076) / design.Height);
        return new Size(design.Width * scale, design.Height * scale);
    }

    internal static System.Drawing.Rectangle MonitorWorkArea(IntPtr hwnd) {
        var info=new MonitorInfo { Size=Marshal.SizeOf(typeof(MonitorInfo)) };
        if(!GetMonitorInfo(MonitorFromWindow(hwnd,2),ref info)) return System.Drawing.Rectangle.Empty;
        return System.Drawing.Rectangle.FromLTRB(info.Work.Left,info.Work.Top,info.Work.Right,info.Work.Bottom);
    }

    public static void Attach(Window window) {
        var shell = (FrameworkElement)window.FindName("ShellGlass");
        var design = new Size(shell.Width, shell.Height);
        window.SourceInitialized += delegate {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            // Set the launcher's logical frame once. A game can change the desktop
            // mode temporarily while this window is still open.
            var area=MonitorWorkArea(source.Handle);
            if(!area.IsEmpty) {
                var work = new Size(area.Width, area.Height);
                var pixels = FrameSize(work, design);
                var logical = source.CompositionTarget.TransformFromDevice.Transform(new Vector(pixels.Width,pixels.Height));
                window.Width = logical.X;
                window.Height = logical.Y;
                int x = area.Left + (int)Math.Round((work.Width-pixels.Width)/2);
                int y = area.Top + (int)Math.Round((work.Height-pixels.Height)/2);
                SetWindowPos(source.Handle, IntPtr.Zero, x, y, 0, 0, 0x15); // NOSIZE | NOZORDER | NOACTIVATE
            }
            source.AddHook(delegate(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) {
                if (msg == 0x112 && (wp.ToInt64() & 0xfff0) == 0xf030) { // SC_MAXIMIZE
                    handled = true;
                    return IntPtr.Zero;
                }
                if (msg != 0x84) return IntPtr.Zero; // WM_NCHITTEST
                long pos = lp.ToInt64();
                var at = window.PointFromScreen(new Point((short)pos, (short)(pos >> 16)));
                if (window.TranslatePoint(at,shell).Y >= 62) return IntPtr.Zero;
                var hit = window.InputHitTest(at);
                if (hit != null && WindowChrome.GetIsHitTestVisibleInChrome(hit)) return IntPtr.Zero;
                handled = true;
                return new IntPtr(2); // HTCAPTION for dragging the title area.
            });
        };
    }
}

