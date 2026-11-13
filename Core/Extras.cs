using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Brim.Core;

/// <summary>Countdown for focus sessions; the closed notch shows it while it runs.</summary>
public static class FocusTimer
{
    public static event Action? Changed;
    public static event Action? Finished;
    public static TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(25);
    public static bool Running { get; private set; }
    static DateTime endsAt;
    static TimeSpan pausedLeft = TimeSpan.FromMinutes(25);
    static readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    static FocusTimer() => tick.Tick += (_, _) =>
    {
        if (Running && DateTime.Now >= endsAt)
        {
            Running = false;
            pausedLeft = Duration;
            tick.Stop();
            Chime();
            Finished?.Invoke();
        }
        Changed?.Invoke();
    };

    public static TimeSpan Left => Running ? (endsAt - DateTime.Now < TimeSpan.Zero ? TimeSpan.Zero : endsAt - DateTime.Now) : pausedLeft;
    public static bool Started => Running || pausedLeft != Duration;

    public static void Set(TimeSpan d)
    {
        Duration = d;
        Running = false;
        pausedLeft = d;
        tick.Stop();
        Changed?.Invoke();
    }

    public static void StartPause()
    {
        if (Running) { pausedLeft = Left; Running = false; tick.Stop(); }
        else { endsAt = DateTime.Now + pausedLeft; Running = true; tick.Start(); }
        Changed?.Invoke();
    }

    public static void Reset() => Set(Duration);

    public static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern bool PlaySound(string? sound, IntPtr mod, uint flags);
    static void Chime() => PlaySound("SystemNotification", IntPtr.Zero, 0x00010000 /* ALIAS */ | 0x0001 /* ASYNC */);
}

/// <summary>The colour of the pixel under the pointer.</summary>
public static class ColorPicker
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);

    public static (byte r, byte g, byte b) UnderPointer()
    {
        Native.GetCursorPos(out var p);
        var dc = GetDC(IntPtr.Zero);
        try
        {
            uint c = GetPixel(dc, p.X, p.Y);
            return ((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
}
