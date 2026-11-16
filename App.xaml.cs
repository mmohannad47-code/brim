using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Brim.Core;
using Brim.Views;

namespace Brim;

public partial class App : Application
{
    /// <summary>Fires once a second on the UI thread; clocks and stats hang off it.</summary>
    public static event Action? SecondTick;

    static AppSettings S => SettingsStore.Current;

    Mutex? single;
    MessageWindow? messages;
    TrayIcon? tray;
    NotchWindow? notch;
    DockWindow? dock;
    SettingsWindow? settings;
    readonly WidgetHost widgets = new();
    readonly DispatcherTimer second = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer shell = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer trimTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer idleTrim = new() { Interval = TimeSpan.FromSeconds(60) };
    GalleryWindow? gallery;

    /// <summary>Compact the heap and return unused pages shortly after a burst of UI work (closing a panel or window).</summary>
    bool hotkeysPaused, gameHasKeys;

    /// <summary>Take Brim's keyboard shortcuts as set up in Settings (each can be off, or on other keys).</summary>
    public void ApplyHotkeys()
    {
        if (messages == null) return;
        messages.UnregisterAll();
        if (hotkeysPaused || gameHasKeys) return;
        foreach (var e in HotkeyCatalog.All)
        {
            var h = HotkeyCatalog.For(e.Id);
            if (!h.On) continue;
            // Win+1…9 belong to the taskbar: Brim's only while its dock stands in for Windows' taskbar
            if (e.Id == "dockapps" && S.Taskbar != TaskbarMode.Dock) continue;
            if (HotkeyCatalog.Numbered(e.Id))
                for (int i = 1; i <= 9; i++)
                    messages.Register(e.MessageId + i - 1, h.Mods, (uint)('0' + i), $"{HotkeyCatalog.Describe(h.Mods, (uint)('0' + i))} ({(e.Id == "dockapps" ? "dock app" : "shortcut")} {i})");
            else if (h.Vk != 0)
                messages.Register(e.MessageId, h.Mods, h.Vk, $"{HotkeyCatalog.Describe(h.Mods, h.Vk)} ({e.Name.ToLowerInvariant()})");
        }
    }

    /// <summary>While Settings records new keys: no shortcut fires (or swallows the keys being pressed).</summary>
    public void PauseHotkeys(bool pause)
    {
        hotkeysPaused = pause;
        ApplyHotkeys();
    }

    /// <summary>"Pause shortcuts while a game or video is fullscreen": the game gets keys like Ctrl+Space.</summary>
    void HotkeysForFullscreen(System.Collections.Generic.HashSet<IntPtr> covered)
    {
        bool game = S.HotkeysOffInFullscreen && covered.Count > 0 &&
                    covered.Contains(Native.MonitorFromWindow(Native.GetForegroundWindow(), 2));
        if (game == gameHasKeys) return;
        gameHasKeys = game;
        Log.Note("hotkey", game ? "a game or video is fullscreen: Brim's shortcuts are paused" : "Brim's shortcuts are back");
        ApplyHotkeys();
    }

    public void ScheduleTrim()
    {
        busyAt = DateTime.Now;
        picturesReleased = false;
        trimTimer.Stop();
        trimTimer.Start();
    }

    DateTime busyAt = DateTime.Now;
    bool picturesReleased;
    int idleTicks;

    /// <summary>
    /// Every minute: hand unused pages back to Windows. Once Brim has been quiet for 3 minutes (notch shut, no
    /// Settings or Wallpapers window), also let go of cached icons and game covers. Every half hour, a `[memory]`
    /// line in the log says where the memory goes.
    /// </summary>
    void IdleTrim()
    {
        idleTicks++;
        // Measured before trimming (straight after, Windows has just moved everything aside and it reads ~0)
        if (idleTicks % 30 == 10) Log.Note("memory", MemoryCare.Describe()); // 10 minutes in, then every half hour
        bool quiet = settings == null && gallery == null && notch?.IsOpen != true && DateTime.Now - busyAt > TimeSpan.FromMinutes(3);
        if (quiet && !picturesReleased)
        {
            picturesReleased = true;
            var before = MemoryCare.Now();
            int n = MemoryCare.Release();
            Native.TrimMemory(collect: true);
            if (n >= 10) Log.Note("memory", $"quiet: let go of {n} cached pictures · committed {before.committed:0.0} → {MemoryCare.Now().committed:0.0} MB");
        }
        else Native.TrimMemory(collect: false);
    }

    DateTime snipUntil;

    /// <summary>Opens Windows' snipping overlay; the snip you take lands on the notch's Shelf.</summary>
    public async void TakeScreenshot()
    {
        snipUntil = DateTime.Now.AddMinutes(1);
        await System.Threading.Tasks.Task.Delay(250); // let the notch tuck away first
        Native.TapKeys(Native.VK_LWIN, 0x10 /* Shift */, (byte)'S');
    }

