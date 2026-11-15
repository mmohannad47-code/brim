using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace Brim.Core;

/// <summary>
/// The keyboard language you're typing in, for the dock ("ENG", "ARA"…, like Windows' taskbar). Windows keeps one
/// per app, so it's read from the window you're using, and switching asks that window to change.
/// </summary>
public static class KeyboardLanguage
{
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? list);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);

    public sealed record Layout(IntPtr Hkl, string Short, string Region, string Name);

    /// <summary>Every keyboard language set up in Windows, in Windows' own order.</summary>
    public static List<Layout> All()
    {
        int n = GetKeyboardLayoutList(0, null);
        if (n <= 0) return new();
        var list = new IntPtr[n];
        n = GetKeyboardLayoutList(n, list);
        var layouts = list.Take(n).Select(Describe).ToList();
        // Two of the same language (English US and English UK): the region tells them apart, like Windows does
        return layouts.Select(l => layouts.Count(o => o.Short == l.Short) > 1 ? l : l with { Region = "" }).ToList();
    }

    /// <summary>The window you're typing in.</summary>
    static IntPtr Typing()
    {
        var fg = Native.GetForegroundWindow();
        if (Native.ClassOf(fg) == "ApplicationFrameWindow" && FindWindowEx(fg, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null) is var core && core != IntPtr.Zero)
            return core; // Store apps: the real app sits inside the frame
        return fg;
    }

    /// <summary>The language of the window you're using now (null if it can't be told).</summary>
    public static Layout? Current()
    {
        var hwnd = Typing();
        if (hwnd == IntPtr.Zero) return null;
        var hkl = GetKeyboardLayout(GetWindowThreadProcessId(hwnd, IntPtr.Zero));
        if (hkl == IntPtr.Zero) return null;
        var all = All();
        return all.FirstOrDefault(l => l.Hkl == hkl) ?? all.FirstOrDefault(l => Lang(l.Hkl) == Lang(hkl)) ?? Describe(hkl);
    }

    /// <summary>Switch the window you're using to this language (the window that was in front when you clicked).</summary>
    public static void Switch(IntPtr window, IntPtr hkl)
    {
        if (window == IntPtr.Zero) window = Typing();
        Native.PostMessage(window, 0x0050 /* WM_INPUTLANGCHANGEREQUEST */, IntPtr.Zero, hkl);
    }

    /// <summary>The next language after the current one (what clicking the dock's language does).</summary>
    public static IntPtr Next(Layout? current)
    {
        var all = All();
        if (all.Count == 0) return IntPtr.Zero;
        int i = current == null ? -1 : all.FindIndex(l => l.Hkl == current.Hkl);
        return all[(i + 1) % all.Count].Hkl;
    }

    public static IntPtr Window => Typing();

    static int Lang(IntPtr hkl) => (int)(hkl.ToInt64() & 0xFFFF);

    static Layout Describe(IntPtr hkl)
    {
        try
        {
            var c = CultureInfo.GetCultureInfo(Lang(hkl));
            string region = "";
            try { region = new RegionInfo(c.Name).TwoLetterISORegionName; } catch { }
            if (region == "GB") region = "UK"; // as Windows writes it
            return new Layout(hkl, c.ThreeLetterISOLanguageName.ToUpperInvariant(), region, c.DisplayName);
        }
        catch { return new Layout(hkl, "?", "", "Keyboard " + Lang(hkl).ToString("X4")); }
    }
}
