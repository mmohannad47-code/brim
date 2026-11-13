using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Brim.Core;

/// <summary>Controls the Windows taskbar: auto-hide, hide completely, and the Windows 11 clean-up switches.</summary>
public static class TaskbarManager
{
    static TaskbarMode applied = TaskbarMode.Windows;
    static DateTime peekUntil;
    static IntPtr lastPrimary;

    static IntPtr Primary => Native.ExplorerTray();

    /// <summary>Raised when Explorer restarts and the taskbar comes back as a new window.</summary>
    public static event Action? ExplorerRestarted;

    static List<IntPtr> AllBars()
    {
        var list = new List<IntPtr>();
        var p = Primary;
        if (p != IntPtr.Zero) list.Add(p);
        Native.EnumWindows((h, _) =>
        {
            if (Native.ClassOf(h) == "Shell_SecondaryTrayWnd") list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool GetAutoHide()
    {
        var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = Primary };
        return ((int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd).ToUInt32() & Native.ABS_AUTOHIDE) != 0;
    }

    static void SetAutoHide(bool on)
    {
        if (GetAutoHide() == on) return;
        var abd = new Native.APPBARDATA
        {
            cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
            hWnd = Primary,
            lParam = new IntPtr(on ? Native.ABS_AUTOHIDE : Native.ABS_ALWAYSONTOP),
        };
        Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
    }

    static void SetVisible(bool visible)
    {
        foreach (var h in AllBars())
            if (Native.IsWindowVisible(h) != visible) Native.ShowWindow(h, visible ? Native.SW_SHOWNA : Native.SW_HIDE);
    }

    /// <summary>Remember how the user had it before Brim touched anything.</summary>
    public static void Init()
    {
        var s = SettingsStore.Current;
        if (s.OriginalAutoHide == null)
        {
            s.OriginalAutoHide = GetAutoHide();
            SettingsStore.Save();
        }
        lastPrimary = Primary;
    }

    public static void Apply(TaskbarMode mode)
    {
        applied = mode;
        bool original = SettingsStore.Current.OriginalAutoHide ?? false;
        switch (mode)
        {
            case TaskbarMode.Windows:
                SetVisible(true);
                SetAutoHide(original);
                break;
            case TaskbarMode.AutoHide:
                SetVisible(true);
                SetAutoHide(true);
                break;
            case TaskbarMode.Dock:
                SetAutoHide(true);
                SetVisible(false);
                break;
        }
    }

    /// <summary>Called on a timer: Explorer likes to bring the taskbar back, and restarts create new windows.</summary>
    public static void Enforce()
    {
        var p = Primary;
        if (p != lastPrimary)
        {
            lastPrimary = p;
            Apply(applied);
            if (p != IntPtr.Zero) ExplorerRestarted?.Invoke();
            return;
        }
        if (applied != TaskbarMode.Dock) return;
        if (DateTime.Now < peekUntil) return;
        // Explorer brings its taskbar back for Start and some shortcuts; with the dock on, it always goes again
        if (Native.IsWindowVisible(p)) SetVisible(false);
    }

    /// <summary>Bring the real taskbar up for a moment, e.g. to reach tray icons.</summary>
    public static void Peek()
    {
        if (applied != TaskbarMode.Dock) return;
        peekUntil = DateTime.Now.AddSeconds(8);
        SetVisible(true);
        Native.TapKeys(Native.VK_LWIN, (byte)'B'); // Win+B: reveal the auto-hidden bar with focus on the tray
    }

    public static void RestoreAll()
    {
        try { Apply(TaskbarMode.Windows); } catch { }
    }

    // ---------- Windows 11 taskbar clean-up (registry, applied live by Explorer) ----------

    const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";

    static int? ReadDword(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name) is int v ? v : null;
    }

    static bool WriteDword(string key, string name, int value)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(key, true);
            k.SetValue(name, value, RegistryValueKind.DWord);
            Native.BroadcastSettingChange("TraySettings");
            return true;
        }
        catch (Exception ex) { Log.Error("taskbar tweak " + name, ex); return false; }
    }

    public static bool CenterIcons
    {
        get => (ReadDword(Adv, "TaskbarAl") ?? 1) == 1;
        set => WriteDword(Adv, "TaskbarAl", value ? 1 : 0);
    }

    public static bool ShowTaskView
    {
        get => (ReadDword(Adv, "ShowTaskViewButton") ?? 1) == 1;
        set => WriteDword(Adv, "ShowTaskViewButton", value ? 1 : 0);
    }

    public static bool ShowWidgetsButton
    {
        get => (ReadDword(Adv, "TaskbarDa") ?? 1) == 1;
    }
    public static bool SetWidgetsButton(bool value) => WriteDword(Adv, "TaskbarDa", value ? 1 : 0);

    /// <summary>0 hidden, 1 icon, 2 box, 3 icon and label</summary>
    public static int SearchMode
    {
        get => ReadDword(SearchKey, "SearchboxTaskbarMode") ?? 2;
        set => WriteDword(SearchKey, "SearchboxTaskbarMode", value);
    }
}