    void OnClipboardImage(ClipItem item)
    {
        if (DateTime.Now > snipUntil || item.Png == null) return;
        snipUntil = default;
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim", "Screenshots");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            System.IO.File.WriteAllBytes(path, item.Png);
            Shelf.Add(new[] { path });
            notch?.ShowActivity("", "Screenshot on your shelf", "", null, 2.5);
        }
        catch (Exception ex) { Log.Error("screenshot", ex); }
    }

    public void OpenWallpapers()
    {
        if (gallery == null)
        {
            gallery = new GalleryWindow();
            gallery.Closed += (_, _) => gallery = null;
            gallery.Show();
        }
        if (gallery.WindowState == WindowState.Minimized) gallery.WindowState = WindowState.Normal;
        gallery.Activate();
        Dispatcher.BeginInvoke(new Action(widgets.CheckDesktop), DispatcherPriority.Background);
    }

    /// <summary>Turn a live wallpaper on (path) or off (null).</summary>
    public async void SetLiveWallpaper(string? path, bool refresh = true)
    {
        S.LiveWallpaper = path;
        SettingsStore.Save();
        if (path == null) LiveWallpaperHost.Stop(refresh);
        else
        {
            await WallpaperEngine.TakeOverAsync(); // otherwise its wallpaper would sit over Brim's (and keep coming back)
            if (S.LiveWallpaper == path) LiveWallpaperHost.Start(path);
        }
        RefreshSettingsWindow();
    }
    int shellTicks;
    bool quitting, owner;

    public NotchWindow? Notch => notch;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 3 && e.Args[0] == "--live")
        {
            // Separate small process that only plays the live wallpaper (graphics card for video; BRIM_LIVE_SOFTWARE=1
            // draws on the CPU instead, for machines whose graphics driver misbehaves)
            if (Environment.GetEnvironmentVariable("BRIM_LIVE_SOFTWARE") == "1")
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            SettingsStore.Load();
            SettingsStore.ReadOnly = true;
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            DispatcherUnhandledException += (_, ex) => { Log.Error("live", ex.Exception); ex.Handled = true; Shutdown(); };
            LiveWallpaperController.Start(e.Args[1], int.TryParse(e.Args[2], out int parent) ? parent : 0);
            return;
        }

        // Brim's UI is small and mostly still: drawing it on the CPU avoids loading the graphics driver,
        // which was most of the memory use
        if (Environment.GetEnvironmentVariable("BRIM_GPU") != "1") System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        if (e.Args.Length == 2 && e.Args[0] == "--backdrop-test")
        {
            // Developer check: does Windows clip the blur layer to its rounded shape? Writes where it is.
            Dispatcher.InvokeAsync(async () =>
            {
                var mon = Native.PrimaryMonitorRect();
                var b = new Backdrop();
                int x = 60, y = mon.Bottom - 360, w = 420, h = 120;
                b.Place(IntPtr.Zero, x, y, w, h, 27, false, true);
                System.IO.File.WriteAllText(e.Args[1], $"{x} {y} {w} {h}");
                await System.Threading.Tasks.Task.Delay(1800);
                b.Dispose();
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--cover-test")
        {
            // Developer check: an invisible notch, then which windows would tuck it away
            SettingsStore.Load();
            SettingsStore.ReadOnly = true;
            Dispatcher.InvokeAsync(async () =>
            {
                var n = new Brim.Views.NotchWindow { Opacity = 0, ShowActivated = false };
                n.Show();
                await System.Threading.Tasks.Task.Delay(500);
                string report;
                try { report = CoverTest(n); } catch (Exception ex) { report = "FAIL " + ex; }
                n.Close();
                System.IO.File.WriteAllText(e.Args[1], report);
                Shutdown();
            });
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--lyrics-test")
        {
            // Developer check (opens nothing): parsing, LRCLIB lookups, and the line for what's playing right now
            Dispatcher.InvokeAsync(async () =>
            {
                string report;
                try { report = await Lyrics.SelfTestAsync(); } catch (Exception ex) { report = "FAIL " + ex; }
                System.IO.File.WriteAllText(e.Args[1], report);
                Shutdown();
            });
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--store-test")
        {
            // Developer check (opens nothing): Store apps resolve to the right app id, before and after updates
            System.IO.File.WriteAllText(e.Args[1], StoreApps.SelfTest());
            Shutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--fullscreen-test")
        {
            // Developer check: the fullscreen rules against situations from the log, plus what they say right now
            var report = WindowTracker.SelfTest() + $"\nnow: fullscreen={WindowTracker.ForegroundIsFullscreen()} {Log.Foreground()}\n" +
                         $"screens covered now: {WindowTracker.FullscreenMonitors().Count} of {Native.Screens().Count}\n";
            report += $"Wallpaper Engine showing a wallpaper: {WallpaperEngine.ShowingWallpaper()}\n";
            System.IO.File.WriteAllText(e.Args[1], report);
            Shutdown();
            return;
        }

        if (e.Args.Length == 1 && e.Args[0] == "--tray-spam-test")
        {
            // Developer check: an app updating its tray icon 20 times a second (like Discord) must never freeze Brim
            Dispatcher.InvokeAsync(async () =>
            {
                var w = new Window { Left = -32000, Top = -32000, Width = 10, Height = 10, ShowActivated = false, ShowInTaskbar = false, Title = "Brim tray spam test" };
                w.Show();
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                var a = Native.LoadIcon(IntPtr.Zero, new IntPtr(32512));
                var b = Native.LoadIcon(IntPtr.Zero, new IntPtr(32513));
                var nid = new Native.NOTIFYICONDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.NOTIFYICONDATA>(), hWnd = h, uID = 78, uFlags = 0x2 | 0x4, hIcon = a, szTip = "Spam test" };
                Native.Shell_NotifyIcon(0, ref nid);
                for (int i = 0; i < 300; i++)
                {
                    nid.hIcon = i % 2 == 0 ? b : a;
                    Native.Shell_NotifyIcon(1, ref nid);
                    await System.Threading.Tasks.Task.Delay(50);
                }
                Native.Shell_NotifyIcon(2, ref nid);
                w.Close();
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 1 && e.Args[0] == "--tray-badge-test")
        {
            // Developer check: a tray icon gets an "unread" badge the way chat apps do; the running Brim should notice
            Dispatcher.InvokeAsync(async () =>
            {
                var w = new Window { Left = -32000, Top = -32000, Width = 10, Height = 10, ShowActivated = false, ShowInTaskbar = false, Title = "Brim tray badge test" };
                w.Show();
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                var nid = new Native.NOTIFYICONDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.NOTIFYICONDATA>(), hWnd = h, uID = 77, uFlags = 0x2 | 0x4, hIcon = Native.LoadIcon(IntPtr.Zero, new IntPtr(32512) /* IDI_APPLICATION */), szTip = "Badge test" };
                Native.Shell_NotifyIcon(0, ref nid);          // NIM_ADD: the normal icon
                await System.Threading.Tasks.Task.Delay(1500);
                nid.hIcon = Native.LoadIcon(IntPtr.Zero, new IntPtr(32513) /* IDI_ERROR: a red badge */);
                Native.Shell_NotifyIcon(1, ref nid);          // NIM_MODIFY: now with a "badge"
                await System.Threading.Tasks.Task.Delay(2000);
                Native.Shell_NotifyIcon(2, ref nid);          // NIM_DELETE
                w.Close();
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 1 && e.Args[0] == "--flash-test")
        {
            // Developer check: a window off-screen asks for attention the way chat apps do; the running Brim should log it
            Dispatcher.InvokeAsync(async () =>
            {
                var w = new Window { Left = -32000, Top = -32000, Width = 200, Height = 80, Title = "Test chat - Brim flash test", ShowActivated = false };
                w.Show();
                await System.Threading.Tasks.Task.Delay(500);
                var fi = new Native.FLASHWINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.FLASHWINFO>(), hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle, dwFlags = 2 /* FLASHW_TRAY */, uCount = 3 };
                Native.FlashWindowEx(ref fi);
                await System.Threading.Tasks.Task.Delay(2500);
                w.Close();
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--appbar-test")
        {
            // Developer check: does reserving a strip at the bottom shrink the work area, through Brim's tray window
            // (how the dock does it today) and straight to Explorer's?
            Dispatcher.InvokeAsync(async () =>
            {
                var sb = new System.Text.StringBuilder();
                var w = new Window { Left = 0, Top = -2000, Width = 300, Height = 60, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Title = "Brim appbar test" };
                w.Show();
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                Native.MakeToolWindow(h, true);
                Rect Work() { Native.SystemParametersInfoRect(0x0030, 0, out var r, 0); return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); }
                sb.AppendLine("before: work area " + Work());
                foreach (var direct in new[] { false, true })
                {
                    sb.AppendLine(direct ? "-- straight to Explorer" : "-- through whatever Shell_TrayWnd is found first (Brim's tray if it runs)");
                    sb.AppendLine("first Shell_TrayWnd belongs to pid " + PidOf(Native.FindWindow("Shell_TrayWnd", null)) + $" (Brim main = {string.Join(",", System.Diagnostics.Process.GetProcessesByName("Brim").Select(p => p.Id))})");
                    var res = DockAppBar.Reserve(h, 60, direct);
                    sb.AppendLine("  " + res);
                    await System.Threading.Tasks.Task.Delay(1200);
                    sb.AppendLine("  work area now " + Work());
                    sb.AppendLine("  " + DockAppBar.Remove(h, direct));
                    await System.Threading.Tasks.Task.Delay(800);
                    sb.AppendLine("  after removing: " + Work());
                }
                w.Close();
                System.IO.File.WriteAllText(e.Args[1], sb.ToString());
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--messages-test")
        {
            // Developer check: run the Messages feed exactly as the app does (timer and all) and report what it sees
            SettingsStore.Load();
            SettingsStore.ReadOnly = true;
            ThemeManager.Apply();
            Dispatcher.InvokeAsync(async () =>
            {
                var sb = new System.Text.StringBuilder();
                MessageCenter.Changed += () => sb.AppendLine($"changed: all={MessageCenter.AllCount} visible={MessageCenter.Visible.Count}");
                MessageCenter.Arrived += m => sb.AppendLine($"POP-UP: {m.App} · {m.Sender} · {m.Text}");
                // Let the flash-test app through (it's called Brim, which is off by default), then make it flash
                SettingsStore.Current.MessageApps["Brim"] = new MessageRule { Mode = "All" };
                MessageCenter.Sync();
                await System.Threading.Tasks.Task.Delay(5000);
                System.Diagnostics.Process.Start(Environment.ProcessPath!, "--flash-test");
                await System.Threading.Tasks.Task.Delay(5000);
                sb.AppendLine($"status={MessageCenter.Status ?? "ok"} all={MessageCenter.AllCount} visible={MessageCenter.Visible.Count}");
                foreach (var m in MessageCenter.Visible) sb.AppendLine($"  {m.App} · {m.Sender.Length} chars · {m.Time:g} · passes={MessageCenter.Passes(m)}");
                foreach (var a in MessageCenter.KnownApps()) sb.AppendLine($"  app {a}: {MessageCenter.Describe(a)}");
                sb.AppendLine("-- links and badges");
                foreach (var line in MessageCenter.Diagnose()) sb.AppendLine(line);
                // and the tab itself
                var host = new Window { Left = -4000, Top = 0, Width = 560, Height = 200, ShowActivated = false, WindowStyle = WindowStyle.None };
                var view = new Brim.Views.MessagesViewProbe();
                host.Content = view.Element;
                host.Show();
                await System.Threading.Tasks.Task.Delay(1500);
                sb.AppendLine("tab rows: " + view.Rows);
                host.Close();
                System.IO.File.WriteAllText(e.Args[1], sb.ToString());
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 3 && e.Args[0] == "--wallpaper-test")
        {
            // Developer check: take the wallpaper over from Wallpaper Engine (a live video or a picture), watch whether
            // its scene comes back (the guard should close it), then hand the desktop back to Wallpaper Engine as it was
            SettingsStore.Load();
            SettingsStore.ReadOnly = true;
            Dispatcher.InvokeAsync(async () =>
            {
                var sb = new System.Text.StringBuilder();
                var start = DateTime.Now;
                void Line(string s) => sb.AppendLine($"{(DateTime.Now - start).TotalSeconds,5:0.0}s {s}");
                var original = WallpaperEngine.CurrentFile();
                var originalPicture = Wallpapers.Current();
                bool picture = System.IO.Path.GetExtension(e.Args[2]).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp";
                Line($"before: WE showing={WallpaperEngine.ShowingWallpaper()} file={System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(original ?? ""))}");
                if (picture) SettingsStore.Current.BrimPicture = e.Args[2];
                await WallpaperEngine.TakeOverAsync();
                Line($"took over: WE showing={WallpaperEngine.ShowingWallpaper()}");
                if (picture) Wallpapers.ApplyPicture(e.Args[2]);
                else LiveWallpaperHost.Start(e.Args[2]);
                int reappeared = 0, closedAgain = 0;
                bool was = false;
                for (int i = 0; i < 30; i++)
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    bool showing = WallpaperEngine.ShowingWallpaper();
                    if (showing && !was) { reappeared++; Line("WE scene came back"); }
                    if (!showing && was) { closedAgain++; Line("WE scene closed again"); }
                    was = showing;
                    WallpaperEngine.Guard();
                }
                await System.Threading.Tasks.Task.Delay(1500);
                Line($"end: WE showing={WallpaperEngine.ShowingWallpaper()} brimLive={LiveWallpaperHost.Running} picture={System.IO.Path.GetFileName(Wallpapers.Current())} reappeared={reappeared} closedAgain={closedAgain}");
                if (!picture)
                {
                    // Stopping Brim's video is what brought the scene back before: watch that too
                    LiveWallpaperHost.Stop(refresh: true);
                    for (int i = 0; i < 16; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(500);
                        bool showing = WallpaperEngine.ShowingWallpaper();
                        if (showing && !was) { reappeared++; Line("after stopping the video: WE scene came back"); }
                        if (!showing && was) { closedAgain++; Line("after stopping the video: WE scene closed again"); }
                        was = showing;
                        WallpaperEngine.Guard();
                    }
                    Line($"after stop: WE showing={WallpaperEngine.ShowingWallpaper()} reappeared={reappeared} closedAgain={closedAgain}");
                }
                if (originalPicture != null) Wallpapers.ApplyPicture(originalPicture);
                if (original != null)
                {
                    var pj = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(original)!, "project.json");
                    var item = WallpaperEngine.Items().FirstOrDefault(x => string.Equals(x.ProjectJson, pj, StringComparison.OrdinalIgnoreCase));
                    if (item != null) WallpaperEngine.Open(item);
                    await System.Threading.Tasks.Task.Delay(3000);
                    Line($"restored: WE showing={WallpaperEngine.ShowingWallpaper()}");
                }
                System.IO.File.WriteAllText(e.Args[1], sb.ToString());
                Shutdown();
            });
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] is "--update-test" or "--mirror-test" or "--banners-test" or "--dock-screens-test" or "--endtask-test" or "--dock-apps-test" or "--accent-test" or "--hotkey-test" or "--memory-test" or "--calc-test" or "--admin-test" or "--lang-test" or "--notch-anim-test" or "--search-test")
        {
            // Developer checks for the newer features; each writes what happened to the file given
            Dispatcher.InvokeAsync(async () =>
            {
                SettingsStore.ReadOnly = true;
                var sb = new System.Text.StringBuilder();
                try
                {
                    switch (e.Args[0])
                    {
                        case "--notch-anim-test":
                        {
                            // How smoothly the notch opens: the time each frame took while it grows, for each surface
                            SettingsStore.Load();
                            ThemeManager.Apply();
                            foreach (var surface in new[] { "Solid", "Glass", "Clear" })
                            {
                                S.NotchSurface = surface;
                                var n = new Brim.Views.NotchWindow();
                                n.Show();
                                n.Left = -4000;
                                await System.Threading.Tasks.Task.Delay(1500);
                                for (int run = 0; run < 3; run++)
                                {
                                    var frames = new System.Collections.Generic.List<double>();
                                    EventHandler tick = (_, a) => frames.Add(((System.Windows.Media.RenderingEventArgs)a).RenderingTime.TotalMilliseconds);
                                    System.Windows.Media.CompositionTarget.Rendering += tick;
                                    await System.Threading.Tasks.Task.Delay(150); // a few calm frames first, to know the screen's pace
                                    int calm = frames.Count;
                                    var cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                                    var sw = System.Diagnostics.Stopwatch.StartNew();
                                    n.DebugOpen(Environment.GetEnvironmentVariable("BRIM_ANIM_TAB") ?? "Home");
                                    long call = sw.ElapsedMilliseconds;
                                    await System.Threading.Tasks.Task.Delay(600);
                                    System.Windows.Media.CompositionTarget.Rendering -= tick;
                                    var cpuMs = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
                                    var gaps = frames.Zip(frames.Skip(1), (a, b) => b - a).Where(g => g > 0.1).ToList();
                                    double pace = gaps.Take(Math.Max(1, calm - 1)).DefaultIfEmpty(16.7).Average();
                                    var during = gaps.Skip(Math.Max(0, calm - 1)).ToList();
                                    sb.AppendLine($"{surface} run {run + 1}: Open() took {call} ms; screen pace {pace:0.0} ms; while opening {during.Count} frames, " +
                                                  $"longest {during.DefaultIfEmpty(0).Max():0} ms, {during.Count(g => g > pace * 1.6)} late; CPU {cpuMs:0} ms");
                                    n.DebugClose();
                                    await System.Threading.Tasks.Task.Delay(700);
                                }
                                n.Close();
                            }
                            sb.AppendLine((Environment.GetEnvironmentVariable("BRIM_GPU") == "1" ? "graphics card" : "CPU drawing") + ": " + MemoryCare.Describe());
                            break;
                        }
                        case "--search-test":
                            // What Ctrl+Space finds among the settings for things people type
                            foreach (var q in new[] { "refresh rate", "hz", "pin", "mouse speed", "dark mode", "sticky keys", "dock left", "env", "screen timeout", "resolution", "drivers", "keyboard layout", "lyrics", "wifi password", "uninstall" })
                            {
                                var hits = SettingsIndex.All.Select(e => (e, s: SettingsIndex.Match(e, q))).Where(x => x.s > 0).OrderByDescending(x => x.s).Take(3);
                                sb.AppendLine($"{q,-16} → " + string.Join("  |  ", hits.Select(h => $"{h.e.Name} ({h.s})")));
                            }
                            break;
                        case "--lang-test":
                            foreach (var l in KeyboardLanguage.All()) sb.AppendLine($"installed: {l.Short} {l.Region} · {l.Name} (0x{l.Hkl.ToInt64():X})");
                            sb.AppendLine("now, in the window in front: " + (KeyboardLanguage.Current() is { } cur ? $"{cur.Short} {cur.Region} · {cur.Name}" : "unknown"));
                            break;
                        case "--admin-test":
                        {
                            // Which shortcuts and games offer "Run as administrator" (nothing is started)
                            SettingsStore.Load();
                            foreach (var s in S.Shortcuts)
                                sb.AppendLine($"shortcut {s.Name}: {(Launcher.CanRunAsAdmin(s.Path) ? "offered" : "not offered")} ({s.Path})");
                            await GameLibrary.Refresh();
                            foreach (var g in GameLibrary.Current)
                                sb.AppendLine($"game {g.Name} [{g.Source}]: {(GameLibrary.AdminExe(g) is { } x ? "offered, starts " + x : "not offered (no exe known)")}");
                            break;
                        }
                        case "--calc-test":
                        {
                            // The calculator tab's maths (with "." as the decimal point, whatever this PC uses)
                            var culture = System.Globalization.CultureInfo.CurrentCulture;
                            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
                            int fails = 0;
                            foreach (var (sum, want) in new (string, string?)[]
                            {
                                ("0.1+0.2", "0.3"), ("12×4", "48"), ("12×4+10%", "52.8"), ("200+10%", "220"), ("200−10%", "180"),
                                ("50%", "0.5"), ("50%×80", "40"), ("2^3^2", "512"), ("√9+1", "4"), ("2(3+4)", "14"), ("(1+2", "3"),
                                ("10÷4", "2.5"), ("-5×-2", "10"), ("1,000+1", "1001"), ("1/3*3", "1"), ("2 x 3", "6"), ("7−10", "-3"),
                                ("1÷0", null), ("3+", null), ("", null), ("√-4", null), ("abc", null), ("2..3", null),
                            })
                            {
                                var got = Calc.Evaluate(sum) is decimal v ? Calc.Format(v) : null;
                                bool ok = got == want;
                                if (!ok) fails++;
                                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {sum} = {got ?? "(nothing)"}{(ok ? "" : $"  (want {want ?? "nothing"})")}");
                            }
                            System.Globalization.CultureInfo.CurrentCulture = culture;
                            sb.Insert(0, fails == 0 ? "ALL PASSED\n" : $"{fails} FAILED\n");
                            break;
                        }
                        case "--memory-test":
                        {
                            // What the picture caches cost once everything has been opened, and what letting go gets back
                            Native.TrimMemory(collect: true);
                            sb.AppendLine("start:         " + MemoryCare.Describe());
                            foreach (var a in AppCatalog.All()) AppCatalog.Icon(a);
                            foreach (var p in Uninstaller.List()) _ = p.Icon;
                            await GameLibrary.Refresh();
                            foreach (var g in GameLibrary.Current) await GameLibrary.Cover(g);
                            sb.AppendLine("all loaded:    " + MemoryCare.Describe());
                            Native.TrimMemory(collect: true);
                            sb.AppendLine("trimmed only:  " + MemoryCare.Describe());
                            int n = MemoryCare.Release();
                            Native.TrimMemory(collect: true);
                            sb.AppendLine($"let go of {n}: " + MemoryCare.Describe());
                            break;
                        }
                        case "--hotkey-test":
                        {
                            // A shortcut another app already has: Brim must still get it. (Ctrl+Shift+Alt+F12, which
                            // nothing uses; the first window takes it so the second one is refused.)
                            const uint all = Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_ALT;
                            using var first = new MessageWindow();
                            using var second = new MessageWindow();
                            int firstGot = 0, secondGot = 0;
                            first.Hotkey += _ => firstGot++;
                            second.Hotkey += _ => secondGot++;
                            sb.AppendLine("first app gets it: " + first.Register(20, all, 0x7B, "test (first app)"));
                            sb.AppendLine("Brim gets it from Windows: " + second.Register(20, all, 0x7B, "test (Brim)"));
                            void Tap(byte vk, bool up) => Native.keybd_event(vk, 0, up ? 2u : 0u, UIntPtr.Zero);
                            for (int n = 0; n < 2; n++)
                            {
                                Tap(0x11, false); Tap(0x10, false); Tap(0x12, false); Tap(0x7B, false);
                                Tap(0x7B, true); Tap(0x12, true); Tap(0x10, true); Tap(0x11, true);
                                await System.Threading.Tasks.Task.Delay(300);
                            }
                            sb.AppendLine($"pressed twice: Brim saw it {secondGot} times, the other app {firstGot} times");
                            sb.AppendLine(secondGot == 2 && firstGot == 0 ? "PASS" : "FAIL");
                            // Win+9 (Windows keeps Win+number for its taskbar): Brim gets it, and Start must not open
                            int winGot = 0;
                            second.Hotkey += id => { if (id == 29) winGot++; };
                            sb.AppendLine("Brim gets Win+9 from Windows: " + second.Register(29, Native.MOD_WIN, '9', "test Win+9"));
                            Tap(0x5B, false); Tap((byte)'9', false); Tap((byte)'9', true); Tap(0x5B, true);
                            await System.Threading.Tasks.Task.Delay(700);
                            bool startOpened = await Native.CloseStartIfOpen();
                            sb.AppendLine($"Win+9: Brim saw it {winGot} times, Start opened: {startOpened}");
                            sb.AppendLine(winGot == 1 && !startOpened ? "PASS" : "FAIL");
                            var mics = Audio.MicMuted;
                            sb.AppendLine($"microphones: all muted {mics} (has a mic: {Audio.HasMic})");
                            Audio.MicMuted = mics; // (the same state again: nothing changes, the log says how many it reached)
                            break;
                        }
                        case "--accent-test":
                        {
                            // Give Windows a colour, then give its own back: the values must match exactly
                            var before = WindowsAccent.Describe();
                            sb.AppendLine("before:").AppendLine(before);
                            S.WindowsAccent = true; S.Accent = "#E3122F"; S.WindowsAccentBefore = null; S.WindowsAccentApplied = null;
                            WindowsAccent.Sync();
                            sb.AppendLine("with Brim's #E3122F:").AppendLine(WindowsAccent.Describe());
                            await System.Threading.Tasks.Task.Delay(1500);
                            S.WindowsAccent = false;
                            WindowsAccent.Sync();
                            var after = WindowsAccent.Describe();
                            sb.AppendLine("given back:").AppendLine(after);
                            sb.AppendLine("identical to before: " + (after == before));
                            break;
                        }
                        case "--update-test":
                            Updater.PretendVersion = "0.0.1";
                            bool wouldInstall = false;
                            Updater.InstallNow += () => wouldInstall = true; // (only noted: nothing is installed here)
                            await Updater.Check();
                            await System.Threading.Tasks.Task.Delay(500);
                            sb.AppendLine($"installs by itself right after start-up: {wouldInstall} (moment: {(Updater.QuietMoment(out var whyQuiet) ? "right" : "not yet")}, {whyQuiet})");
                            sb.AppendLine($"state {Updater.Now}, new version {Updater.NewVersion}, problem {Updater.Problem}");
                            if (Updater.ReadyFile is { } f && System.IO.File.Exists(f))
                            {
                                using var s = System.IO.File.OpenRead(f);
                                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
                                sb.AppendLine($"file {System.IO.Path.GetFileName(f)} {new System.IO.FileInfo(f).Length} bytes, matches release checksum: {hash.Equals(Updater.ExpectedHash, StringComparison.OrdinalIgnoreCase)}");
                            }
                            break;
                        case "--banners-test":
                        {
                            SettingsStore.Load(); // (read-only: nothing is saved)
                            string Val(string app) => S.MessageApps.TryGetValue(app, out var r) && r.AppId != null
                                ? $"{(Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + r.AppId)?.GetValue("ShowBanner") ?? "not set")}"
                                : "no id";
                            var apps = S.MessageApps.Keys.Where(a => S.MessageApps[a].AppId != null).ToList();
                            sb.AppendLine("before:    " + string.Join(", ", apps.Select(a => $"{a}={Val(a)}")));
                            S.NotchOnlyPopups = true; S.MessagePopups = true;
                            WindowsBanners.Sync();
                            sb.AppendLine("switch on: " + string.Join(", ", apps.Select(a => $"{a}={Val(a)}")) + $"  (Brim turned off: {string.Join(", ", S.BannersHidden)})");
                            WindowsBanners.RestoreAll();
                            sb.AppendLine("restored:  " + string.Join(", ", apps.Select(a => $"{a}={Val(a)}")));
                            break;
                        }
                        case "--dock-screens-test":
                        {
                            SettingsStore.Load();
                            S.DockReserve = false; S.DockAutoHide = false; // (read-only: nothing is saved)
                            ThemeManager.Apply();
                            var d1 = new Brim.Views.DockWindow();
                            var d2 = new Brim.Views.DockWindow(Native.Screens()[0].Device);
                            Brim.Views.DockWindow.Others.Add(d2);
                            d1.Show(); d2.Show();
                            await System.Threading.Tasks.Task.Delay(2500);
                            d1.RefreshApps(); d2.RefreshApps();
                            await System.Threading.Tasks.Task.Delay(500);
                            sb.AppendLine("main:  " + d1.DebugState);
                            sb.AppendLine("other: " + d2.DebugState);
                            d2.SetFullscreen(true);
                            await System.Threading.Tasks.Task.Delay(600);
                            sb.AppendLine("after fullscreen on the other's screen:");
                            sb.AppendLine("  main:  " + d1.DebugState);
                            sb.AppendLine("  other: " + d2.DebugState);
                            d2.SetFullscreen(false);
                            await System.Threading.Tasks.Task.Delay(600);
                            sb.AppendLine("back: other " + d2.DebugState);
                            // Full-width bar on both, then "Floating" in settings: both must go back to the pill
                            d1.DebugDocked(); d2.DebugDocked();
                            // (straight after: the dock's own refresh puts it back to a pill when nothing is maximised)
                            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                            sb.AppendLine("as bars:  main  " + d1.DebugState).AppendLine("          other " + d2.DebugState);
                            S.DockWhenMaximized = "Floating";
                            d1.ApplySettings(); d2.ApplySettings();
                            await System.Threading.Tasks.Task.Delay(800);
                            sb.AppendLine("floating: main  " + d1.DebugState).AppendLine("          other " + d2.DebugState);
                            d1.Close(); d2.Close();
                            break;
                        }                        case "--endtask-test":
                        {
                            var dummy = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -Command Start-Sleep 120") { CreateNoWindow = true, UseShellExecute = false })!;
                            await System.Threading.Tasks.Task.Delay(800);
                            Native.GetWindowThreadProcessId(Native.ExplorerTray(), out uint explorerPid); // (the real one: Brim's stand-in answers FindWindow first)
                            var mainBrim = System.Diagnostics.Process.GetProcessesByName("Brim").FirstOrDefault(x => x.Id != Environment.ProcessId);
                            var list = new System.Collections.Generic.List<AppWindow> { new() { Pid = (uint)dummy.Id, ExePath = Native.ProcessPath((uint)dummy.Id) }, new() { Pid = explorerPid, ExePath = Native.ProcessPath(explorerPid) }, new() { Pid = (uint)Environment.ProcessId, ExePath = Environment.ProcessPath } };
                            if (mainBrim != null) list.Add(new() { Pid = (uint)mainBrim.Id, ExePath = Native.ProcessPath((uint)mainBrim.Id) });
                            sb.AppendLine(Brim.Views.DockWindow.EndTaskTest(list));
                            await System.Threading.Tasks.Task.Delay(500);
                            sb.AppendLine($"throwaway process ended: {dummy.HasExited}; Explorer still running: {System.Diagnostics.Process.GetProcessesByName("explorer").Length > 0}; your Brim still running: {mainBrim == null || !mainBrim.HasExited}");
                            break;
                        }
                        case "--dock-apps-test":
                        {
                            SettingsStore.Load(); S.DockReserve = false; S.DockAutoHide = false; // (read-only)
                            ThemeManager.Apply();
                            var d = new Brim.Views.DockWindow { Left = -4000 };
                            d.Show();
                            await System.Threading.Tasks.Task.Delay(2000);
                            d.RefreshApps();
                            sb.AppendLine(d.DebugApps);
                            sb.AppendLine("pins after healing: " + string.Join(" ; ", S.DockPins.Select(p => p.Name + " -> " + p.Path)));
                            d.Close();
                            break;
                        }
                        case "--mirror-test":
                            ThemeManager.Apply();
                            var main = new Brim.Views.NotchWindow { Left = -4000 };
                            main.Show();
                            var copy = new Brim.Views.NotchWindow(Native.Screens()[0].Device);
                            Brim.Views.NotchWindow.Mirrors.Add(copy);
                            copy.Show();
                            await System.Threading.Tasks.Task.Delay(800);
                            main.ShowAction("", "Someone is live", "Just Chatting", () => { }, 5);
                            await System.Threading.Tasks.Task.Delay(600);
                            sb.AppendLine("main:  " + main.DebugState);
                            sb.AppendLine("other: " + copy.DebugState);
                            main.ShowActivity("", "Muted", "", 0, 2);
                            await System.Threading.Tasks.Task.Delay(300);
                            sb.AppendLine("after a mute pop-up, other: " + copy.DebugState);
                            break;
                    }
                }
                catch (Exception ex) { sb.AppendLine("FAIL " + ex); }
                System.IO.File.WriteAllText(e.Args[1], sb.ToString());
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--keys-test")
        {
            // Developer check: with factory settings (no keys of your own), do the built-in keys work?
            Dispatcher.InvokeAsync(async () =>
            {
                SettingsStore.UseFactorySettings();
                var sb = new System.Text.StringBuilder();
                try { sb.AppendLine($"video wallpapers: {(await Wallpapers.Videos(null, 1)).Count} found"); }
                catch (Exception ex) { sb.AppendLine("video wallpapers: FAIL " + ex.Message); }
                int bytes = await GameLibrary.CoverBytesFor("Bloodborne");
                sb.AppendLine(bytes > 0 ? $"cover for a game not on Steam: {bytes / 1024} KB" : "cover for a game not on Steam: FAIL");
                System.IO.File.WriteAllText(e.Args[1], sb.ToString());
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--download-test")
        {
            // Developer check: act like a browser downloading (a window on the dock + an open .crdownload file in
            // Downloads), photograph the dock, then clean up
            Dispatcher.InvokeAsync(async () =>
            {
                var w = new Window { Title = "Brim download test", Width = 300, Height = 120, Left = -32000, Top = -32000, ShowActivated = false };
                w.Show();
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                var file = System.IO.Path.Combine(dir, "brim-download-test.crdownload");
                using (var fs = new System.IO.FileStream(file, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read | System.IO.FileShare.Delete))
                {
                    for (int i = 0; i < 6; i++) { fs.Write(new byte[4096]); fs.Flush(); await System.Threading.Tasks.Task.Delay(500); }
                    var wa = SystemParameters.PrimaryScreenHeight;
                    Native.GetWindowRect(Native.FindWindow(null, "Brim Dock"), out var dock);
                    var shot = Native.CaptureScreen(dock, e.Args[1]);
                    System.IO.File.WriteAllText(e.Args[1] + ".txt", "dock " + shot);
                }
                System.IO.File.Delete(file);
                w.Close();
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--badge-test")
        {
            System.IO.File.WriteAllText(e.Args[1], MessageCenter.BadgeSelfTest());
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--favicon-test")
        {
            // Developer check: fetch the logo of every website in your shortcuts and report what came back
            SettingsStore.Load();
            SettingsStore.ReadOnly = true;
            Dispatcher.InvokeAsync(async () =>
            {
                var sb = new System.Text.StringBuilder();
                foreach (var s in SettingsStore.Current.Shortcuts.Where(x => IconHelper.IsUrl(x.Path)))
                {
                    var img = await Favicons.GetAsync(s.Path) as System.Windows.Media.Imaging.BitmapSource;
                    sb.AppendLine($"{s.Name}: {(img == null ? "no logo found" : $"logo {img.PixelWidth}x{img.PixelHeight}")}");
                }
                if (sb.Length == 0) sb.AppendLine("no website shortcuts");
                System.IO.File.WriteAllText(e.Args[1], sb.ToString());
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 3 && e.Args[0] == "--live-capture")
        {
            // Developer check: play a video wallpaper (forced to play) and photograph what the wallpaper layer shows,
            // twice, a second apart; then hand the desktop back to Wallpaper Engine as it was
            SettingsStore.Load();
            SettingsStore.ReadOnly = true;
            Environment.SetEnvironmentVariable("BRIM_LIVE_ALWAYS", "1"); // the --live process inherits it
            Dispatcher.InvokeAsync(async () =>
            {
                var sb = new System.Text.StringBuilder();
                var original = WallpaperEngine.CurrentFile();
                bool justLook = e.Args[2] == "none"; // only photograph what's there now (e.g. Wallpaper Engine's scene)
                if (!justLook)
                {
                    await WallpaperEngine.TakeOverAsync();
                    LiveWallpaperHost.Start(e.Args[2]);
                    await System.Threading.Tasks.Task.Delay(4000);
                }
                else original = null;
                IntPtr live = IntPtr.Zero;
                var progman = Native.FindWindow("Progman", null);
                var worker = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                sb.AppendLine($"mode: {Environment.GetEnvironmentVariable("BRIM_LIVE_MODE") ?? "inside WorkerW"}, gpu: {Environment.GetEnvironmentVariable("BRIM_LIVE_GPU") ?? "0"}");
                foreach (var parent in new[] { progman, worker })
                    for (var c = Native.FindWindowEx(parent, IntPtr.Zero, null, null); c != IntPtr.Zero; c = Native.FindWindowEx(parent, c, null, null))
                        if (Native.TitleOf(c) == "Brim live wallpaper") live = c;
                sb.AppendLine("inside: " + Native.ClassOf(Native.GetAncestor(live, 1 /* GA_PARENT */)));
                Native.GetWindowRect(live, out var lr);
                Native.GetWindowRect(Native.GetAncestor(live, 1), out var pr);
                sb.AppendLine($"video window on screen: {lr.Left},{lr.Top} → {lr.Right},{lr.Bottom}; its parent: {pr.Left},{pr.Top} → {pr.Right},{pr.Bottom}; " +
                              $"style 0x{Native.GetWindowLongPtr(live, -16).ToInt64():X} ex 0x{Native.GetWindowLongPtr(live, -20).ToInt64():X}");
                sb.AppendLine("live window: " + live + " visible=" + Native.IsWindowVisible(live));
                // Windows' own live preview of the desktop (what Alt+Tab shows), in a small box in the corner for a moment
                var wa = SystemParameters.WorkArea;
                var box = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
                                       Width = 400, Height = 250, Left = wa.Right - 410, Top = wa.Bottom - 260, Background = System.Windows.Media.Brushes.Black, Title = "Brim desktop check" };
                box.Show();
                var boxHwnd = new System.Windows.Interop.WindowInteropHelper(box).Handle;
                Native.GetWindowRect(boxHwnd, out var boxRect);
                var thumb = Native.ShowLivePreview(boxHwnd, progman, boxRect.Right - boxRect.Left, boxRect.Bottom - boxRect.Top);
                sb.AppendLine("live preview: " + (thumb != IntPtr.Zero ? "on" : "failed"));
                for (int i = 0; i < 2; i++)
                {
                    await System.Threading.Tasks.Task.Delay(i == 0 ? 600 : 1000);
                    sb.AppendLine($"desktop as shown {i}: " + Native.CaptureScreen(boxRect, System.IO.Path.Combine(e.Args[1], $"shown-{i}.png")));
                }
                Native.HideLivePreview(thumb);
                box.Close();
                LiveWallpaperHost.Stop(refresh: false);
                if (original != null)
                {
                    var pj = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(original)!, "project.json");
                    var item = WallpaperEngine.Items().FirstOrDefault(x => string.Equals(x.ProjectJson, pj, StringComparison.OrdinalIgnoreCase));
                    if (item != null) WallpaperEngine.Open(item);
                    await System.Threading.Tasks.Task.Delay(3000);
                    sb.AppendLine($"restored Wallpaper Engine: {WallpaperEngine.ShowingWallpaper()}");
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "live.txt"), sb.ToString());
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--pad-test")
        {
            try
            {
                SonyPads.DebugOn = true;
                var lines = SonyPads.Read().ConvertAll(p => $"{p.Name} battery={p.Battery} charging={p.Charging} wireless={p.Wireless}");
                System.IO.File.WriteAllText(e.Args[1], (lines.Count == 0 ? "none" : string.Join("\n", lines)) + "\n" + SonyPads.Debug);
            }
            catch (Exception ex) { System.IO.File.WriteAllText(e.Args[1], ex.ToString()); }
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--desktop-test")
        {
            // Developer check: focus the desktop for a moment and record what Brim's checks decide
            Dispatcher.InvokeAsync(async () =>
            {
                var sb = new System.Text.StringBuilder();
                var before = Native.GetForegroundWindow();
                void Snap(string when)
                {
                    var fg = Native.GetForegroundWindow();
                    Native.GetWindowThreadProcessId(fg, out uint pid);
                    sb.AppendLine($"{when}: fg={Native.ClassOf(fg)} '{Native.TitleOf(fg)}' exe={System.IO.Path.GetFileName(Native.ProcessPath(pid) ?? "?")} " +
                                  $"visible={Native.IsWindowVisible(fg)} appInFront={Native.AppInFront()} fullscreen={WindowTracker.ForegroundIsFullscreen()} desktopRaised={Native.DesktopRaised()}");
                }
                Snap("before");
                Native.ForceForeground(Native.DesktopHost());
                await System.Threading.Tasks.Task.Delay(1500);
                Snap("desktop");
                System.IO.File.WriteAllText(e.Args[1], sb.ToString());
                await System.Threading.Tasks.Task.Delay(1500);
                Native.ForceForeground(before);
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--menu-test")
        {
            // Developer check: open the "+ Add line" dropdown for real in the bottom-right corner and note where it is
            SettingsStore.Load();
            SettingsStore.ReadOnly = true;
            ThemeManager.Apply();
            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var set = new SettingsWindow { Left = SystemParameters.WorkArea.Right - 300, Top = SystemParameters.WorkArea.Bottom - 120, Width = 300, Height = 120, WindowStartupLocation = WindowStartupLocation.Manual };
                    var btn = new System.Windows.Controls.Button { Content = "test" };
                    set.Content = btn;
                    set.Show();
                    await System.Threading.Tasks.Task.Delay(300);
                    var menu = set.AddLineMenu(btn, _ => { }, open: true);
                    await System.Threading.Tasks.Task.Delay(800);
                    string rect = "";
                    if (PresentationSource.FromVisual(menu) != null)
                    {
                        var tl = menu.PointToScreen(new Point(0, 0));
                        var br = menu.PointToScreen(new Point(menu.ActualWidth, menu.ActualHeight));
                        rect = $"{(int)tl.X} {(int)tl.Y} {(int)(br.X - tl.X)} {(int)(br.Y - tl.Y)}";
                    }
                    System.IO.File.WriteAllText(e.Args[1] + ".txt", $"{rect}\nopen={menu.IsOpen} items={menu.Items.Count}");
                    await System.Threading.Tasks.Task.Delay(2500);
                    menu.IsOpen = false;
                    set.Close();
                }
                catch (Exception ex) { System.IO.File.WriteAllText(e.Args[1] + ".txt", ex.ToString()); }
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--preview-test")
        {
            SettingsStore.Load();
            SettingsStore.ReadOnly = true;
            ThemeManager.Apply();
            Dispatcher.InvokeAsync(async () =>
            {
                try { await WindowPreview.SelfTest(e.Args[1]); }
                catch (Exception ex) { System.IO.File.WriteAllText(e.Args[1] + ".txt", ex.ToString()); }
                Shutdown();
            });
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--drag-snapshot")
        {
            SettingsStore.Load();
            Dispatcher.InvokeAsync(async () =>
            {
                try { await Snapshot.DragShots(e.Args[1]); }
                catch (Exception ex) { Log.Error("drag snapshot", ex); System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "error.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--lyrics-snapshot")
        {
            SettingsStore.Load();
            Dispatcher.InvokeAsync(async () =>
            {
                try { await Snapshot.LyricsShots(e.Args[1]); }
                catch (Exception ex) { Log.Error("lyrics snapshot", ex); }
                Shutdown();
            });
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--snapshot")
        {
            SettingsStore.Load();
            Dispatcher.InvokeAsync(async () =>
            {
                try { await Snapshot.Run(e.Args[1]); }
                catch (Exception ex) { Log.Error("snapshot", ex); }
                Shutdown();
            });
            return;
        }
        if (e.Args.Length >= 1 && e.Args[0] == "--uninstall")
        {
            SettingsStore.Load();
            bool quiet = e.Args.Contains("--quiet");
            if (!quiet && MessageBox.Show("Remove Brim from this PC?\n\nYour Windows taskbar, desktop icons and wallpaper settings will be put back.",
                    "Uninstall Brim", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            { Shutdown(); return; }
            bool keep = quiet || MessageBox.Show("Keep your Brim settings (layout, widgets, dock, games) in case you install it again?",
                "Uninstall Brim", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            Installer.Uninstall(keep);
            if (!quiet) MessageBox.Show("Brim has been removed.", "Uninstall Brim", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Started from a download (not the installed copy): offer to install, or update the installed one
        // ("Brim-Setup.exe --install" installs or updates without asking; "--after <pid>" first waits for that Brim
        // to close, which is how a running Brim hands over to its update)
        bool quietInstall = e.Args.Length >= 1 && e.Args[0] == "--install";
        if (quietInstall && e.Args.Length == 3 && e.Args[1] == "--after" && int.TryParse(e.Args[2], out int waitPid))
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(waitPid); old.WaitForExit(30000); }
            catch { } // already gone
        }
        if ((e.Args.Length == 0 || quietInstall) && !Installer.RunningInstalled && (!Installer.IsDevCopy || quietInstall))
        {
            bool update = Installer.IsInstalled;
            var answer = quietInstall ? MessageBoxResult.Yes : MessageBox.Show(update
                    ? "Brim is already installed. Update it with this version?"
                    : "Install Brim?\n\nIt will be added to Start and to Settings › Apps (where you can uninstall it). No admin rights needed.",
                "Brim " + Installer.Version, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    if (update) Installer.CloseRunning();
                    var exe = Installer.Install();
                    Log.Note("install", $"{(update ? "updated" : "installed")} {Installer.Version} from {Environment.ProcessPath} → {exe}");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Installer.InstallDir });
                }
                catch (Exception ex)
                {
                    Log.Error("install", ex);
                    MessageBox.Show("Couldn't install Brim: " + ex.Message, "Brim", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                Shutdown();
                return;
            }
        }

        // Switching from another copy: wait for it to quit (it puts the taskbar back as it goes) before taking over
        if (e.Args.Length == 2 && e.Args[0] == "--wait-for" && int.TryParse(e.Args[1], out int oldPid))
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(oldPid); old.WaitForExit(15000); }
            catch { } // already gone
        }

        single = new Mutex(true, "Brim.Desktop.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }
        owner = true;

        DispatcherUnhandledException += (_, ex) => { Log.Error("ui", ex.Exception); ex.Handled = true; };
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler(ProportionalWheel));
        HoverTip.Install();
        HoverMenu.Install(); // every menu closes itself once the pointer has left it and what it came from
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Error("fatal", (Exception)ex.ExceptionObject);
            Restore();
        };

        var processStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;

        SettingsStore.Load();
        FirstRun();
        ThemeManager.Apply();
        TaskbarManager.Init();
        TaskbarManager.Peeking += time => { foreach (var d in AllDocks) d.StepAside(time); };
        SystemInfo.Sample();

        messages = new MessageWindow();
        messages.Hotkey += OnHotkey;
        ApplyHotkeys();
        WidgetWindow.WentOnTop += () => { notch?.EnsureTopmost(); foreach (var d in AllDocks) d.EnsureTopmost(); };
        AutoLook.Changed += () => ApplyAll();
        AutoLook.Start();
        WeatherService.Updated += () => { if (S.AutoTheme) AutoLook.Check(); };
        // Plugging in / unplugging changes whether Wallpaper Engine should play
        var powerWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        powerWatch.Tick += (_, _) => LivePolicy.ApplyToWallpaperEngine();
        powerWatch.Start();

        notch = new NotchWindow();
        notch.Show();
        notch.ContentRendered += (_, _) =>
            Log.Note("startup", $"on screen {(DateTime.Now - processStart).TotalMilliseconds:0} ms after launch" +
                                $" ({(Installer.RunningInstalled ? "installed copy" : "running from " + System.IO.Path.GetDirectoryName(Environment.ProcessPath))})");

        ApplyAll();

        tray = new TrayIcon(this);

        second.Tick += (_, _) => { SystemInfo.Sample(); SecondTick?.Invoke(); };
        second.Start();
        shell.Tick += (_, _) => ShellTick();
        shell.Start();
        HookWindowEvents();
        trimTimer.Tick += (_, _) => { trimTimer.Stop(); Native.TrimMemory(collect: true); };
        idleTrim.Tick += (_, _) => IdleTrim();
        idleTrim.Start();

        // Everything below isn't needed to draw the notch and dock, so it starts once they're on screen
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _ = MediaService.Instance.InitAsync();
            Lyrics.Start();
            messages.ClipboardChanged += ClipboardHistory.OnChanged;
            ClipboardHistory.Load();
            ClipboardHistory.ImageCaptured += OnClipboardImage;
            DeviceBattery.Connected += d => { if (S.LiveActivities) notch?.ShowActivity(d.Glyph, d.Name + " connected", d.Battery >= 0 ? $"{d.Battery}%" : "", d.Battery >= 0 ? d.Battery / 100.0 : null, 2.5); };
            DeviceBattery.Low += d => notch?.ShowActivity(d.Glyph, d.Name + " battery low", $"{d.Battery}%", d.Battery / 100.0, 4,
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B)));
            DeviceBattery.Start();
            PlayTime.Start();
            OledCare.Start();
            MessageCenter.Arrived += m =>
            {
                if (!S.MessagePopups || notch == null) { Log.Note("messages", "pop-up off in settings"); return; }
                if (MessageCenter.MutedUntil is DateTime until) { Log.Note("messages", $"pop-up snoozed until {until:HH:mm}"); return; }
                var shown = notch.ShowMessage(m.Sender, m.Text.Length > 0 ? $"{m.App} · {m.Text}" : m.App, m.Icon);
                Log.Note("messages", $"pop-up for {m.App}: {shown}");
            };
            MessageCenter.Sync();
            Downloads.Start(); // the dock's download badge
            Updater.Ready += () =>
            {
                // Installing by itself: it waits for a quiet moment, so there's nothing to ask
                if (S.AutoInstallUpdates) return;
                Log.Note("update", "pop-up: " + (notch?.ShowAction("", $"Brim {Updater.NewVersion} is ready", "Click to restart and update",
                    InstallUpdate, 10) ?? "no notch"));
            };
            Updater.InstallNow += InstallUpdate;
            Updater.Start();
            SayIfUpdated();
            WindowsBanners.Start(); // "only pop up in the notch"
            Backdrop.Watch(ApplyAll); // glass falls back to a tint while Windows won't blur (transparency off, energy saver)
            // A Wallpaper Engine video Brim used to play itself: hand it to Wallpaper Engine (it plays its own better)
            if (S.LiveWallpaper != null && WallpaperEngine.Installed &&
                WallpaperEngine.Items().FirstOrDefault(i => string.Equals(i.File, S.LiveWallpaper, StringComparison.OrdinalIgnoreCase)) is { } weVideo)
            {
                Log.Note("wallpaper", $"\"{weVideo.Title}\" is a Wallpaper Engine video: Wallpaper Engine shows it now");
                S.LiveWallpaper = null;
                SettingsStore.Save();
                WallpaperEngine.HandBack();
                WallpaperEngine.Open(weVideo);
            }
            if (S.LiveWallpaper != null)
            {
                if (LiveWallpaperHost.IsValid(S.LiveWallpaper)) LiveWallpaperHost.Start(S.LiveWallpaper);
                else { S.LiveWallpaper = null; SettingsStore.Save(); }
            }
            // Find games in the background and keep watching for new installs
            Dispatcher.BeginInvoke(new Action(() => { _ = GameLibrary.Refresh(); }), DispatcherPriority.ApplicationIdle);
            ScheduleTrim();
        }), DispatcherPriority.ContextIdle);

        if (!S.FirstRunDone)
        {
            S.FirstRunDone = true;
            SettingsStore.Save();
            OpenSettings();
        }
    }

    void FirstRun()
    {
        if (S.FirstRunDone) return;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        void Add(string name, string path)
        {
            if (path.Contains(':') && !path.StartsWith("ms-") && !System.IO.File.Exists(path)) return;
            S.Shortcuts.Add(new ShortcutItem { Name = name, Path = path });
        }
        Add("File Explorer", System.IO.Path.Combine(win, "explorer.exe"));
        Add("Task Manager", System.IO.Path.Combine(win, "System32", "Taskmgr.exe"));
        Add("Calculator", System.IO.Path.Combine(win, "System32", "calc.exe"));
        Add("Notepad", System.IO.Path.Combine(win, "System32", "notepad.exe"));
        S.Widgets.Add(new WidgetItem { Kind = WidgetKind.Clock });
        S.Widgets.Add(new WidgetItem { Kind = WidgetKind.Music });
        SettingsStore.Save();
    }

    /// <summary>Push the current settings out to every part of the app.</summary>
    public void ApplyAll()
    {
        try
        {
            ThemeManager.Apply();
            SyncNotches();
            notch?.ApplySettings();

            TaskbarManager.Apply(S.Taskbar);
            if (S.Taskbar == TaskbarMode.Dock)
            {
                if (TrayService.Instance == null)
                {
                    TrayService.Start();
                    tray?.Readd();
                }
                if (dock == null)
                {
                    dock = new DockWindow();
                    dock.Show();
                }
                else dock.ApplySettings();
                SyncDocks(apply: true);
            }
            else
            {
                if (TrayService.Instance != null)
                {
                    TrayService.Stop();
                    tray?.Readd();
                }
                if (dock != null)
                {
                    dock.Close();
                    dock = null;
                }
                SyncDocks(); // closes the other screens' docks too
            }

            widgets.Sync();
            DesktopIcons.Set(!S.HideDesktopIcons);
            Core.Startup.Set(S.StartWithWindows);
            OledCare.Apply();
            WeatherService.Refresh();
            MessageCenter.Sync();
            // Win+1…9 come and go with the dock
            if (S.Taskbar != keysForTaskbar) { keysForTaskbar = S.Taskbar; ApplyHotkeys(); }
            SyncDockAttention();
        }
        catch (Exception ex) { Log.Error("apply", ex); }
    }

    TaskbarMode? keysForTaskbar;
    AttentionWatcher? dockAttention;

    /// <summary>Apps asking for attention (a finished download, an installer waiting, a call) flash orange on the dock.</summary>
    void SyncDockAttention()
    {
        if (S.Taskbar == TaskbarMode.Dock && dockAttention == null)
            dockAttention = new AttentionWatcher((hwnd, flash) =>
            {
                if (!flash || !DockWindow.WantsAttention(hwnd)) return;
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                Log.Note("dock", $"{System.IO.Path.GetFileName(Native.ProcessPath(pid) ?? "?")} asked for attention ('{Native.TitleOf(hwnd)}')");
                foreach (var d in AllDocks) d.RefreshApps();
            });
        else if (S.Taskbar != TaskbarMode.Dock && dockAttention != null) { dockAttention.Dispose(); dockAttention = null; }
    }

    static uint PidOf(IntPtr h) { Native.GetWindowThreadProcessId(h, out uint pid); return pid; }

    public void ResetWidgetPositions()
    {
        widgets.ResetPositions();
        SettingsStore.Save();
    }

    /// <summary>
    /// WPF scrolls a fixed three lines per wheel message. Touchpads send many small messages, so that raced;
    /// scroll by the distance actually moved instead (one mouse-wheel notch still moves the usual amount).
    /// </summary>
    static void ProportionalWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not System.Windows.Controls.ScrollViewer sv) return;
        // Only the innermost scrollable area under the pointer should move
        DependencyObject? d = e.OriginalSource as DependencyObject;
        System.Windows.Controls.ScrollViewer? target = null;
        while (d != null)
        {
            if (d is System.Windows.Controls.ScrollViewer s && s.ScrollableHeight > 0 && s.VerticalScrollBarVisibility != System.Windows.Controls.ScrollBarVisibility.Disabled) { target = s; break; }
            d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        if (target != sv) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 0.4);
        e.Handled = true;
    }

    // ---------- instant reactions: Windows tells us when focus or windows change, instead of us polling ----------

    Native.WinEventDelegate? winEventProc;
    readonly System.Collections.Generic.List<IntPtr> winHooks = new();
    readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromMilliseconds(60) };

    void HookWindowEvents()
    {
        winEventProc = OnWinEvent;
        settle.Tick += (_, _) => { settle.Stop(); React(); };
        const uint outOfContext = 0x0000, skipOwnProcess = 0x0002;
        foreach (var (min, max) in new (uint, uint)[]
        {
            (0x0003, 0x0003), // foreground changed
            (0x0016, 0x0017), // minimise start / end
            (0x8002, 0x8003), // window shown / hidden
            (0x800B, 0x800B), // window moved or resized (a video or browser going fullscreen without a focus change)
        })
            winHooks.Add(Native.SetWinEventHook(min, max, IntPtr.Zero, winEventProc, 0, 0, outOfContext | skipOwnProcess));
        fsCheck.Tick += (_, _) => { fsCheck.Stop(); FullscreenNow(); };
    }

    // Resizes come in bursts (60 a second while dragging a window): look at most every 40 ms, always once at the end
    readonly DispatcherTimer fsCheck = new() { Interval = TimeSpan.FromMilliseconds(40) };
    DateTime fsCheckedAt;

    void FullscreenSoon()
    {
        if (DateTime.Now - fsCheckedAt > TimeSpan.FromMilliseconds(40)) FullscreenNow();
        else if (!fsCheck.IsEnabled) fsCheck.Start();
    }

    void FullscreenNow()
    {
        fsCheckedAt = DateTime.Now;
        ApplyFullscreen();
        CheckAppInFront(); // a window dragged or resized into (or out of) the notch's spot
    }

    string lastFullscreenWhy = "";

    /// <summary>Hide the notch and dock on each screen a game or video covers (the other screens keep theirs).</summary>
    void ApplyFullscreen()
    {
        if (Native.SwitcherInFront()) return; // (see React)
        var covered = WindowTracker.FullscreenMonitors(out var why);
        // What decided each screen, when a screen goes fullscreen or stops being it (to see why a notch showed
        // over a game); not on every switch between ordinary apps
        var key = string.Join(",", covered.OrderBy(m => (long)m));
        if (key != lastFullscreenWhy) { lastFullscreenWhy = key; Log.Note("fullscreen", why); }
        foreach (var n in AllNotches) n.SetFullscreenHidden(S.HideInFullscreen && covered.Contains(n.Monitor));
        foreach (var d in AllDocks) d.SetFullscreen(covered.Contains(d.Monitor));
        HotkeysForFullscreen(covered);
    }

    int winEvents, reacts;
    DateTime countedAt = DateTime.Now;

    /// <summary>Once a minute, how busy the system signals were (for spotting what keeps Brim busy).</summary>
    void CountActivity()
    {
        if (DateTime.Now - countedAt < TimeSpan.FromMinutes(1)) return;
        // Only worth a line when something is unusually busy
        if (winEvents > 600 || AttentionWatcher.Signals > 300 || TrayService.IconMessages > 600)
            Log.Note("activity", $"busy minute: {winEvents} window events, {reacts} reactions, {AttentionWatcher.Signals} shell signals, {TrayService.IconMessages} tray updates");
        winEvents = reacts = 0;
        AttentionWatcher.Signals = 0;
        TrayService.IconMessages = 0;
        countedAt = DateTime.Now;
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return; // only whole windows (not the caret, cursor, scrollbars…)
        if (ev == 0x800B)
        {
            // Only the window you're using can go fullscreen on you; everything else moving around doesn't matter
            if (hwnd == Native.GetForegroundWindow()) FullscreenSoon();
            return;
        }
        winEvents++;
        if (ev == 0x0003)
        {
            // focus change: act now, then check the taskbar again shortly (Explorer shows it a beat after Start opens)
            React();
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await System.Threading.Tasks.Task.Delay(200); TaskbarManager.Enforce();
                await System.Threading.Tasks.Task.Delay(400); TaskbarManager.Enforce();
            }));
            return;
        }
        if (Native.GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;
        if (ev == 0x8002 && Native.ClassOf(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") { TaskbarManager.Enforce(); return; }
        settle.Stop();                                   // show/hide come in bursts: act once they settle
        settle.Start();
    }

    /// <summary>
    /// "Hide while an app covers it": each screen's notch tucks away while an app window there is maximised or
    /// reaches up into the notch's spot. A smaller window elsewhere on the desktop leaves it showing, focused or not.
    /// </summary>
    void CheckAppInFront()
    {
        if (!S.HideOverApps || notch == null) return;
        if (Native.AppInFront() is null) return; // Start, Search, Brim's own pop-ups: no change
        if (Native.DesktopRaised()) { foreach (var n in AllNotches) n.SetAppInFront(false, mirrorsToo: false); return; }
        var windows = WindowTracker.TaskbarWindows();
        foreach (var n in AllNotches) n.SetAppInFront(CoversNotch(n, windows), mirrorsToo: false);
    }

    static bool CoversNotch(Brim.Views.NotchWindow n, System.Collections.Generic.List<AppWindow> windows)
    {
        var zone = n.Zone();
        var mon = n.Monitor;
        foreach (var w in windows)
        {
            if (Native.IsIconic(w.Hwnd)) continue;
            bool zoomed = Native.IsZoomed(w.Hwnd);
            // Brim's own Settings and Wallpapers windows: you're setting up the desktop (unless they're maximised)
            if (w.Pid == (uint)Environment.ProcessId && !zoomed) continue;
            if (WindowCovers(zone, mon, Native.VisibleRect(w.Hwnd), zoomed, Native.MonitorFromWindow(w.Hwnd, 2))) return true;
        }
        return false;
    }

    /// <summary>A window covers the notch: maximised on its screen, or reaching into its spot.</summary>
    internal static bool WindowCovers(Native.RECT zone, IntPtr notchScreen, Native.RECT r, bool zoomed, IntPtr windowScreen) =>
        zoomed && windowScreen == notchScreen ||
        r.Left < zone.Right && r.Right > zone.Left && r.Top < zone.Bottom && r.Bottom > zone.Top;

    /// <summary>`--cover-test out.txt`: the cover rules on made-up windows, then on the windows open right now.</summary>
    static string CoverTest(Brim.Views.NotchWindow n)
    {
        var sb = new System.Text.StringBuilder();
        int fails = 0;
        var zone = n.Zone();
        var mon = n.Monitor;
        var other = new IntPtr(-1);
        Native.RECT R(int l, int t, int w, int h) => new() { Left = l, Top = t, Right = l + w, Bottom = t + h };
        int cx = (zone.Left + zone.Right) / 2;
        void Expect(string name, bool got, bool want) { if (got != want) fails++; sb.AppendLine($"{(got == want ? "ok  " : "FAIL")} {name}: {got}"); }
        Expect("small window in the middle of the desktop", WindowCovers(zone, mon, R(cx - 300, 300, 600, 400), false, mon), false);
        Expect("small window off to the side, at the very top", WindowCovers(zone, mon, R(zone.Left - 700, 0, 500, 400), false, mon), false);
        Expect("window dragged up under the notch", WindowCovers(zone, mon, R(cx - 200, zone.Bottom - 10, 400, 300), false, mon), true);
        Expect("window just below the notch", WindowCovers(zone, mon, R(cx - 200, zone.Bottom + 2, 400, 300), false, mon), false);
        Expect("maximised on this screen", WindowCovers(zone, mon, R(0, 0, 10, 10), true, mon), true);
        Expect("maximised on another screen", WindowCovers(zone, mon, R(-3000, 0, 1920, 1040), true, other), false);
        sb.AppendLine($"\nnotch spot: {zone.Left},{zone.Top} – {zone.Right},{zone.Bottom}");
        foreach (var w in WindowTracker.TaskbarWindows().Where(w => !Native.IsIconic(w.Hwnd)))
        {
            var r = Native.VisibleRect(w.Hwnd);
            bool c = WindowCovers(zone, mon, r, Native.IsZoomed(w.Hwnd), Native.MonitorFromWindow(w.Hwnd, 2));
            sb.AppendLine($"     now: {System.IO.Path.GetFileName(w.ExePath ?? "?")} at {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}{(Native.IsZoomed(w.Hwnd) ? " maximised" : "")} → {(c ? "covers" : "leaves it showing")}");
        }
        sb.Insert(0, fails == 0 ? "ALL PASSED\n" : $"{fails} FAILED\n");
        return sb.ToString();
    }

    void React()
    {
        // Alt+Tab, Task View or a swipe in progress: hold everything still until it's done (Brim used to slide the
        // notch and dock out and back under Windows' own animation, which made gestures stutter)
        if (Native.SwitcherInFront()) return;
        reacts++;
        var started = System.Diagnostics.Stopwatch.StartNew();
        LivePolicy.ApplyToWallpaperEngine();
        CheckAppInFront();
        ApplyFullscreen();
        TaskbarManager.Enforce();
        foreach (var d in AllDocks) d.RefreshApps();
        widgets.CheckDesktop();
        // A reaction should take a few ms; note the slow ones (a frame is 16 ms)
        if (started.ElapsedMilliseconds > 40 && DateTime.Now - slowNotedAt > TimeSpan.FromMinutes(1))
        {
            slowNotedAt = DateTime.Now;
            Log.Note("activity", $"reacting to a window change took {started.ElapsedMilliseconds} ms");
        }
    }

    DateTime slowNotedAt;

    void ShellTick()
    {
        shellTicks++;
        CountActivity();
        // Minimising the last window or clicking the desktop doesn't always send a focus-change event
        CheckAppInFront();
        ApplyFullscreen();
        TaskbarManager.Enforce();
        widgets.CheckDesktop();
        if (shellTicks % 4 == 0) TrayService.Instance?.KeepInFront(); // every 2 s is plenty
        // Wallpaper Engine puts its wallpaper back on its own (desktop changes, restarts): keep Brim's showing
        if (WallpaperEngine.Guard() && S.LiveWallpaper != null) SetLiveWallpaper(null, refresh: false);
        if (shellTicks % 6 == 0)
        {
            notch?.EnsureTopmost();
            foreach (var d in AllDocks) { d.EnsureTopmost(); d.CheckReservation(); } // "keep windows above the dock" holds even if Explorer drops the strip
            if (S.Taskbar == TaskbarMode.Dock && S.DockScreen == "*") SyncDocks(); // a screen plugged in or out
            if (S.NotchScreen == "*") SyncNotches(); // a screen plugged in or out
            if (S.HideDesktopIcons) DesktopIcons.Set(false);
        }
    }

    // ---------- a dock on every screen ("Every screen" in Taskbar › Screen, the default)

    System.Collections.Generic.IEnumerable<DockWindow> AllDocks =>
        dock == null ? DockWindow.Others.ToList() : DockWindow.Others.Prepend(dock).ToList();

    /// <param name="apply">Settings changed: the other screens' docks take them too. (The 3-second screen check only
    /// adds and removes docks; re-applying there cleared their icons and re-reserved their strip each time.)</param>
    void SyncDocks(bool apply = false)
    {
        var others = dock != null && S.DockScreen == "*" ? Native.Screens().Where(s => !s.Primary).Select(s => s.Device).ToList() : new System.Collections.Generic.List<string>();
        foreach (var d in DockWindow.Others.Where(d => !others.Contains(d.ScreenDevice!, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            DockWindow.Others.Remove(d);
            d.Close();
        }
        foreach (var device in others.Where(x => !DockWindow.Others.Any(d => string.Equals(d.ScreenDevice, x, StringComparison.OrdinalIgnoreCase))))
        {
            var d = new DockWindow(device);
            DockWindow.Others.Add(d);
            d.Show();
            Log.Note("dock", $"added a dock on {device}");
        }
        // New docks apply their settings once they load
        if (apply) foreach (var d in DockWindow.Others.Where(d => d.IsLoaded)) d.ApplySettings();
    }

    // ---------- a notch on every screen ("Every screen" in Notch › Screen)

    /// <summary>Make sure there's a notch on each other screen when "Every screen" is on (and none when it's off).</summary>
    void SyncNotches()
    {
        if (notch == null) return;
        var others = S.NotchScreen == "*" ? Native.Screens().Where(s => !s.Primary).Select(s => s.Device).ToList() : new System.Collections.Generic.List<string>();
        foreach (var m in Brim.Views.NotchWindow.Mirrors.Where(m => !others.Contains(m.ScreenDevice!, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            Brim.Views.NotchWindow.Mirrors.Remove(m);
            m.Close();
        }
        foreach (var device in others.Where(d => !Brim.Views.NotchWindow.Mirrors.Any(m => string.Equals(m.ScreenDevice, d, StringComparison.OrdinalIgnoreCase))))
        {
            var m = new Brim.Views.NotchWindow(device);
            Brim.Views.NotchWindow.Mirrors.Add(m);
            m.Show();
            Log.Note("notch", $"added a notch on {device}");
        }
    }

    System.Collections.Generic.IEnumerable<Brim.Views.NotchWindow> AllNotches =>
        notch == null ? Enumerable.Empty<Brim.Views.NotchWindow>() : Brim.Views.NotchWindow.Mirrors.Prepend(notch);

    /// <summary>The notch on the screen the pointer is on (for Ctrl+Alt+N).</summary>
    Brim.Views.NotchWindow? NotchUnderPointer()
    {
        if (Brim.Views.NotchWindow.Mirrors.Count == 0) return notch;
        Native.GetCursorPos(out var pt);
        var mon = Native.MonitorFromPoint(pt, 2);
        return AllNotches.FirstOrDefault(n => n.Monitor == mon) ?? notch;
    }

    void OnHotkey(int id)
    {
        switch (id)
        {
            case 1:
                NotchUnderPointer()?.ToggleOpen();
                break;
            case >= 2 and <= 10:
                int index = id - 2;
                if (index < S.Shortcuts.Count) Launcher.Open(S.Shortcuts[index]);
                break;
            case 11:
                S.HideDesktopIcons = !S.HideDesktopIcons;
                SettingsStore.Save();
                DesktopIcons.Set(!S.HideDesktopIcons);
                RefreshSettingsWindow();
                break;
            case 12:
                MediaService.Instance.PlayPause();
                break;
            case 13:
            {
                // Colour picker: copy the colour under the pointer as #RRGGBB
                var (r, g, b) = ColorPicker.UnderPointer();
                var hex = $"#{r:X2}{g:X2}{b:X2}";
                try { Clipboard.SetText(hex); } catch { }
                notch?.ShowActivity("●", "Copied " + hex, $"{r}, {g}, {b}", null, 2.5,
                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)), symbolFont: true);
                break;
            }
            case 14:
                Audio.MicMuted = !Audio.MicMuted;
                notch?.ShowMicActivity(Audio.MicMuted);
                break;
            case 15:
                SearchWindow.Toggle();
                break;
            case 16:
                ClipboardHistory.PastePlain();
                break;
            case >= 21 and <= 29:
                // Win+1…9: the dock's 1st–9th app, like Windows' taskbar
                dock?.OpenApp(id - 20);
                break;
        }
    }

    public void OpenSettings(string page, string widgetId)
    {
        OpenSettings(page);
        settings?.ShowWidget(widgetId);
    }

    public void OpenSettings(string? page = null)
    {
        if (settings == null)
        {
            settings = new SettingsWindow();
            settings.Closed += (_, _) => { settings = null; ScheduleTrim(); };
            settings.Show();
        }
        if (page != null) settings.Show(page);
        if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
        settings.Activate();
        // Brim's own windows don't trigger the window-change hook, so re-check right away
        Dispatcher.BeginInvoke(new Action(widgets.CheckDesktop), DispatcherPriority.Background);
    }

    public void RefreshSettingsWindow() => settings?.Rebuild();

    public void OpenUninstall(InstalledProgram p)
    {
        OpenSettings();
        settings?.ShowUninstall(p);
    }

    public void OpenUninstallSearch(string name)
    {
        OpenSettings();
        settings?.ShowUninstallSearch(name);
    }

    /// <summary>Brim's own tray menu (also from the dock's tray panel).</summary>
    public void ShowTrayMenu(UIElement? near = null) => tray?.ShowMenu(near);

    /// <summary>Restart into the downloaded update (the new Setup waits for Brim to close, then installs and starts it).</summary>
    /// <summary>Once, after Brim updated itself: a short "Brim updated" in the notch.</summary>
    void SayIfUpdated()
    {
        if (!Installer.RunningInstalled) return;
        var last = S.LastVersion;
        if (last == Installer.Version) return;
        S.LastVersion = Installer.Version;
        SettingsStore.Save();
        if (last == null || !Version.TryParse(last, out var was) || !Version.TryParse(Installer.Version, out var now) || now <= was) return;
        Log.Note("update", $"now on {Installer.Version} (was {last})");
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        t.Tick += (_, _) => { t.Stop(); notch?.ShowActivity("" /* sync arrows */, "Brim updated", Installer.Version, null, 4); };
        t.Start();
    }

    public void InstallUpdate()
    {
        if (Updater.Install()) Quit();
    }

    public void Quit()
    {
        if (quitting) return;
        quitting = true;
        Shutdown();
    }

    void Restore()
    {
        try { TrayService.Stop(); } catch { }
        try { LiveWallpaperHost.Stop(); } catch { }
        try { WindowsBanners.RestoreAll(); } catch { } // Windows' pop-ups back while Brim isn't running
        try { foreach (var d in AllDocks) d.RemoveAppBar(); } catch { }
        TaskbarManager.RestoreAll();
        try { DesktopIcons.Set(true); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Only the instance that actually owns the desktop puts things back
        if (owner)
        {
            second.Stop();
            shell.Stop();
            foreach (var h in winHooks) Native.UnhookWinEvent(h);
            ClipboardHistory.Save();
            Restore();
            widgets.CloseAll();
            tray?.Dispose();
            messages?.Dispose();
            SettingsStore.Save();
        }
        base.OnExit(e);
    }
}
