using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Brim.Core;

/// <summary>
/// When should a live wallpaper move? Shared by Brim's own live wallpaper and Wallpaper Engine.
///   Desktop   – only while the desktop itself has focus (on any screen); paused behind any app, even a small one
///   Uncovered – paused only while a fullscreen or maximised window fills a screen
///   Always    – never paused
/// </summary>
public static class LivePolicy
{
    public static string Mode => Environment.GetEnvironmentVariable("BRIM_LIVE_ALWAYS") == "1" ? "Always" // developer checks
        : SettingsStore.Current.LivePlayMode is "Always" or "Uncovered" ? SettingsStore.Current.LivePlayMode : "Desktop";

    /// <summary>true = play, false = pause, null = leave as it is (Start, the dock, the notch, pop-ups…).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    /// <summary>Paused for power reasons (unplugged, or Battery saver on), if you asked for that.</summary>
    public static bool PowerSaysPause()
    {
        var s = SettingsStore.Current;
        if (!s.LivePauseOnBattery && !s.LivePauseOnSaver) return false;
        if (!GetSystemPowerStatus(out var p)) return false;
        bool hasBattery = p.BatteryFlag != 128 && p.BatteryFlag != 255;
        if (s.LivePauseOnBattery && hasBattery && p.ACLineStatus == 0) return true;
        if (s.LivePauseOnSaver && p.SystemStatusFlag == 1) return true;
        return false;
    }

    public static bool? ShouldPlay(int brimPid)
    {
        if (PowerSaysPause()) return false;
        switch (Mode)
        {
            case "Always": return true;
            case "Uncovered": return !Covered();
        }

        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return NoAppWindowsShowing() ? true : null;
        var cls = Native.ClassOf(fg);
        if (cls is "Progman" or "WorkerW") return true;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (WindowTracker.IsWallpaperApp(pid)) return true; // clicking a Wallpaper Engine desktop
        // The taskbar, Start, Search, Alt+Tab, Brim's dock, notch, widgets and pop-ups don't change anything,
        // except when there's nothing left on screen (you minimised the last window)
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow"
            or "TopLevelWindowForOverflowXamlIsland" or "MultitaskingViewFrame" or "ForegroundStaging")
            return NoAppWindowsShowing() ? true : null;
        if (!Native.IsWindowVisible(fg) || Native.IsIconic(fg)) return NoAppWindowsShowing() ? true : null;
        long ex = Native.GetWindowLongPtr(fg, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 || (ex & Native.WS_EX_NOACTIVATE) != 0)
            return NoAppWindowsShowing() ? true : null;
        return false; // an app (including Brim's Settings) has focus
    }

    /// <summary>No app window is open and un-minimised on any screen.</summary>
    static bool NoAppWindowsShowing()
    {
        foreach (var w in WindowTracker.TaskbarWindows())
            if (!Native.IsIconic(w.Hwnd)) return false;
        return true;
    }

    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);

    static bool Covered()
    {
        var fg = Native.GetForegroundWindow();
        return WindowTracker.ForegroundIsFullscreen() || fg != IntPtr.Zero && IsZoomed(fg) && Native.IsWindowVisible(fg) && !Native.IsIconic(fg);
    }

    // ---------- Wallpaper Engine follows the same rule (driven from Brim's main process) ----------

    static bool? weState;
    static DateTime weCheckedAt;
    static bool weRunning;

    public static void ApplyToWallpaperEngine()
    {
        try
        {
            if (Mode == "Always" && weState == null) return; // never touched it; leave Wallpaper Engine's own settings alone
            // Brim shows the wallpaper: Wallpaper Engine has nothing on screen to play or pause (and each command starts a process)
            if (SettingsStore.Current.WallpaperOwner == "Brim") { weState = null; return; }
            if (DateTime.Now - weCheckedAt > TimeSpan.FromSeconds(20))
            {
                weCheckedAt = DateTime.Now;
                bool was = weRunning;
                weRunning = WallpaperEngine.Running;
                if (!was && weRunning) weState = null; // it (re)started: send the current state again
            }
            if (!weRunning) { weState = null; return; }
            var play = ShouldPlay(Environment.ProcessId);
            if (play == null || play == weState) return;
            weState = play;
            WallpaperEngine.Control(play.Value ? "play" : "pause");
        }
        catch (Exception ex) { Log.Error("we policy", ex); }
    }
}