/// <summary>Shows or hides the icons on the desktop without touching any files.</summary>
public static class DesktopIcons
{
    static IntPtr FindListView()
    {
        IntPtr progman = Native.FindWindow("Progman", null);
        IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            Native.EnumWindows((h, _) =>
            {
                if (Native.ClassOf(h) == "WorkerW")
                {
                    var dv = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (dv != IntPtr.Zero) { defView = dv; return false; }
                }
                return true;
            }, IntPtr.Zero);
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    public static void Set(bool visible)
    {
        var lv = FindListView();
        if (lv != IntPtr.Zero && Native.IsWindowVisible(lv) != visible)
            Native.ShowWindow(lv, visible ? Native.SW_SHOWNA : Native.SW_HIDE);
    }
}

public sealed class AppWindow
{
    public IntPtr Hwnd;
    public string Title = "";
    public string? ExePath;
    public uint Pid;
}

/// <summary>Finds the windows that would normally appear on the taskbar.</summary>
public static class WindowTracker
{
    static readonly uint MyPid = (uint)Environment.ProcessId;
    static readonly Dictionary<uint, string?> pathCache = new();

    public static List<AppWindow> TaskbarWindows()
    {
        var list = new List<AppWindow>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
            if ((ex & Native.WS_EX_NOACTIVATE) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
            if (Native.DwmGetWindowAttribute(h, 14 /* CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var title = Native.TitleOf(h);
            if (title.Length == 0) return true;
            var cls = Native.ClassOf(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            // Brim's own Settings and Wallpapers windows belong on the dock like any app (the notch, dock,
            // widgets and pop-ups are tool windows and were already skipped above)
            if (!pathCache.TryGetValue(pid, out var path))
            {
                path = Native.ProcessPath(pid);
                pathCache[pid] = path;
            }
            list.Add(new AppWindow { Hwnd = h, Title = title, ExePath = path, Pid = pid });
            return true;
        }, IntPtr.Zero);
        if (pathCache.Count > 400) pathCache.Clear();
        return list;
    }

    static readonly Dictionary<uint, bool> wallpaperPids = new();

    /// <summary>Wallpaper Engine, Lively and similar draw the desktop; their windows mean "you're on the desktop".</summary>
    public static bool IsWallpaperApp(uint pid)
    {
        if (wallpaperPids.TryGetValue(pid, out bool yes)) return yes;
        var path = Native.ProcessPath(pid) ?? "";
        var name = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        yes = name is "wallpaper32" or "wallpaper64" or "webwallpaper32" or "ui32" or "lively" or "livelywpf" or "livelycu" ||
              name.StartsWith("lively.");
        if (wallpaperPids.Count > 200) wallpaperPids.Clear();
        wallpaperPids[pid] = yes;
        return yes;
    }

    /// <summary>What the focused window means for "is something fullscreen?".</summary>
    public enum FocusKind
    {
        /// <summary>The desktop or a wallpaper app: nothing is fullscreen</summary>
        Desktop,
        /// <summary>Start, Search or the taskbar: you asked for the shell, so the dock may show over a game</summary>
        Shell,
        /// <summary>A pop-up that doesn't change what's on screen (tray flyouts, toasts, OSDs, Brim's own widgets):
        /// look at the app window behind it instead</summary>
        Transient,
        /// <summary>An ordinary app window: judge it</summary>
        App,
    }

    static readonly Dictionary<uint, string> exeNames = new();

    static string ExeName(uint pid)
    {
        if (exeNames.TryGetValue(pid, out var n)) return n;
        n = System.IO.Path.GetFileNameWithoutExtension(Native.ProcessPath(pid) ?? "").ToLowerInvariant();
        if (exeNames.Count > 300) exeNames.Clear();
        exeNames[pid] = n;
        return n;
    }

    /// <summary>
    /// Sorts the focused window. Pure rules over a few facts about the window, so they can be checked without
    /// opening anything on screen (see <see cref="SelfTest"/>).
    /// </summary>
    public static FocusKind Classify(string cls, string exe, bool visible, bool iconic, long exStyle, bool own, bool ownAppWindow, bool wallpaperApp)
    {
        if (cls is "Progman" or "WorkerW" || wallpaperApp) return FocusKind.Desktop;
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return FocusKind.Shell;
        if (exe is "startmenuexperiencehost" or "searchhost" or "searchapp" or "searchui") return FocusKind.Shell;
        // Hidden or minimised windows can't cover anything (e.g. the "Battery Meter" tray window, or Wallpaper
        // Engine's hidden one when you click the desktop): look behind them
        if (!visible || iconic) return FocusKind.Transient;
        if (own) return ownAppWindow ? FocusKind.App : FocusKind.Transient; // Brim's Settings counts; its widgets don't
        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0 || (exStyle & Native.WS_EX_NOACTIVATE) != 0) return FocusKind.Transient;
        if (exe is "shellexperiencehost" or "shellhost" or "lockapp" && cls != "Windows.UI.Core.CoreWindow") return FocusKind.Transient;
        if (cls is "SystemTray_Main" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "Windows.UI.Composition.DesktopWindowContentBridge"
            or "XamlExplorerHostIslandWindow" or "ForegroundStaging" or "MultitaskingViewFrame" or "#32768" or "tooltips_class32")
            return cls is "MultitaskingViewFrame" or "XamlExplorerHostIslandWindow" ? FocusKind.Shell : FocusKind.Transient;
        return FocusKind.App;
    }

    /// <summary>
    /// A window fills the whole primary screen and isn't an ordinary maximised window. Browsers going fullscreen from
    /// maximised stay "maximised" but drop their title bar and border, so only a maximised window that still has
    /// those is an ordinary window.
    /// </summary>
    internal static bool CoversScreen(bool zoomed, long style, bool onPrimary, Native.RECT r, Native.RECT mon) =>
        onPrimary && !(zoomed && (style & (0x00C00000 /* WS_CAPTION */ | 0x00040000 /* WS_THICKFRAME */)) != 0) &&
        r.Left <= mon.Left && r.Top <= mon.Top && r.Right >= mon.Right && r.Bottom >= mon.Bottom;

    static bool Covers(IntPtr h)
    {
        var mon = Native.MonitorFromWindow(h, 2);
        var primary = Native.MonitorFromPoint(new Native.POINT(), 1);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);
        Native.GetWindowRect(h, out var r);
        return CoversScreen(Native.IsZoomed(h), Native.GetWindowLongPtr(h, -16).ToInt64(), mon == primary, r, mi.rcMonitor);
    }

