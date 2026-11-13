using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Brim.Core;

/// <summary>
/// Burn-in protection for OLED screens: nudges Brim's always-visible pieces by a few pixels over time,
/// dims them when you're away, and can black the screen out after a while (unless something like a
/// video player is asking Windows to keep the display on).
/// </summary>
public static class OledCare
{
    public static event Action<double, double>? Shift;  // dx, dy in pixels
    public static event Action<bool>? Dim;              // true = dim

    static readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    static int step;
    static DateTime lastShift = DateTime.Now;
    static bool dimmed;
    static Window? blackout;

    // A slow walk around a small square, so every pixel spends time off the "hot" spot
    static readonly (double x, double y)[] Path =
    {
        (0, 0), (2, 0), (3, 2), (2, 3), (0, 3), (-2, 3), (-3, 2), (-2, 0), (-3, -2), (-2, -3), (0, -3), (2, -3), (3, -2),
    };

    static AppSettings S => SettingsStore.Current;

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public int cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr input, uint inLen, out uint output, uint outLen);

    public static TimeSpan Idle
    {
        get
        {
            var i = new LASTINPUTINFO { cbSize = Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref i) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - i.dwTime)) : TimeSpan.Zero;
        }
    }

    /// <summary>True while some app (a video player, a presentation) is asking Windows to keep the display on.</summary>
    static bool DisplayRequired()
    {
        try { return CallNtPowerInformation(16 /* SystemExecutionState */, IntPtr.Zero, 0, out uint state, 4) == 0 && (state & 0x2 /* DISPLAY_REQUIRED */) != 0; }
        catch { return false; }
    }

    public static void Start()
    {
        timer.Tick -= Tick;
        timer.Tick += Tick;
        Apply();
    }

    /// <summary>Called when settings change.</summary>
    public static void Apply()
    {
        if (S.OledCare) { if (!timer.IsEnabled) timer.Start(); }
        else
        {
            timer.Stop();
            if (step != 0) { step = 0; Shift?.Invoke(0, 0); }
            if (dimmed) { dimmed = false; Dim?.Invoke(false); }
            EndBlackout();
        }
        if (S.OledCare && !S.OledShift && step != 0) { step = 0; Shift?.Invoke(0, 0); }
    }

    static void Tick(object? s, EventArgs e)
    {
        var idle = Idle;

        if (S.OledShift && DateTime.Now - lastShift > TimeSpan.FromMinutes(3))
        {
            lastShift = DateTime.Now;
            step = (step + 1) % Path.Length;
            Shift?.Invoke(Path[step].x, Path[step].y);
        }

        bool wantDim = S.OledDimMinutes > 0 && idle > TimeSpan.FromMinutes(S.OledDimMinutes);
        if (wantDim != dimmed) { dimmed = wantDim; Dim?.Invoke(wantDim); }

        if (blackout != null)
        {
            if (idle < TimeSpan.FromSeconds(2)) EndBlackout();
        }
        else if (S.OledBlackMinutes > 0 && idle > TimeSpan.FromMinutes(S.OledBlackMinutes) && !DisplayRequired())
            StartBlackout();
    }

    static void StartBlackout()
    {
        var mon = Native.PrimaryMonitorRect();
        blackout = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Background = Brushes.Black,
            Cursor = Cursors.None,
            Left = SystemParameters.VirtualScreenLeft,
            Top = SystemParameters.VirtualScreenTop,
            Width = SystemParameters.PrimaryScreenWidth,
            Height = SystemParameters.PrimaryScreenHeight,
            Title = "Brim OLED rest",
        };
        blackout.MouseMove += (_, _) => { if (Idle < TimeSpan.FromSeconds(1)) EndBlackout(); };
        blackout.KeyDown += (_, _) => EndBlackout();
        blackout.MouseDown += (_, _) => EndBlackout();
        blackout.Show();
        blackout.Left = 0;
        blackout.Top = 0;
    }

    static void EndBlackout()
    {
        var b = blackout;
        blackout = null;
        b?.Close();
    }
}
