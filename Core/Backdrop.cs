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

    static Windows.UI.ViewManagement.UISettings? ui;
    static bool? available;

    /// <summary>
    /// Whether Windows will really blur. With "Transparency effects" off (Settings › Personalisation › Colours),
    /// energy/battery saver on, or a basic display driver, the blur layer is drawn as its plain black fill instead,
    /// so glass looks fall back to a thicker see-through tint with no blur layer.
    /// </summary>
    public static bool Available
    {
        get
        {
            if (available is bool known) return known;
            bool on = Environment.GetEnvironmentVariable("BRIM_NOBLUR") != "1"; // dev: see the fallback
            if (!on) { available = false; return false; }
            try { ui ??= new Windows.UI.ViewManagement.UISettings(); on = ui.AdvancedEffectsEnabled; }
            catch
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    on = k?.GetValue("EnableTransparency") is not int v || v != 0;
                }
                catch { }
            }
            available = on;
            return on;
        }
    }

    /// <summary>Calls <paramref name="changed"/> (on the UI thread) when blur turns on or off, e.g. energy saver kicking in.</summary>
    public static void Watch(Action changed)
    {
        bool was = Available;
        Log.Note("glass", $"Windows blur {(was ? "on" : "off (transparency effects or energy saver): glass uses a plain tint")}");
        var d = System.Windows.Application.Current.Dispatcher;
        void Recheck() => d.BeginInvoke(new Action(() =>
        {
            available = null;
            if (Available == was) return;
            was = Available;
            Log.Note("glass", $"Windows blur turned {(was ? "on" : "off")}");
            changed();
        }));
        try { if (ui != null) ui.AdvancedEffectsEnabledChanged += (_, _) => Recheck(); } catch { }
        // Also on Windows' "a setting changed" broadcast, in case the event above doesn't reach a desktop app
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Recheck();
    }

    /// <summary>The tint a glass look uses: see-through over the blur, or thick enough to read on its own without it.</summary>
    public static System.Windows.Media.Color Tint(System.Windows.Media.Color c, byte alpha) =>
        System.Windows.Media.Color.FromArgb(Available ? alpha : Math.Max(alpha, (byte)0xD0), c.R, c.G, c.B);

    /// <summary>
    /// The glass fill: the see-through tint with a soft light falling in from the lit edge (the top, or the bottom for
    /// shapes hanging from the screen top), which is what makes it read as a pane of glass rather than tinted plastic.
    /// Without Windows' blur it's the plain thick tint.
    /// </summary>
    public static System.Windows.Media.Brush Glass(System.Windows.Media.Color c, byte alpha, bool litFromBelow = false)
    {
        var tint = Tint(c, alpha);
        if (!Available) { var flat = new System.Windows.Media.SolidColorBrush(tint); flat.Freeze(); return flat; }
        // A light tint is already pale: there the light shows as the glass getting a little more frosted
        bool light = ThemeManager.IsLight(c);
        System.Windows.Media.Color Sheen(double amount, int extraAlpha)
        {
            byte Mix(byte v) => (byte)Math.Round(v + (255 - v) * amount);
            return System.Windows.Media.Color.FromArgb((byte)Math.Min(255, tint.A + extraAlpha), Mix(tint.R), Mix(tint.G), Mix(tint.B));
        }
        var g = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, litFromBelow ? 1 : 0),
            EndPoint = new System.Windows.Point(0, litFromBelow ? 0 : 1),
        };
        g.GradientStops.Add(new System.Windows.Media.GradientStop(Sheen(light ? 0.3 : 0.10, light ? 0x18 : 0x0C), 0));
        g.GradientStops.Add(new System.Windows.Media.GradientStop(Sheen(light ? 0.1 : 0.03, light ? 0x06 : 0x03), 0.45));
        g.GradientStops.Add(new System.Windows.Media.GradientStop(tint, 1));
        g.Freeze();
        return g;
    }

    /// <summary>The edge of a pane of glass: bright where the light hits, fading away from it.</summary>
    public static System.Windows.Media.Brush Rim(bool litFromBelow = false)
    {
        var g = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, litFromBelow ? 1 : 0),
            EndPoint = new System.Windows.Point(0, litFromBelow ? 0 : 1),
        };
        g.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0x66, 255, 255, 255), 0));
        g.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0x24, 255, 255, 255), 0.35));
        g.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0x14, 255, 255, 255), 1));
        g.Freeze();
        return g;
    }

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
