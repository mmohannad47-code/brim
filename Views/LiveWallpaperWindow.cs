using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Views;

/// <summary>
/// Plays a looping, muted video (or animated scene) behind the desktop icons, one window per screen.
/// Runs in its own process (Brim.exe --live file parentPid). Plays and pauses by <see cref="LivePolicy"/>, reacting
/// the moment focus changes, and quits when Brim does.
/// </summary>
static class LiveWallpaperController
{
    static readonly List<LiveWallpaperWindow> windows = new();
    static readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromSeconds(1) };
    static readonly DispatcherTimer trim = new() { Interval = TimeSpan.FromSeconds(60) };
    static readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromMilliseconds(30) };
    static Native.WinEventDelegate? proc;
    static readonly List<IntPtr> hooks = new();
    static int parentPid;
    static bool playing = true;

    public static void Start(string path, int parent)
    {
        parentPid = parent;
        foreach (var screen in Native.Screens())
        {
            var w = new LiveWallpaperWindow(path, screen.Bounds);
            windows.Add(w);
            w.Show();
        }

        proc = (_, ev, hwnd, idObject, _, _, _) =>
        {
            if (idObject != 0) return;
            // A focus change acts at once, so the wallpaper starts the moment you click the desktop
            if (ev == 0x0003) { settle.Stop(); Apply(); return; }
            // Windows showing/hiding come in bursts (minimising, Win+D): act once they settle
            settle.Stop();
            settle.Start();
        };
        foreach (var (min, max) in new (uint, uint)[] { (0x0003, 0x0003), (0x0016, 0x0017), (0x8002, 0x8003) })
            hooks.Add(Native.SetWinEventHook(min, max, IntPtr.Zero, proc, 0, 0, 0x0002 /* skip own process */));
        settle.Tick += (_, _) => { settle.Stop(); Apply(); };
        watch.Tick += (_, _) => { CheckParent(); Apply(); };
        watch.Start();
        trim.Tick += (_, _) => { trim.Stop(); if (!playing) Native.TrimMemory(false); };
        Application.Current.Exit += (_, _) => { foreach (var h in hooks) Native.UnhookWinEvent(h); };
        Apply();
    }

    static void CheckParent()
    {
        try
        {
            if (Process.GetProcessById(parentPid).HasExited) Application.Current.Shutdown();
        }
        catch { Application.Current.Shutdown(); }
    }

    static void Apply()
    {
        var play = LivePolicy.ShouldPlay(parentPid);
        if (play == null || play == playing) return;
        playing = play.Value;
        foreach (var w in windows) w.SetPlaying(playing);
        // Free memory only after a long pause, so coming back to the desktop is instant
        trim.Stop();
        if (!playing) trim.Start();
    }
}

sealed class LiveWallpaperWindow : Window
{
    readonly MediaElement? media;
    readonly SceneView? sceneView;
    readonly Native.RECT bounds;

    public LiveWallpaperWindow(string path, Native.RECT bounds)
    {
        this.bounds = bounds;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Black;
        Left = -32000;
        Top = -32000;
        Width = 64;
        Height = 64;
        Title = "Brim live wallpaper";

        var scene = Scenes.Create(path);
        if (scene != null)
        {
            sceneView = new SceneView(scene);
            Content = sceneView;
        }
        else
        {
            var m = new MediaElement
            {
                Source = new Uri(path, UriKind.Absolute),
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                IsMuted = true,
                Volume = 0,
                Stretch = Stretch.UniformToFill,
                ScrubbingEnabled = true, // keeps the current frame shown while paused
            };
            m.MediaEnded += (_, _) => { m.Position = TimeSpan.Zero; m.Play(); };
            m.MediaFailed += (_, e) => { Log.Error("live media " + System.IO.Path.GetFileName(path), e.ErrorException); Application.Current.Shutdown(); };
            m.MediaOpened += async (_, _) =>
            {
                Log.Note("live", $"opened {System.IO.Path.GetFileName(path)} ({m.NaturalVideoWidth}x{m.NaturalVideoHeight}, {m.NaturalDuration})");
                var p0 = m.Position;
                await System.Threading.Tasks.Task.Delay(3000);
                Log.Note("live", $"after 3 s: position {p0.TotalSeconds:0.0} → {m.Position.TotalSeconds:0.0} s ({(m.Position > p0 ? "playing" : "not moving: paused because a window covers the desktop, or stuck")})");
            };
            media = m;
            Content = m;
        }

        SourceInitialized += (_, _) =>
        {
            // WPF strips WS_EX_LAYERED from windows that weren't created see-through; this window needs it (see Attach)
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(KeepLayered);
            Attach();
        };
        // WPF applies the window's own Left/Top/size when it first shows it, which would move it off-screen inside
        // the desktop. Put it back where it belongs once it's shown.
        Loaded += (_, _) => { Place(); media?.Play(); };
        ContentRendered += (_, _) => Place();
        SizeChanged += (_, _) => { if (!placing) Dispatcher.BeginInvoke(new Action(Place)); };
        Closed += (_, _) => Wallpapers.RefreshDesktop();
    }