    static FocusKind KindOf(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        bool own = pid == MyPid;
        return Classify(Native.ClassOf(h), ExeName(pid), Native.IsWindowVisible(h), Native.IsIconic(h), ex, own,
                        own && (ex & Native.WS_EX_TOOLWINDOW) == 0, !own && IsWallpaperApp(pid));
    }

    /// <summary>The top-most ordinary app window on the primary screen (skipping pop-ups, tool windows and Brim's own).</summary>
    static IntPtr TopAppWindow()
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            if (Native.DwmGetWindowAttribute(h, 14 /* CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var kind = KindOf(h);
            if (kind == FocusKind.Desktop) return false; // reached the desktop: nothing covers it
            if (kind != FocusKind.App) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>True when a game or fullscreen video covers the whole primary monitor.</summary>
    public static bool ForegroundIsFullscreen()
    {
        var fg = Native.GetForegroundWindow();
        var kind = fg == IntPtr.Zero ? FocusKind.Transient : KindOf(fg);
        switch (kind)
        {
            case FocusKind.Desktop:
            case FocusKind.Shell:
                return false;
            case FocusKind.App:
                return Covers(fg);
            default:
                var top = TopAppWindow();
                return top != IntPtr.Zero && Covers(top);
        }
    }

    /// <summary>`Brim.exe --fullscreen-test out.txt`: the rules against situations seen in the log.</summary>
    public static string SelfTest()
    {
        var sb = new System.Text.StringBuilder();
        int fails = 0;
        void Expect(string name, FocusKind got, FocusKind want)
        {
            bool ok = got == want;
            if (!ok) fails++;
            sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {name}: {got} (want {want})");
        }
        const long tool = Native.WS_EX_TOOLWINDOW;
        Expect("Battery Meter (hidden tray window) over fullscreen Chrome", Classify("SystemTray_Main", "explorer", false, false, 0, false, false, false), FocusKind.Transient);
        Expect("Brim widget clicked over a game", Classify("HwndWrapper[Brim;;x]", "brim", true, false, tool, true, false, false), FocusKind.Transient);
        Expect("Brim Settings", Classify("HwndWrapper[Brim;;x]", "brim", true, false, 0, true, true, false), FocusKind.App);
        Expect("Start menu", Classify("Windows.UI.Core.CoreWindow", "startmenuexperiencehost", true, false, 0, false, false, false), FocusKind.Shell);
        Expect("Search", Classify("Windows.UI.Core.CoreWindow", "searchhost", true, false, 0, false, false, false), FocusKind.Shell);
        Expect("Desktop", Classify("Progman", "explorer", true, false, 0, false, false, false), FocusKind.Desktop);
        Expect("Wallpaper Engine", Classify("WPEDesktopDX11Window", "wallpaper64", false, false, 0, false, false, true), FocusKind.Desktop);
        Expect("Game (SDL window)", Classify("SDL_app", "bg3", true, false, 0, false, false, false), FocusKind.App);
        Expect("Volume / notification toast", Classify("Windows.UI.Core.CoreWindow", "shellexperiencehost", true, false, tool | Native.WS_EX_NOACTIVATE, false, false, false), FocusKind.Transient);
        Expect("Alt+Tab", Classify("XamlExplorerHostIslandWindow", "explorer", true, false, 0, false, false, false), FocusKind.Shell);

        var mon = new Native.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1600 };
        void Cover(string name, bool got, bool want) { bool ok = got == want; if (!ok) fails++; sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {name}: {got} (want {want})"); }
        Cover("YouTube fullscreen (maximised, no caption, style 0x170B0000)", CoversScreen(true, 0x170B0000, true, new Native.RECT { Left = -8, Top = -8, Right = 2568, Bottom = 1608 }, mon), true);
        Cover("Chrome maximised (caption, style 0x17CF0000)", CoversScreen(true, 0x17CF0000, true, new Native.RECT { Left = -8, Top = -8, Right = 2568, Bottom = 1608 }, mon), false);
        Cover("Borderless game (style 0x96020000)", CoversScreen(false, 0x96020000, true, mon, mon), true);
        Cover("Windowed game", CoversScreen(false, 0x14CF0000, true, new Native.RECT { Left = 100, Top = 100, Right = 1700, Bottom = 1000 }, mon), false);
        Cover("Fullscreen on another screen", CoversScreen(false, 0x96020000, false, mon, mon), false);
        sb.Insert(0, fails == 0 ? "ALL PASSED\n" : $"{fails} FAILED\n");
        return sb.ToString();
    }
}

public static class Launcher
{
    public static void Open(string path, string? args = null)
    {
        try
        {
            // Store apps saved as "shell:AppsFolder\<app id>" start through Explorer
            var psi = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }
                : new ProcessStartInfo(path) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Error("launch " + path, ex); }
    }

    public static void ShowInFolder(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }
}

public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Set(bool enabled, string? exe = null)
    {
        try
        {
            exe ??= Installer.IsInstalled && !Installer.RunningInstalled ? System.IO.Path.Combine(Installer.InstallDir, "Brim.exe") : Environment.ProcessPath;
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled && exe != null) k.SetValue("Brim", $"\"{exe}\"");
            else if (k.GetValue("Brim") != null) k.DeleteValue("Brim");
        }
        catch (Exception ex) { Log.Error("startup", ex); }
    }
}
