using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Views;

public sealed class DockApp : Observable
{
    public string Key { get; init; } = "";
    public string? LaunchPath { get; set; }
    public string? LaunchArgs { get; set; }
    public bool Pinned { get; set; }
    public List<AppWindow> Windows { get; set; } = new();

    string title = "";
    public string Title { get => title; set => Set(ref title, value); }

    ImageSource? icon;
    public ImageSource? Icon
    {
        get => icon;
        set { Set(ref icon, value); OnChanged(nameof(GlyphVisibility)); }
    }
    public Visibility GlyphVisibility => icon == null ? Visibility.Visible : Visibility.Collapsed;

    bool running, active;
    public bool Running { get => running; set { Set(ref running, value); OnDot(); } }
    public bool Active { get => active; set { Set(ref active, value); OnDot(); } }

    public double TileSize { get; set; } = 44;
    Thickness tileMargin;
    public Thickness TileMargin { get => tileMargin; set { if (tileMargin != value) Set(ref tileMargin, value); } }
    public double IconSize { get; set; } = 28;

    public Visibility DotVisibility => running ? Visibility.Visible : Visibility.Collapsed;

    string? badge;
    /// <summary>Something to see in this app: a count, "" for a plain dot, null for nothing</summary>
    public string? Badge
    {
        get => badge;
        set
        {
            if (badge == value) return;
            Set(ref badge, value);
            OnChanged(nameof(BadgeVisibility));
            OnChanged(nameof(BadgeWidth));
            OnChanged(nameof(BadgeHeight));
        }
    }
    public Visibility BadgeVisibility => badge == null ? Visibility.Collapsed : Visibility.Visible;
    bool downloading;
    /// <summary>The app is downloading something (a small blue arrow on its icon)</summary>
    public bool Downloading { get => downloading; set { if (downloading == value) return; Set(ref downloading, value); OnChanged(nameof(DownloadVisibility)); } }
    public Visibility DownloadVisibility => downloading ? Visibility.Visible : Visibility.Collapsed;

    public double BadgeWidth => string.IsNullOrEmpty(badge) ? 9 : badge.Length > 1 ? 20 : 15;
    public double BadgeHeight => string.IsNullOrEmpty(badge) ? 9 : 15;
    public double DotWidth => active ? 14 : 5;
    public Brush DotBrush => active ? (Brush)Application.Current.FindResource("Brim.Accent") : (Brush)Application.Current.FindResource("Brim.Dim");

    void OnDot() { OnChanged(nameof(DotVisibility)); OnChanged(nameof(DotWidth)); OnChanged(nameof(DotBrush)); }
    void OnChanged(string name) => RaiseChanged(name);
}

public partial class DockWindow : Window
{
    readonly ObservableCollection<DockApp> apps = new();
    readonly Dictionary<string, DockApp> known = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string?> pinTargets = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    // Checks where the pointer is while the dock hides itself: often enough that it comes up as soon as you reach the edge
    readonly DispatcherTimer edge = new() { Interval = TimeSpan.FromMilliseconds(50) };
    IntPtr hwnd;
    bool appBar, shownByEdge, fullscreen;
    DateTime leftAt = DateTime.MaxValue;
    uint appBarMsg;
    int reservedPx;

    static AppSettings S => SettingsStore.Current;