    public void SetPlaying(bool play)
    {
        if (play) media?.Play();
        else media?.Pause();
        if (sceneView != null) sceneView.Paused = !play;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct STYLESTRUCT { public uint styleOld, styleNew; }

    IntPtr KeepLayered(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        const int WM_STYLECHANGING = 0x007C;
        if (msg == WM_STYLECHANGING && w.ToInt32() == Native.GWL_EXSTYLE && (wantedEx & Native.WS_EX_LAYERED) != 0)
        {
            var s = System.Runtime.InteropServices.Marshal.PtrToStructure<STYLESTRUCT>(l);
            if ((s.styleNew & Native.WS_EX_LAYERED) == 0)
            {
                s.styleNew |= (uint)Native.WS_EX_LAYERED;
                System.Runtime.InteropServices.Marshal.StructureToPtr(s, l, false);
            }
            handled = true; // and don't let WPF take it out again
        }
        return IntPtr.Zero;
    }

    bool placing;
    long wantedEx;
    IntPtr below; // the icons layer we sit just under (24H2), or zero

    /// <summary>
    /// Cover this window's screen exactly (in the desktop window's coordinates), with the styles it needs. WPF resets
    /// the position and the extended style when it first shows the window, after Attach ran, so this runs again then.
    /// </summary>
    void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
        placing = true;
        long had = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if (wantedEx != 0 && had != wantedEx)
        {
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(wantedEx));
            bool alpha = (wantedEx & Native.WS_EX_LAYERED) == 0 || Native.SetLayeredWindowAttributes(hwnd, 0, 255, 0x2 /* LWA_ALPHA */);
            long now = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Log.Note("live", $"window style 0x{had:X} → wanted 0x{wantedEx:X}, got 0x{now:X}{(alpha ? "" : $", layering refused (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})")}");
        }
        Native.SetWindowPos(hwnd, below, bounds.Left - vx, bounds.Top - vy, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            Native.SWP_NOACTIVATE | (below == IntPtr.Zero ? Native.SWP_NOZORDER : 0) | 0x0020 /* SWP_FRAMECHANGED */);
        placing = false;
    }

    void Attach()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        IntPtr progman = Native.FindWindow("Progman", null);
        // Ask Explorer to create the layer that sits between the wallpaper and the icons
        Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out _);
        Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        // Child windows are placed relative to the desktop window, whose corner is the top-left of all screens
        int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
        int w = bounds.Right - bounds.Left, h = bounds.Bottom - bounds.Top;
        int x = bounds.Left - vx, y = bounds.Top - vy;

        IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr innerWorker = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);

        long ex = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;

        if (defView != IntPtr.Zero && innerWorker != IntPtr.Zero && Environment.GetEnvironmentVariable("BRIM_LIVE_MODE") != "worker")
        {
            // Windows 11 24H2 and later: the icons (SHELLDLL_DefView) and the wallpaper (a WorkerW) are children of
            // Progman, and the still wallpaper is drawn on top of anything ordinary inside that WorkerW. So sit between
            // the two as a layer of our own (a layered child of Progman), just under the icons. Windows only accepts the
            // layer while the window is still top-level, so: layered first, then child.
            wantedEx = ex | Native.WS_EX_LAYERED; // set before the style change, so KeepLayered lets it through
            below = defView;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(wantedEx));
            Native.SetLayeredWindowAttributes(hwnd, 0, 255, 0x2 /* LWA_ALPHA */);
            Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(Native.WS_CHILD | Native.WS_VISIBLE));
            Native.SetParent(hwnd, progman);
            Native.SetWindowPos(hwnd, defView, x, y, w, h, Native.SWP_NOACTIVATE);
            Log.Note("live", $"attached as a layer under the icons: ex 0x{Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64():X}");
            return;
        }
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(Native.WS_CHILD | Native.WS_VISIBLE));
        if (false) { }
        else if (defView != IntPtr.Zero && innerWorker != IntPtr.Zero)
        {
            // Alternative: inside the WorkerW, like Wallpaper Engine's window
            wantedEx = ex;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            Native.SetParent(hwnd, innerWorker);
            Native.SetWindowPos(hwnd, new IntPtr(0) /* HWND_TOP */, x, y, w, h, Native.SWP_NOACTIVATE);
        }
        else
        {
            // Older Windows: draw inside the WorkerW that sits behind the icon window
            IntPtr worker = IntPtr.Zero;
            Native.EnumWindows((top, _) =>
            {
                if (Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                    worker = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                return true;
            }, IntPtr.Zero);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            Native.SetParent(hwnd, worker != IntPtr.Zero ? worker : progman);
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }
    }
}
