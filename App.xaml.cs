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
    public void ScheduleTrim()
    {
        trimTimer.Stop();
        trimTimer.Start();
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
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

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

        if (e.Args.Length == 2 && e.Args[0] == "--fullscreen-test")
        {
            // Developer check: the fullscreen rules against situations from the log, plus what they say right now
            var report = WindowTracker.SelfTest() + $"\nnow: fullscreen={WindowTracker.ForegroundIsFullscreen()} {Log.Foreground()}\n";
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

        if (e.Args.Length >= 2 && e.Args[0] is "--update-test" or "--mirror-test" or "--banners-test" or "--banners-test")
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
                        case "--update-test":
                            Updater.PretendVersion = "0.0.1";
                            await Updater.Check();
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
        SystemInfo.Sample();

        messages = new MessageWindow();
        messages.Hotkey += OnHotkey;
        messages.Register(1, Native.MOD_CONTROL | Native.MOD_ALT, 'N');
        for (int i = 1; i <= 9; i++) messages.Register(1 + i, Native.MOD_CONTROL | Native.MOD_ALT, (uint)('0' + i));
        messages.Register(11, Native.MOD_CONTROL | Native.MOD_ALT, 'H');
        messages.Register(12, Native.MOD_CONTROL | Native.MOD_ALT, 'P');
        messages.Register(13, Native.MOD_CONTROL | Native.MOD_ALT, 'C');
        messages.Register(14, Native.MOD_CONTROL | Native.MOD_ALT, 'M');
        messages.Register(15, Native.MOD_CONTROL, 0x20 /* Space */);
        messages.Register(16, Native.MOD_CONTROL | Native.MOD_ALT, 'V');
        WidgetWindow.WentOnTop += () => { notch?.EnsureTopmost(); dock?.EnsureTopmost(); };
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
        idleTrim.Tick += (_, _) => Native.TrimMemory(collect: false);
        idleTrim.Start();

        // Everything below isn't needed to draw the notch and dock, so it starts once they're on screen
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _ = MediaService.Instance.InitAsync();
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
                var shown = notch.ShowMessage(m.Sender, m.Text.Length > 0 ? $"{m.App} · {m.Text}" : m.App, m.Icon);
                Log.Note("messages", $"pop-up for {m.App}: {shown}");
            };
            MessageCenter.Sync();
            Downloads.Start(); // the dock's download badge
            Updater.Ready += () =>
                Log.Note("update", "pop-up: " + (notch?.ShowAction("", $"Brim {Updater.NewVersion} is ready", "Click to restart and update",
                    InstallUpdate, 10) ?? "no notch"));
            Updater.Start();
            WindowsBanners.Start(); // "only pop up in the notch"
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
            }

            widgets.Sync();
            DesktopIcons.Set(!S.HideDesktopIcons);
            Core.Startup.Set(S.StartWithWindows);
            OledCare.Apply();
            WeatherService.Refresh();
            MessageCenter.Sync();
        }
        catch (Exception ex) { Log.Error("apply", ex); }
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
        bool fs = WindowTracker.ForegroundIsFullscreen();
        SetNotchesFullscreen(fs);
        dock?.SetFullscreen(fs);
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

    void CheckAppInFront()
    {
        if (!S.HideOverApps || notch == null) return;
        if (Native.AppInFront() is bool app) notch.SetAppInFront(app && !Native.DesktopRaised());
    }

    void React()
    {
        reacts++;
        LivePolicy.ApplyToWallpaperEngine();
        CheckAppInFront();
        bool fs = WindowTracker.ForegroundIsFullscreen();
        SetNotchesFullscreen(fs);
        dock?.SetFullscreen(fs);
        TaskbarManager.Enforce();
        dock?.RefreshApps();
        widgets.CheckDesktop();
    }

    void ShellTick()
    {
        shellTicks++;
        CountActivity();
        // Minimising the last window or clicking the desktop doesn't always send a focus-change event
        CheckAppInFront();
        bool fs = WindowTracker.ForegroundIsFullscreen();
        SetNotchesFullscreen(fs);
        dock?.SetFullscreen(fs);
        TaskbarManager.Enforce();
        widgets.CheckDesktop();
        if (shellTicks % 4 == 0) TrayService.Instance?.KeepInFront(); // every 2 s is plenty
        // Wallpaper Engine puts its wallpaper back on its own (desktop changes, restarts): keep Brim's showing
        if (WallpaperEngine.Guard() && S.LiveWallpaper != null) SetLiveWallpaper(null, refresh: false);
        if (shellTicks % 6 == 0)
        {
            notch?.EnsureTopmost();
            dock?.EnsureTopmost();
            dock?.CheckReservation(); // "keep windows above the dock" holds even if Explorer drops the strip
            if (S.NotchScreen == "*") SyncNotches(); // a screen plugged in or out
            if (S.HideDesktopIcons) DesktopIcons.Set(false);
        }
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

    /// <summary>Hide the notch on the screen with the fullscreen game or video (the others stay).</summary>
    void SetNotchesFullscreen(bool fs)
    {
        if (notch == null) return;
        if (Brim.Views.NotchWindow.Mirrors.Count == 0) { notch.SetFullscreenHidden(fs && S.HideInFullscreen); return; }
        var fsMonitor = fs ? Native.MonitorFromWindow(Native.GetForegroundWindow(), 2 /* nearest */) : IntPtr.Zero;
        foreach (var n in AllNotches) n.SetFullscreenHidden(fs && S.HideInFullscreen && n.Monitor == fsMonitor);
    }

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
                if (index < S.Shortcuts.Count) Launcher.Open(S.Shortcuts[index].Path, S.Shortcuts[index].Args);
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

    /// <summary>Restart into the downloaded update (the new Setup waits for Brim to close, then installs and starts it).</summary>
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
        try { dock?.RemoveAppBar(); } catch { }
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
