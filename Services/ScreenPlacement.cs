using System.Runtime.InteropServices;

namespace MultiSSH.Services;

/// <summary>A rectangle in physical screen pixels (left/top inclusive, right/bottom exclusive).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public long OverlapArea(PixelRect o)
    {
        long w = Math.Min(Right, o.Right) - Math.Max(Left, o.Left);
        long h = Math.Min(Bottom, o.Bottom) - Math.Max(Top, o.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }
}

/// <summary>
/// Keeps the main window on a real monitor at startup. The saved position is
/// only trusted when the window sits inside one monitor's work area; anywhere
/// else (an unplugged screen, or a gap between monitors of different sizes
/// that the bounding "virtual screen" still covers) it is pulled back onto the
/// monitor it overlaps most, or the primary one if it overlaps none.
/// </summary>
public static class ScreenPlacement
{
    /// <summary>
    /// Windows 10/11 frames carry ~7px invisible resize borders, so a window
    /// snapped flush to a screen edge reports a rect slightly past it.
    /// </summary>
    public const int EdgeTolerance = 10;

    /// <summary>
    /// Returns where the window should be: <paramref name="win"/> itself when it
    /// is already fully on one monitor, otherwise a rect moved (and shrunk if it
    /// can't fit) inside the best monitor's work area. Index 0 of
    /// <paramref name="workAreas"/> must be the primary monitor.
    /// </summary>
    public static PixelRect EnsureVisible(PixelRect win, IReadOnlyList<PixelRect> workAreas)
    {
        if (workAreas.Count == 0) return win;

        foreach (var wa in workAreas)
            if (win.Left >= wa.Left - EdgeTolerance && win.Top >= wa.Top - EdgeTolerance &&
                win.Right <= wa.Right + EdgeTolerance && win.Bottom <= wa.Bottom + EdgeTolerance)
                return win;

        var target = workAreas[0];
        long best = 0;
        foreach (var wa in workAreas)
        {
            long a = win.OverlapArea(wa);
            if (a > best) { best = a; target = wa; }
        }

        int w = Math.Min(win.Width, target.Width);
        int h = Math.Min(win.Height, target.Height);
        int left = Math.Clamp(win.Left, target.Left, target.Right - w);
        int top = Math.Clamp(win.Top, target.Top, target.Bottom - h);
        return new PixelRect(left, top, left + w, top + h);
    }

    /// <summary>Move/resize <paramref name="hwnd"/> onto a monitor if it isn't fully on one.</summary>
    public static void EnsureWindowVisible(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;

        var areas = new List<PixelRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi))
            {
                var wa = new PixelRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
                if ((mi.dwFlags & MONITORINFOF_PRIMARY) != 0) areas.Insert(0, wa); else areas.Add(wa);
            }
            return true;
        }, IntPtr.Zero);

        var cur = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
        var fixedRect = EnsureVisible(cur, areas);
        if (fixedRect == cur) return;

        SetWindowPos(hwnd, IntPtr.Zero, fixedRect.Left, fixedRect.Top, fixedRect.Width, fixedRect.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private const uint MONITORINFOF_PRIMARY = 1;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
