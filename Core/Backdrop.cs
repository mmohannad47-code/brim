using System;
using System.Runtime.InteropServices;

namespace Brim.Core;

/// <summary>
/// A plain window that asks Windows to blur whatever is behind it, clipped to a rounded shape.
/// Brim's see-through windows (notch, widgets) can't blur by themselves, so one of these sits exactly
/// underneath and follows them, which is how the glass looks are made.
/// </summary>
public sealed class Backdrop : IDisposable
{
    static Native.WndProcDelegate? proc;
    static bool registered;
    const string ClassName = "BrimBackdrop";

    readonly IntPtr hwnd;
    int lx, ly, lw, lh, lr;
    bool lsq, shown;

    [StructLayout(LayoutKind.Sequential)]
    struct ACCENTPOLICY { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINCOMPATTRDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WINCOMPATTRDATA d);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);

    public Backdrop()
    {
        var hInst = Native.GetModuleHandle(null);
        if (!registered)
        {
            proc = Native.DefWindowProc;
            var wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
                hInstance = hInst,
                hbrBackground = GetStockObject(4 /* BLACK_BRUSH: reads as fully transparent to the compositor */),
                lpszClassName = ClassName,
            };
            Native.RegisterClassEx(ref wc);
            registered = true;
        }
        hwnd = Native.CreateWindowEx((uint)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | 0x20 /* TRANSPARENT */),
            ClassName, "", Native.WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

        // Windows ignores window regions for blur, but rounds the corners when asked (Windows 11).
        // Also drop the thin outline Windows draws around rounded windows.
        SetCorners(true);
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        Native.DwmSetWindowAttribute(hwnd, 34 /* BORDER_COLOR */, ref noBorder, 4);
        var accent = new ACCENTPOLICY { AccentState = 3 /* BLURBEHIND */ };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WINCOMPATTRDATA { Attribute = 19 /* ACCENT_POLICY */, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void SetCaptureHidden(bool hidden) => Native.SetWindowDisplayAffinity(hwnd, hidden ? 0x11u : 0u);

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);

    bool? rounded;

    void SetCorners(bool round)
    {
        if (rounded == round) return;
        rounded = round;
        int pref = round ? 2 /* ROUND */ : 1 /* DONOTROUND */;
        Native.DwmSetWindowAttribute(hwnd, 33 /* WINDOW_CORNER_PREFERENCE */, ref pref, 4);
    }

    /// <summary>
    /// Place the blur directly under <paramref name="owner"/>, in screen pixels. Windows' own corner rounding is
    /// tighter than Brim's shapes, so the blur is tucked just inside the rounded shape; for shapes hanging from the
    /// top edge it runs up past the screen top instead, so no corner shows there.
    /// </summary>
    public void Place(IntPtr owner, int x, int y, int w, int h, int radius, bool squareTop, bool force = false)
    {
        if (w <= 0 || h <= 0) { Hide(); return; }
        bool shapeChanged = w != lw || h != lh || radius != lr || squareTop != lsq;
        if (!force && shown && !shapeChanged && x == lx && y == ly) return;
        lx = x; ly = y; lw = w; lh = h; lr = radius; lsq = squareTop;

        if (radius <= 1) SetCorners(false);
        else
        {
            SetCorners(true);
            int sys = (int)Math.Round(8 * GetDpiForWindow(hwnd) / 96.0);
            int inset = radius > sys ? (int)Math.Ceiling((radius - sys) * 0.3) + 1 : 0;
            x += inset; w -= 2 * inset; h -= inset;
            if (squareTop) { y -= sys; h += sys; }
            else { y += inset; h -= inset; }
        }
        if (w <= 0 || h <= 0) { Hide(); return; }
        Native.SetWindowPos(hwnd, owner, x, y, w, h, Native.SWP_NOACTIVATE | 0x40 /* SHOWWINDOW */);
        shown = true;
    }

    public void Hide()
    {
        if (!shown) return;
        Native.ShowWindow(hwnd, Native.SW_HIDE);
        shown = false;
    }

    public void Dispose()
    {
        if (hwnd != IntPtr.Zero) Native.DestroyWindow(hwnd);
    }
}