    public DockWindow()
    {
        InitializeComponent();
        Apps.ItemsSource = apps;
        refresh.Tick += (_, _) => RefreshApps();
        edge.Tick += (_, _) => EdgeCheck();
        App.SecondTick += UpdateClock;
        SizeChanged += (_, _) => Place();
        OledCare.Shift += OnOledShift;
        OledCare.Dim += OnOledDim;
        LocationChanged += (_, _) => SyncBackdrop();
        Bar.LayoutUpdated += (_, _) => SyncBackdrop();
        IsVisibleChanged += (_, _) => SyncBackdrop();
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, true);
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        };
        Loaded += (_, _) => { ApplySettings(); refresh.Start(); };
        Closed += (_, _) =>
        {
            refresh.Stop(); edge.Stop(); RemoveAppBar(); App.SecondTick -= UpdateClock;
            backdrop?.Dispose(); backdrop = null;
            OledCare.Shift -= OnOledShift; OledCare.Dim -= OnOledDim;
        };
    }

    (double tile, double icon) Sizes
    {
        get
        {
            double icon = Math.Clamp(S.DockIconPx > 0 ? S.DockIconPx : 30, 16, 72);
            return (icon + 14, icon);
        }
    }

    Backdrop? backdrop;

    /// <summary>Solid bar, Glass (blurs what's behind) or Clear (no bar at all, icons float with a soft shadow).</summary>
    bool docked;

    // OLED care: the floating dock drifts a few pixels over time (the full-width bar stays put, so no gaps show)
    void OnOledShift(double dx, double dy)
    {
        if (Bar.RenderTransform is not TransformGroup) Bar.RenderTransform = new TransformGroup { Children = { oled, slide } };
        oled.X = docked ? 0 : dx;
        oled.Y = docked ? 0 : dy;
        SyncBackdrop();
    }

    void OnOledDim(bool dim) =>
        Bar.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(dim ? 0.35 : 1, TimeSpan.FromMilliseconds(dim ? 1500 : 250)));
    internal void DebugDocked() { docked = true; ApplyShape(); }

    /// <summary>
    /// Floating pill normally; a full-width bar when a maximised window sits above it (with "keep windows above the
    /// dock" on), so there's no strip of wallpaper below and beside the window. Same height either way.
    /// </summary>
    void UpdateDocked(List<AppWindow> windows)
    {
        var chosen = Native.ScreenOrPrimary(S.DockScreen).Handle;
        var primary = chosen != IntPtr.Zero ? chosen : Native.MonitorFromPoint(new Native.POINT(), 1);
        bool want = S.DockReserve && !S.DockAutoHide && S.DockWhenMaximized == "Bar" && windows.Any(w =>
            !Native.IsIconic(w.Hwnd) && Native.IsZoomed(w.Hwnd) && Native.MonitorFromWindow(w.Hwnd, 2) == primary);
        if (want == docked) return;
        docked = want;
        ApplyShape();
    }

    void ApplyShape()
    {
        if (docked)
        {
            SizeToContent = SizeToContent.Height;
            Width = Native.ScreenDip(S.DockScreen).Width;
            Bar.Margin = new Thickness(0);
            Bar.CornerRadius = new CornerRadius(0);
            Bar.Padding = new Thickness(6, S.DockPadding + 4, 6, S.DockPadding + 5);
            // The icons stay where you put the dock, even when it stretches into a bar
            BarContent.HorizontalAlignment = S.DockPosition == "Left" ? HorizontalAlignment.Left : S.DockPosition == "Right" ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            BarContent.Margin = new Thickness(S.DockPosition == "Left" ? 10 : 0, 0, S.DockPosition == "Right" ? 10 : 0, 0);
        }
        else
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            BarContent.Margin = new Thickness(0);
            Bar.Margin = new Thickness(0, 0, 0, Math.Clamp(S.DockGap, 0, 60));
            Bar.CornerRadius = new CornerRadius(Math.Clamp(S.DockRadius, 0, 40));
            Bar.Padding = new Thickness(6, Math.Clamp(S.DockPadding, 0, 30), 6, Math.Clamp(S.DockPadding, 0, 30));
        }
        ApplySurface();
        Place();
    }

    void ApplySurface()
    {
        // A see-through bar would show wallpaper again when docked, so Clear becomes solid there
        var surface = docked && S.DockSurface == "Clear" ? "Solid" : S.DockSurface;
        switch (surface)
        {
            case "Glass":
                var c = ThemeManager.Parse(ThemeManager.Current.Surface);
                Bar.Background = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B));
                Bar.SetResourceReference(Border.BorderBrushProperty, "Brim.Stroke");
                Bar.BorderThickness = docked ? new Thickness(0, 1, 0, 0) : new Thickness(1);
                BarContent.Effect = null;
                backdrop ??= new Backdrop();
                break;
            case "Clear":
                Bar.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
                Bar.BorderThickness = new Thickness(0);
                BarContent.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.75, Color = Colors.Black };
                backdrop?.Dispose();
                backdrop = null;
                break;
            default:
                Bar.SetResourceReference(Border.BackgroundProperty, "Brim.Surface");
                Bar.SetResourceReference(Border.BorderBrushProperty, "Brim.Stroke");
                Bar.BorderThickness = docked ? new Thickness(0, 1, 0, 0) : new Thickness(1);
                BarContent.Effect = null;
                backdrop?.Dispose();
                backdrop = null;
                break;
        }
        SyncBackdrop();
    }

    void SyncBackdrop()
    {
        if (backdrop == null || hwnd == IntPtr.Zero) return;
        if (!IsVisible || fullscreen || slideTarget > 0 || PresentationSource.FromVisual(Bar) == null) { backdrop.Hide(); return; }
        var tl = Bar.PointToScreen(new Point(0, 0));
        var br = Bar.PointToScreen(new Point(Bar.ActualWidth, Bar.ActualHeight));
        double scale = (br.X - tl.X) / Math.Max(1, Bar.ActualWidth);
        backdrop.Place(hwnd, (int)Math.Round(tl.X), (int)Math.Round(tl.Y), (int)Math.Round(br.X - tl.X), (int)Math.Round(br.Y - tl.Y),
            (int)Math.Round(Bar.CornerRadius.TopLeft * scale), false);
    }

    public void ApplySettings()
    {
        if (docked && S.DockWhenMaximized != "Bar") docked = false;
        TaskMgrBtn.Visibility = S.DockTaskManager ? Visibility.Visible : Visibility.Collapsed;
        ApplyShape(); // size, gap, roundness and position may have changed
        ApplySurface();
        var (tile, _) = Sizes;
        StartBtn.Width = StartBtn.Height = tile;
        TrayBtn.Height = tile;
        StatusBtn.Height = tile;
        WeatherBtn.Height = tile;
        DesktopBtn.Height = tile;
        ClockBtn.Height = tile;
        ClockBtn.Visibility = S.DockClock ? Visibility.Visible : Visibility.Collapsed;
        pinTargets.Clear();
        known.Clear();
        apps.Clear();
        RefreshApps();
        UpdateClock();

        if (S.DockAutoHide) { RemoveAppBar(); edge.Start(); }
        else
        {
            edge.Stop();
            shownByEdge = false;
            if (S.DockReserve) Dispatcher.BeginInvoke(new Action(SetupAppBar), DispatcherPriority.Background);
            else RemoveAppBar();
        }
        Place();
    }

    void UpdateClock()
    {
        UpdateStatus();
        var now = DateTime.Now;
        TimeText.Text = now.ToString(S.Clock24 ? "HH:mm" : "h:mm tt");
        DateText.Text = now.ToString("ddd d MMM");
    }

    // ------------------------------------------------------------------ placement

    void Place()
    {
        if (ActualWidth <= 0) return;
        var screen = Native.ScreenDip(S.DockScreen);
        const double gap = 12;
        Left = docked ? screen.Left + (screen.Width - ActualWidth) / 2 : S.DockPosition switch
        {
            "Left" => screen.Left + gap,
            "Right" => screen.Left + screen.Width - ActualWidth - gap,
            _ => screen.Left + (screen.Width - ActualWidth) / 2,
        };
        bool hidden = fullscreen || S.DockAutoHide && !shownByEdge;
        // The window stays put; the bar slides down out of sight and back. (The window's empty, see-through parts
        // let clicks through, so a hidden dock doesn't get in the way.)
        Top = screen.Bottom - ActualHeight;
        Slide(hidden ? ActualHeight : 0);
        if (appBar && !S.DockAutoHide)
        {
            int px = (int)Math.Ceiling(ActualHeight * Dpi());
            if (px != reservedPx) SetupAppBar();
        }
    }

    readonly TranslateTransform slide = new(), oled = new();
    double slideTarget = double.NaN;

    bool slideRunning;
    int slideGen;

    /// <summary>Ease the bar to this many pixels below its place (0 = shown), fading with it.</summary>
    void Slide(double to, bool fast = false, Action? done = null)
    {
        if (Bar.RenderTransform is not TransformGroup)
            Bar.RenderTransform = new TransformGroup { Children = { oled, slide } };
        if (to == slideTarget && done == null) return;
        bool showing = to < slideTarget || double.IsNaN(slideTarget);
        slideTarget = to;
        if (to > 0) backdrop?.Hide(); // the blur layer can't follow the slide; it comes back once the dock is up
        var ms = TimeSpan.FromMilliseconds(fast ? 130 : showing ? 180 : 220);
        var ease = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = showing ? System.Windows.Media.Animation.EasingMode.EaseOut : System.Windows.Media.Animation.EasingMode.EaseIn,
        };
        var move = new System.Windows.Media.Animation.DoubleAnimation(to, ms) { EasingFunction = ease };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(to > 0 ? 0 : 1, ms) { EasingFunction = ease };
        int gen = ++slideGen;
        slideRunning = true;
        move.Completed += (_, _) =>
        {
            if (gen != slideGen) return; // a newer slide took over
            slideRunning = false;
            Bar.CacheMode = null; // back to sharp, live drawing
            if (to == 0) SyncBackdrop();
            done?.Invoke();
        };
        // Smooth on the GPU: draw the bar once and move the picture, instead of re-drawing every icon each frame
        Bar.CacheMode = new BitmapCache { RenderAtScale = Dpi(), SnapsToDevicePixels = true };
        slide.BeginAnimation(TranslateTransform.YProperty, move);
        Bar.BeginAnimation(OpacityProperty, fade);
    }

    double Dpi()
    {
        var src = PresentationSource.FromVisual(this);
        return src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
    }

    void EdgeCheck()
    {
        if (fullscreen) return;
        Native.GetCursorPos(out var pt);
        var mon = Native.ScreenOrPrimary(S.DockScreen).Bounds;
        double scale = Dpi();
        bool atEdge = pt.Y >= mon.Bottom - 2 && pt.X >= mon.Left && pt.X < mon.Right;
        bool overDock = IsMouseOver || pt.Y >= mon.Bottom - (ActualHeight + 6) * scale &&
                        pt.X >= Left * scale - 10 && pt.X <= (Left + ActualWidth) * scale + 10;

        if (!shownByEdge && atEdge)
        {
            shownByEdge = true;
            leftAt = DateTime.MaxValue;
            Place();
        }
        else if (shownByEdge)
        {
            if (overDock || IsAnyMenuOpen || Flyout.IsOpen) leftAt = DateTime.MaxValue;
            else if (leftAt == DateTime.MaxValue) leftAt = DateTime.Now;
            else if (DateTime.Now - leftAt > TimeSpan.FromMilliseconds(450))
            {
                shownByEdge = false;
                Place();
            }
        }
    }

    bool IsAnyMenuOpen;

    DateTime keepVisibleUntil;

    public void SetFullscreen(bool fs)
    {
        // Over a fullscreen game the dock shows when Start (or the like) takes focus. Clicking the dock hands focus
        // back to the game, so don't hide out from under the pointer mid-click.
        if (fs && (IsMouseOver || IsAnyMenuOpen || Flyout.IsOpen || DateTime.Now < keepVisibleUntil)) return;
        if (fs == fullscreen) return;
        fullscreen = fs;
        if (fs)
        {
            // Slide out of the way quickly, then hide properly (nothing of Brim's left on top of a game or video)
            if (slideTarget >= ActualHeight && !slideRunning) Visibility = Visibility.Hidden;
            else Slide(ActualHeight, fast: true, done: () => { if (fullscreen) Visibility = Visibility.Hidden; });
        }
        else
        {
            Visibility = Visibility.Visible;
            Place(); // slides back up (or stays tucked away with auto-hide)
        }
    }

    public void EnsureTopmost()
    {
        // Don't jump in front of a hover label, a preview or an open panel
        if (HoverTip.IsShowing || Flyout.IsOpen) return;
        if (hwnd != IntPtr.Zero && IsVisible)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------ app bar (keeps maximised windows above the dock)

    void SetupAppBar()
    {
        if (hwnd == IntPtr.Zero || S.DockAutoHide || !S.DockReserve || ActualHeight <= 0) return;
        if (appBarMsg == 0) appBarMsg = Native.RegisterWindowMessage("BrimDockAppBar");
        var mon = Native.ScreenOrPrimary(S.DockScreen).Bounds;
        reservedPx = (int)Math.Ceiling(ActualHeight * Dpi());
        // Straight to Explorer (it owns the work area), not through Brim's own tray window
        DockAppBar.Reserve(hwnd, reservedPx, direct: true, callback: appBarMsg, monitor: mon, alreadyRegistered: appBar);
        appBar = true;
    }

    public void RemoveAppBar()
    {
        if (!appBar) return;
        DockAppBar.Remove(hwnd, direct: true);
        appBar = false;
        reservedPx = 0;
    }

    /// <summary>
    /// "Keep windows above the dock": make sure the strip is still reserved. Explorer can drop it (at startup, when it
    /// restarts, when screens change), and then maximised windows slide under the dock. Called every few seconds.
    /// </summary>
    bool reservationOk = true;

    public void CheckReservation()
    {
        if (hwnd == IntPtr.Zero || S.DockAutoHide || !S.DockReserve || !IsVisible || fullscreen || ActualHeight <= 0) return;
        var mon = Native.ScreenOrPrimary(S.DockScreen);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon.Handle, ref mi)) return;
        int want = (int)Math.Ceiling(ActualHeight * Dpi());
        int have = mi.rcMonitor.Bottom - mi.rcWork.Bottom;
        if (appBar && have >= want - 2) { reservationOk = true; return; }
        if (reservationOk) Log.Note("dock", $"strip for the dock wasn't reserved (had {have}px, needs {want}px): reserving it again");
        reservationOk = false;
        if (appBar) RemoveAppBar();
        SetupAppBar();
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (appBar && msg == appBarMsg)
        {
            switch (w.ToInt32())
            {
                case Native.ABN_POSCHANGED: SetupAppBar(); break;
                // ABN_FULLSCREENAPP is left alone on purpose: Windows' own guess disagreed with Brim's check
                // (which also runs on every focus change) and the two made the dock flicker over videos
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ apps

    static string KeyFor(string? path) => (path ?? "").ToLowerInvariant();

    string? PinTarget(ShortcutItem pin)
    {
        if (!pinTargets.TryGetValue(pin.Path, out var t))
        {
            t = IconHelper.ResolveShortcut(pin.Path);
            pinTargets[pin.Path] = t;
        }
        return t;
    }

    readonly List<string> runningOrder = new();

    public void RefreshApps()
    {
        if (dragging) return;
        var (tile, iconSize) = Sizes;
        var windows = WindowTracker.TaskbarWindows();
        UpdateDocked(windows);
        var fg = Native.GetForegroundWindow();
        var order = new List<DockApp>();

        // group windows by executable; packaged apps share a host, so those stay one-per-window
        List<(string key, List<AppWindow> wins)> groups = new();
        foreach (var w in windows)
        {
            bool hosted = w.ExePath != null && w.ExePath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
            string key = hosted || w.ExePath == null ? "hwnd:" + w.Hwnd : KeyFor(w.ExePath);
            var g = groups.FirstOrDefault(x => x.key == key);
            if (g.wins == null) groups.Add((key, new List<AppWindow> { w }));
            else g.wins.Add(w);
        }

        foreach (var pin in S.DockPins)
        {
            var target = PinTarget(pin);
            string key = KeyFor(target ?? pin.Path);
            var app = Get(key, () => new DockApp
            {
                Key = key,
                Title = pin.Name,
                Icon = IconHelper.ForPath(pin.Path),
            });
            app.Pinned = true;
            app.LaunchPath = pin.Path;
            app.LaunchArgs = pin.Args;
            var match = groups.FirstOrDefault(g => g.key == key);
            app.Windows = match.wins ?? new List<AppWindow>();
            if (match.wins != null) groups.Remove(match);
            order.Add(app);
        }

        // Running apps keep the spot they first appeared in (Windows' own stacking order changes
        // whenever you minimise or switch, which made icons jump around)
        foreach (var (key, _) in groups) if (!runningOrder.Contains(key)) runningOrder.Add(key);
        runningOrder.RemoveAll(k => !groups.Any(g => g.key == k));
        groups = groups.OrderBy(g => runningOrder.IndexOf(g.key)).ToList();

        foreach (var (key, wins) in groups)
        {
            var first = wins[0];
            var app = Get(key, () => new DockApp
            {
                Key = key,
                Icon = key.StartsWith("hwnd:") ? IconHelper.ForWindow(first.Hwnd) : IconHelper.ForPath(first.ExePath!),
            });
            app.Pinned = false;
            app.LaunchPath = first.ExePath;
            app.Windows = wins;
            order.Add(app);
        }

        // Your own order (from dragging icons) wins; apps you haven't placed go after, in the usual order
        if (S.DockOrder.Count > 0)
            order = order.Select((a, i) => (a, i))
                         .OrderBy(t => S.DockOrder.IndexOf(t.a.Key) is int k && k >= 0 ? k : S.DockOrder.Count + t.i)
                         .Select(t => t.a).ToList();

        foreach (var app in order)
        {
            app.TileSize = tile;
            app.IconSize = iconSize;
            app.Running = app.Windows.Count > 0;
            app.Active = app.Windows.Any(w => w.Hwnd == fg);
            app.TileMargin = new Thickness(S.DockSpacing / 2.0, 0, S.DockSpacing / 2.0, 0);
            app.Downloading = S.DockBadges && Downloads.ActiveFor(app.Windows.FirstOrDefault()?.ExePath ?? PinTargetOrPath(app), app.Windows.Select(w => w.Pid));
            if (!S.DockBadges) app.Badge = null;
            else try { app.Badge = MessageCenter.IndicatorFor(app.Windows.FirstOrDefault()?.ExePath ?? PinTargetOrPath(app), app.Windows); }
            catch (Exception ex) { Log.Error("dock badge", ex); }
            if (app.Windows.Count > 0)
                app.Title = app.Windows.Count == 1 ? app.Windows[0].Title : $"{app.Windows[0].Title}  (+{app.Windows.Count - 1})";
        }

        if (!order.Select(a => a.Key).SequenceEqual(apps.Select(a => a.Key)) || apps.Any(a => a.TileSize != tile))
        {
            apps.Clear();
            foreach (var a in order) apps.Add(a);
        }
        foreach (var k in known.Keys.Where(k => !order.Any(o => o.Key == k)).ToList()) known.Remove(k);
    }

    /// <summary>The exe a pinned app starts (for its badge while it isn't running).</summary>
    string? PinTargetOrPath(DockApp app)
    {
        var p = app.LaunchPath;
        if (p == null) return null;
        var pin = S.DockPins.FirstOrDefault(x => x.Path == p);
        var target = pin != null ? PinTarget(pin) : p;
        return target != null && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    DockApp Get(string key, Func<DockApp> make)
    {
        if (!known.TryGetValue(key, out var app))
        {
            app = make();
            known[key] = app;
        }
        return app;
    }

    // ---------- drag icons to reorder them

    DockApp? pressed;
    Point pressAt;
    bool dragging, swallowClick;

    void App_PressDrag(object sender, MouseButtonEventArgs e)
    {
        pressed = ((FrameworkElement)sender).Tag as DockApp;
        pressAt = e.GetPosition(Apps);
    }

    void App_DragMove(object sender, MouseEventArgs e)
    {
        if (pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(Apps);
        if (!dragging)
        {
            if (Math.Abs(at.X - pressAt.X) < 8) return;
            dragging = true;
            previewDelay.Stop();
            WindowPreview.HideNow();
        }
        // Which slot is the pointer over?
        int from = apps.IndexOf(pressed), to = from;
        for (int i = 0; i < apps.Count; i++)
        {
            if (Apps.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var left = c.TranslatePoint(new Point(0, 0), Apps).X;
            if (at.X >= left && at.X < left + c.ActualWidth) { to = i; break; }
        }
        if (from >= 0 && to != from) apps.Move(from, to);
    }

    void App_DragEnd(object sender, MouseButtonEventArgs e)
    {
        pressed = null;
        if (!dragging) return;
        dragging = false;
        S.DockOrder = apps.Select(a => a.Key).ToList();
        SettingsStore.Save();
        // The button would treat letting go as a click; skip that one
        swallowClick = true;
        Dispatcher.BeginInvoke(new Action(() => swallowClick = false), DispatcherPriority.Input);
        e.Handled = true;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    // ---------- drop files on an app to open them with it

    static string? ExeFor(DockApp app, DockWindow self)
    {
        var path = app.LaunchPath;
        if (path == null) return null;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            path = self.PinTarget(new ShortcutItem { Path = path }) ?? path;
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    void App_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) && ((FrameworkElement)sender).Tag is DockApp app &&
                  !app.Key.StartsWith("hwnd:") && ExeFor(app, this) != null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void App_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).Tag is not DockApp app || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var exe = ExeFor(app, this);
        if (exe == null) return;
        Launcher.Open(exe, string.Join(" ", files.Select(f => "\"" + f + "\"")));
    }

    async void App_Click(object sender, RoutedEventArgs e)
    {
        if (swallowClick) { swallowClick = false; return; }
        if (((FrameworkElement)sender).Tag is not DockApp app) return;
        WindowPreview.HideNow();
        previewDelay.Stop();
        keepVisibleUntil = DateTime.Now.AddSeconds(1.5);
        // With Start open, close it first; then just bring the app up (don't minimise it)
        bool startWasOpen = await Native.CloseStartIfOpen();
        var fg = Native.GetForegroundWindow();
        if (app.Windows.Count == 0)
        {
            if (app.LaunchPath != null) Launcher.Open(app.LaunchPath, app.LaunchArgs);
            return;
        }
        int idx = app.Windows.FindIndex(w => w.Hwnd == fg);
        if (startWasOpen) Native.ForceForeground(app.Windows[Math.Max(0, idx)].Hwnd);
        else if (idx >= 0)
        {
            if (app.Windows.Count == 1) Native.ShowWindow(fg, Native.SW_MINIMIZE);
            else Native.ForceForeground(app.Windows[(idx + 1) % app.Windows.Count].Hwnd);
        }
        else Native.ForceForeground(app.Windows[0].Hwnd);
        Dispatcher.BeginInvoke(new Action(RefreshApps), DispatcherPriority.Background);
    }

    // ---------- hover previews ----------

    readonly DispatcherTimer previewDelay = new() { Interval = TimeSpan.FromMilliseconds(380) };
    FrameworkElement? hovered;
    bool previewShowing;

    void App_Enter(object sender, MouseEventArgs e)
    {
        hovered = (FrameworkElement)sender;
        if (previewShowing) { ShowPreview(); return; } // moving between icons: switch straight away
        previewDelay.Stop();
        previewDelay.Tick -= PreviewTick;
        previewDelay.Tick += PreviewTick;
        previewDelay.Start();
    }

    void PreviewTick(object? s, EventArgs e)
    {
        previewDelay.Stop();
        if (hovered?.IsMouseOver == true) ShowPreview();
    }

    async void ShowPreview()
    {
        if (hovered?.Tag is not DockApp app) return;
        if (app.Windows.Count == 0)
        {
            hovered.ToolTip = app.Title;
            return;
        }
        hovered.ToolTip = null;
        // Previews can't appear over Start, so close it first (like the other dock panels)
        keepVisibleUntil = DateTime.Now.AddSeconds(1.5);
        var target = hovered;
        if (await Native.CloseStartIfOpen() && !target.IsMouseOver) return;
        WindowPreview.ShowFor(app, target);
        previewShowing = true;
    }

    void App_Leave(object sender, MouseEventArgs e)
    {
        previewDelay.Stop();
        WindowPreview.HideSoon();
        // forget "showing" a moment later, unless the pointer moved onto another icon or the preview
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await System.Threading.Tasks.Task.Delay(350);
            if (!(hovered?.IsMouseOver ?? false)) previewShowing = false;
        }));
    }

    void App_MiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (((FrameworkElement)sender).Tag is DockApp { LaunchPath: { } p } app) Launcher.Open(p, app.LaunchArgs);
    }

    void App_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not DockApp app) return;
        e.Handled = true;
        var menu = new ContextMenu();
        if (app.LaunchPath != null && !app.Key.StartsWith("hwnd:"))
        {
            menu.Items.Add(Item("New window", () => Launcher.Open(app.LaunchPath, app.LaunchArgs)));
            if (app.Pinned)
                menu.Items.Add(Item("Unpin from dock", () =>
                {
                    S.DockPins.RemoveAll(p => KeyFor(PinTarget(p) ?? p.Path) == app.Key);
                    SettingsStore.Save();
                    RefreshApps();
                    ((App)Application.Current).RefreshSettingsWindow();
                }));
            else
                menu.Items.Add(Item("Pin to dock", () =>
                {
                    S.DockPins.Add(new ShortcutItem { Name = IconHelper.NiceName(app.LaunchPath), Path = app.LaunchPath });
                    SettingsStore.Save();
                    RefreshApps();
                    ((App)Application.Current).RefreshSettingsWindow();
                }));
        }
        if (app.Windows.Count > 0)
            menu.Items.Add(Item(app.Windows.Count > 1 ? "Close all windows" : "Close window", () =>
            {
                foreach (var w in app.Windows) Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }));
        if (menu.Items.Count == 0) return;
        IsAnyMenuOpen = true;
        menu.Closed += (_, _) => IsAnyMenuOpen = false;
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }

    void Start_Click(object sender, RoutedEventArgs e) => Native.TapKeys(Native.VK_LWIN);
    void Clock_Click(object sender, RoutedEventArgs e) => Native.TapKeys(Native.VK_LWIN, (byte)'N');
    void Tray_Click(object sender, RoutedEventArgs e) => Flyout.Toggle("tray", TrayBtn, () => new TrayPanel());

    // Task Manager (Windows' own; on every Windows 10/11 PC)
    void TaskMgr_Click(object sender, RoutedEventArgs e) =>
        Launcher.Open(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe"));
    // Same as Windows' corner button: shows the desktop, and clicking again restores every window
    async void Desktop_Click(object sender, RoutedEventArgs e)
    {
        keepVisibleUntil = DateTime.Now.AddSeconds(1.5);
        // If Start or Search is still open, close it and let it finish closing,
        // otherwise Explorer ignores the first toggle (that was the "needs a double click")
        var fg = Native.GetForegroundWindow();
        if (Native.ClassOf(fg) == "Windows.UI.Core.CoreWindow")
        {
            Native.TapKeys(0x1B /* Esc */);
            await System.Threading.Tasks.Task.Delay(220);
        }
        try
        {
            // The command Explorer's own show-desktop button runs, so no fake key presses that a game or Start could eat
            var t = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(t!)!;
            shell.ToggleDesktop();
            Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception ex)
        {
            Log.Error("show desktop", ex);
            Native.TapKeys(Native.VK_LWIN, (byte)'D');
        }
    }
    void Weather_Click(object sender, RoutedEventArgs e) => Flyout.Toggle("weather", WeatherBtn, () =>
    {
        var w = WeatherService.Current;
        var p = new StackPanel { MinWidth = 220 };
        if (w == null) return p;
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new TextBlock { Text = w.Glyph, FontSize = 40, FontFamily = (FontFamily)FindResource("Brim.Symbols"), VerticalAlignment = VerticalAlignment.Center });
        var temp = new TextBlock { Text = $"{Math.Round(w.Temp)}°", FontSize = 44, FontWeight = FontWeights.Light, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(temp);
        p.Children.Add(top);
        p.Children.Add(new TextBlock { Text = w.Description, FontSize = 15, Margin = new Thickness(0, 6, 0, 0) });
        var sub = new TextBlock { Text = $"High {Math.Round(w.High)}°   Low {Math.Round(w.Low)}°\n{w.Place}", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        p.Children.Add(sub);
        return p;
    });

    void Status_Click(object sender, RoutedEventArgs e) => Flyout.Toggle("quick", StatusBtn, () => new QuickPanel(false));

    int statusTicks;
    string lastNet = "";

    void UpdateStatus()
    {
        if (statusTicks++ % 3 == 0)
        {
            var net = Network.Current();
            NetGlyph.Text = !net.Connected ? "" : net.Wireless ? "" : "";
            NetGlyph.Opacity = net.Connected ? 1 : 0.5;
            lastNet = !net.Connected ? "Not connected" : net.Wireless ? $"Wi-Fi: {net.Name}" : net.Name;
        }
        // One label for the whole status group: network, sound and battery as they are right now
        var sound = Audio.Muted ? "Sound muted" : $"Sound {Math.Round(Audio.Volume * 100)}%";
        var battery = SystemInfo.HasBattery ? $"  ·  Battery {SystemInfo.BatteryPercent}%{(SystemInfo.Charging ? ", charging" : "")}" : "";
        StatusBtn.ToolTip = $"{lastNet}  ·  {sound}{battery}";
        ClockBtn.ToolTip = DateTime.Now.ToString("dddd, d MMMM yyyy") + "\nClick for notifications and calendar";
        VolGlyph.Text = Audio.Muted ? "" : "";

        // Weather temperature, like Windows' taskbar (needs a city in Settings › Widgets)
        if (statusTicks % 300 == 1) WeatherService.Refresh();
        var w = WeatherService.Current;
        if (S.DockWeather && w != null)
        {
            WeatherBtn.Visibility = Visibility.Visible;
            WeatherGlyph.Text = w.Glyph;
            WeatherTemp.Text = $"{Math.Round(w.Temp)}°";
            WeatherBtn.ToolTip = $"{w.Place}: {w.Description}, high {Math.Round(w.High)}° low {Math.Round(w.Low)}°";
        }
        else WeatherBtn.Visibility = Visibility.Collapsed;
        if (SystemInfo.HasBattery)
        {
            BatteryText.Visibility = Visibility.Visible;
            BatteryText.Text = $"{SystemInfo.BatteryPercent}%{(SystemInfo.Charging ? "⚡" : "")}";
        }
        else BatteryText.Visibility = Visibility.Collapsed;
    }
}
