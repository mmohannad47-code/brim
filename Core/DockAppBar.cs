using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Brim.Core;

/// <summary>
/// Reserves a strip of the screen for the dock (Windows' "app bar"), so maximised windows stop above it.
/// Windows sends app-bar requests to the first window called Shell_TrayWnd. While the dock replaces the taskbar,
/// that's Brim's own tray window (it collects the tray icons), and Explorer, which owns the work area, never
/// acts on a request passed along from there. So for these calls Brim's tray window steps behind Explorer's.
/// </summary>
static class DockAppBar
{
    /// <summary>Brim's Shell_TrayWnd windows (any Brim process), which would otherwise catch app-bar requests.</summary>
    static List<IntPtr> BrimTrays()
    {
        var mine = new HashSet<uint>(System.Diagnostics.Process.GetProcessesByName("Brim").Select(p => (uint)p.Id));
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.ClassOf(h) == "Shell_TrayWnd")
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (mine.Contains(pid)) list.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static T WithExplorer<T>(bool direct, Func<T> call)
    {
        var trays = direct ? BrimTrays() : new List<IntPtr>();
        foreach (var t in trays)
        {
            Native.SetWindowPos(t, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Native.SetWindowPos(t, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        try { return call(); }
        finally
        {
            foreach (var t in trays)
                Native.SetWindowPos(t, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>Register (if needed) and reserve <paramref name="px"/> pixels along one edge of the monitor (the bottom, or a side).</summary>
    public static string Reserve(IntPtr hwnd, int px, bool direct, uint callback = 0, Native.RECT? monitor = null, bool alreadyRegistered = false,
                                 uint edge = Native.ABE_BOTTOM)
    {
        return WithExplorer(direct, () =>
        {
            var mon = monitor ?? Native.ScreenOrPrimary("").Bounds;
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = hwnd, uCallbackMessage = callback };
            string log = "";
            if (!alreadyRegistered) log += "new=" + Native.SHAppBarMessage(Native.ABM_NEW, ref abd).ToUInt32() + " ";
            abd.uEdge = edge;
            abd.rc = edge switch
            {
                Native.ABE_LEFT => new Native.RECT { Left = mon.Left, Right = mon.Left + px, Top = mon.Top, Bottom = mon.Bottom },
                Native.ABE_RIGHT => new Native.RECT { Left = mon.Right - px, Right = mon.Right, Top = mon.Top, Bottom = mon.Bottom },
                _ => new Native.RECT { Left = mon.Left, Right = mon.Right, Top = mon.Bottom - px, Bottom = mon.Bottom },
            };
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
            // Windows may move it inwards (another bar on that edge): keep the size we asked for from there
            if (edge == Native.ABE_LEFT) abd.rc.Right = abd.rc.Left + px;
            else if (edge == Native.ABE_RIGHT) abd.rc.Left = abd.rc.Right - px;
            else abd.rc.Top = abd.rc.Bottom - px;
            Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);
            return log + $"setpos rect {abd.rc.Left},{abd.rc.Top},{abd.rc.Right},{abd.rc.Bottom}";
        });
    }

    public static string Remove(IntPtr hwnd, bool direct)
    {
        return WithExplorer(direct, () =>
        {
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = hwnd };
            return "remove=" + Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd).ToUInt32();
        });
    }
}
