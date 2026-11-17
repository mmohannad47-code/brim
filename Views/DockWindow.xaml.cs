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

    bool attention;
    /// <summary>It asked for attention (a finished download, an installer waiting, a call): an orange glow until you go to it.</summary>
    public bool Attention
    {
        get => attention;
        set { if (attention == value) return; Set(ref attention, value); OnChanged(nameof(AttentionVisibility)); OnDot(); }
    }
    public Visibility AttentionVisibility => attention ? Visibility.Visible : Visibility.Collapsed;

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
    public double DotWidth => active || attention ? 14 : 5;

    /// <summary>The dock's edge ("Bottom", "Left", "Right"): the running mark sits on the screen side of the icon.</summary>
    public static string Side { get; set; } = "Bottom";
    bool Across => Side != "Bottom";
    public double DotW => Across ? 3 : DotWidth;
    public double DotH => Across ? DotWidth : 3;
    public HorizontalAlignment DotHAlign => Side switch { "Left" => HorizontalAlignment.Left, "Right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
    public VerticalAlignment DotVAlign => Across ? VerticalAlignment.Center : VerticalAlignment.Bottom;
    public Thickness DotMargin => Side switch { "Left" => new Thickness(1, 0, 0, 0), "Right" => new Thickness(0, 0, 1, 0), _ => new Thickness(0, 0, 0, 1) };
    static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
    public Brush DotBrush => attention ? Orange : active ? (Brush)Application.Current.FindResource("Brim.Accent") : IdleDot ?? (Brush)Application.Current.FindResource("Brim.Dim");
    /// <summary>The running mark of apps not in front, when the bar is see-through (grey would vanish on a bright wallpaper).</summary>
    public static Brush? IdleDot { get; set; }

    void OnDot() { OnChanged(nameof(DotVisibility)); OnChanged(nameof(DotWidth)); OnChanged(nameof(DotW)); OnChanged(nameof(DotH)); OnChanged(nameof(DotBrush)); }
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
    string? laidOutSide;
    DateTime leftAt = DateTime.MaxValue;
    uint appBarMsg;
    int reservedPx;

    static AppSettings S => SettingsStore.Current;

    /// <summary>Docks on the other screens ("Every screen"): each shows your apps, clock and buttons on its own screen.</summary>
    public static readonly List<DockWindow> Others = new();
    /// <summary>The screen this dock sits on when it isn't the main one (null: the one picked in settings).</summary>
    readonly string? screenDevice;
    public string? ScreenDevice => screenDevice;
    string ScreenId => screenDevice ?? (S.DockScreen == "*" ? "" : S.DockScreen);
    public IntPtr Monitor => Native.ScreenOrPrimary(ScreenId).Handle;

    /// <summary>A dock for another screen.</summary>
    public DockWindow(string device) : this() { screenDevice = device; }

    /// <summary>For `--dock-apps-test`: every icon on the dock.</summary>
    internal string DebugApps => string.Join("\n", apps.Select(a => $"{a.Title} | pinned {a.Pinned} | running {a.Running} ({a.Windows.Count} windows) | {a.Key}"));

    /// <summary>For `--dock-screens-test`.</summary>
    internal string DebugState => $"screen '{ScreenId}', visible {IsVisible}, at {Left:0},{Top:0} {ActualWidth:0}x{ActualHeight:0}, {apps.Count} apps, fullscreen-hidden {fullscreen}, bar {docked}";

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
        // Clicked an icon to switch to a fullscreen video: the dock waited because the pointer was on it; off it, it goes now
        MouseLeave += (_, _) => { if (!fullscreen) ((App)Application.Current).RecheckFullscreen(); };
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
    /// <summary>For `--snapshot`: show app n asking for attention (-1: none).</summary>
    internal void DebugAttention(int n) { refresh.Stop(); for (int i = 0; i < apps.Count; i++) apps[i].Attention = i == n; if (n < 0) refresh.Start(); }

    /// <summary>
    /// Floating pill normally; a full-width bar when a maximised window sits above it (with "keep windows above the
    /// dock" on), so there's no strip of wallpaper below and beside the window. Same height either way.
    /// </summary>
    void UpdateDocked(List<AppWindow> windows)
    {
        var chosen = Native.ScreenOrPrimary(ScreenId).Handle;
        var primary = chosen != IntPtr.Zero ? chosen : Native.MonitorFromPoint(new Native.POINT(), 1);
        bool want = S.DockReserve && !S.DockAutoHide && S.DockWhenMaximized == "Bar" && windows.Any(w =>
            !Native.IsIconic(w.Hwnd) && Native.IsZoomed(w.Hwnd) && Native.MonitorFromWindow(w.Hwnd, 2) == primary);
        if (want == docked) return;
        docked = want;
        ApplyShape();
    }

    // ---------- which edge the dock sits on: the bottom (like Windows' taskbar), or the left or right side

    /// <summary>The edge of the screen the dock is on: "Bottom", "Left" or "Right".</summary>
    static string Side => S.DockSide is "Left" or "Right" ? S.DockSide : "Bottom";
    static bool Vertical => Side != "Bottom";
    /// <summary>How deep the dock reaches into the screen from its edge (its height at the bottom, its width on a side).</summary>
    double Depth => Vertical ? ActualWidth : ActualHeight;
    static uint AppBarEdge => Side switch { "Left" => Native.ABE_LEFT, "Right" => Native.ABE_RIGHT, _ => Native.ABE_BOTTOM };
    /// <summary>Menus open away from the edge: above a bottom dock, beside a side one.</summary>
    static System.Windows.Controls.Primitives.PlacementMode MenuPlacement => Side switch
    {
        "Left" => System.Windows.Controls.Primitives.PlacementMode.Right,
        "Right" => System.Windows.Controls.Primitives.PlacementMode.Left,
        _ => System.Windows.Controls.Primitives.PlacementMode.Top,
    };
    /// <summary>The thin line along the screen side of a full-length bar.</summary>
    static Thickness DockedBorder => Side switch { "Left" => new Thickness(0, 0, 1, 0), "Right" => new Thickness(1, 0, 0, 0), _ => new Thickness(0, 1, 0, 0) };

    /// <summary>For pop-ups shown from a dock (previews, panels): which side to open on.</summary>
    public static string SideFor(DependencyObject anchor) => GetWindow(anchor) is DockWindow ? Side : "Bottom";

    /// <summary>A row along the bottom, or a column down a side: the same parts, turned.</summary>
    void ApplyLayout()
    {
        bool v = Vertical;
        var o = v ? Orientation.Vertical : Orientation.Horizontal;
        BarContent.Orientation = o;
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, o);
        Apps.ItemsPanel = new ItemsPanelTemplate(panel);
        Sep.Width = v ? double.NaN : 1;
        Sep.Height = v ? 1 : double.NaN;
        Sep.Margin = v ? new Thickness(10, 6, 10, 6) : new Thickness(6, 10, 6, 10);
        // The weather and status groups stack up on a side, and their parts sit under each other
        foreach (var stack in new[] { WeatherStack, StatusStack })
        {
            stack.Orientation = o;
            stack.Margin = v ? new Thickness(0, 9, 0, 9) : new Thickness(10, 0, 10, 0);
        }
        foreach (var (part, across) in new (FrameworkElement, double)[] { (WeatherTemp, 6), (VolGlyph, 10), (BatteryText, 10), (VolText, 5) })
        {
            part.Margin = v ? new Thickness(0, 6, 0, 0) : new Thickness(across, 0, 0, 0);
            part.HorizontalAlignment = v ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }
        NetGlyph.HorizontalAlignment = WeatherGlyph.HorizontalAlignment = v ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        ClockStack.Margin = v ? new Thickness(0, 7, 0, 7) : new Thickness(10, 0, 10, 0);
        TimeText.HorizontalAlignment = DateText.HorizontalAlignment = v ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        LangStack.Margin = v ? new Thickness(0, 6, 0, 6) : new Thickness(8, 0, 8, 0);
        // Show desktop: a sliver at the end of the bar, across it
        DesktopLine.Width = v ? 20 : 2;
        DesktopLine.Height = v ? 2 : 20;
        DesktopBtn.Margin = v ? new Thickness(0, 4, 0, -2) : new Thickness(4, 0, -2, 0);
    }

    void ApplyShape()
    {
        bool v = Vertical;
        var screen = Native.ScreenDip(ScreenId);
        if (docked)
        {
            if (v) { SizeToContent = SizeToContent.Width; ClearValue(WidthProperty); Height = screen.Height; }
            else { SizeToContent = SizeToContent.Height; ClearValue(HeightProperty); Width = screen.Width; }
            Bar.Margin = new Thickness(0);
            Bar.CornerRadius = new CornerRadius(0);
            Bar.Padding = v ? new Thickness(S.DockPadding + 4, 6, S.DockPadding + 5, 6) : new Thickness(6, S.DockPadding + 4, 6, S.DockPadding + 5);
            // The icons stay where you put the dock, even when it stretches into a bar
            if (v)
            {
                BarContent.HorizontalAlignment = HorizontalAlignment.Stretch;
                BarContent.VerticalAlignment = S.DockPosition == "Left" ? VerticalAlignment.Top : S.DockPosition == "Right" ? VerticalAlignment.Bottom : VerticalAlignment.Center;
                BarContent.Margin = new Thickness(0, S.DockPosition == "Left" ? 10 : 0, 0, S.DockPosition == "Right" ? 10 : 0);
            }
            else
            {
                BarContent.VerticalAlignment = VerticalAlignment.Stretch;
                BarContent.HorizontalAlignment = S.DockPosition == "Left" ? HorizontalAlignment.Left : S.DockPosition == "Right" ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                BarContent.Margin = new Thickness(S.DockPosition == "Left" ? 10 : 0, 0, S.DockPosition == "Right" ? 10 : 0, 0);
            }
        }
        else
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            BarContent.Margin = new Thickness(0);
            double gap = Math.Clamp(S.DockGap, 0, 60), pad = Math.Clamp(S.DockPadding, 0, 30);
            Bar.Margin = Side switch { "Left" => new Thickness(gap, 0, 0, 0), "Right" => new Thickness(0, 0, gap, 0), _ => new Thickness(0, 0, 0, gap) };
            Bar.CornerRadius = new CornerRadius(Math.Clamp(S.DockRadius, 0, 40));
            Bar.Padding = v ? new Thickness(pad, 6, pad, 6) : new Thickness(6, pad, 6, pad);
        }
        ApplySurface();
        Place();
    }

    static readonly Brush GlassDim = Frozen(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    void ApplySurface()
    {
        // A see-through bar would show wallpaper again when docked, so Clear becomes solid there
        var surface = docked && S.DockSurface == "Clear" ? "Solid" : S.DockSurface;
        // Your own colour for the bar; on a light one the clock and labels turn dark so they stay readable
        Color? own = S.DockColor != null && surface != "Clear" ? ThemeManager.Parse(S.DockColor) : null;
        if (own is Color lc && ThemeManager.IsLight(lc)) { Bar.Resources["Brim.Text"] = Brushes.Black; Bar.Resources["Brim.Dim"] = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4C)); }
        else if (surface != "Solid")
        {
            // On glass the date, keyboard region and running marks are light rather than grey, so they read over any wallpaper
            Bar.Resources.Remove("Brim.Text");
            Bar.Resources["Brim.Dim"] = GlassDim;
        }
        else { Bar.Resources.Remove("Brim.Text"); Bar.Resources.Remove("Brim.Dim"); }
        DockApp.IdleDot = surface != "Solid" && !(own is Color dc && ThemeManager.IsLight(dc)) ? GlassDim : null;
        switch (surface)
        {
            case "Glass":
            case "Crystal":
                var c = own ?? ThemeManager.Parse(ThemeManager.Current.Surface);
                bool crystal = surface == "Crystal";
                Bar.Background = Backdrop.Glass(c, crystal ? (own != null ? (byte)0x48 : (byte)0x12) : (own != null ? (byte)0x80 : (byte)0x2E));
                if (crystal)
                {
                    // Nearly clear: a frosted rim covers the strip along the edge the blur doesn't reach, and the
                    // icons and clock get a soft shadow so they stay readable
                    Bar.BorderBrush = Backdrop.CrystalEdge();
                    double band = Math.Max(1, Backdrop.EdgeBand(Bar.CornerRadius.TopLeft, Dpi()));
                    Bar.BorderThickness = docked ? DockedBorder : new Thickness(band);
                    BarContent.Effect = Backdrop.TextLift();
                }
                else
                {
                    Bar.SetResourceReference(Border.BorderBrushProperty, "Brim.Stroke");
                    Bar.BorderThickness = docked ? DockedBorder : new Thickness(1);
                    BarContent.Effect = null;
                }
                if (Backdrop.Available) backdrop ??= new Backdrop();
                else { backdrop?.Dispose(); backdrop = null; }
                break;
            case "Clear":
                Bar.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
                Bar.BorderThickness = new Thickness(0);
                BarContent.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.75, Color = Colors.Black };
                backdrop?.Dispose();
                backdrop = null;
                break;
            default:
                if (own is Color oc) Bar.Background = new SolidColorBrush(oc);
                else Bar.SetResourceReference(Border.BackgroundProperty, "Brim.Surface");
                Bar.SetResourceReference(Border.BorderBrushProperty, "Brim.Stroke");
                Bar.BorderThickness = docked ? DockedBorder : new Thickness(1);
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
        SettingsBtn.Visibility = S.DockSettingsButton ? Visibility.Visible : Visibility.Collapsed;
        // Moved to another edge: give back the old strip first (Windows keeps one per edge)
        if (laidOutSide != Side) { RemoveAppBar(); laidOutSide = Side; reorder = null; }
        DockApp.Side = Side;
        ApplyLayout();
        ApplyShape(); // size, gap, roundness and position may have changed
        ApplySurface();
        var (tile, _) = Sizes;
        StartBtn.Width = StartBtn.Height = tile;
        if (Vertical)
        {
            // A column as wide as an icon: the small buttons get their height back, the others size to what they show
            foreach (var b in new[] { TrayBtn, TaskMgrBtn, SettingsBtn }) { b.Width = tile; b.Height = 30; }
            foreach (var b in new[] { StatusBtn, WeatherBtn, ClockBtn, LangBtn }) { b.Width = tile; b.Height = double.NaN; }
            DesktopBtn.Width = tile;
            DesktopBtn.Height = 16;
        }
        else
        {
            foreach (var b in new[] { TrayBtn, TaskMgrBtn, SettingsBtn }) { b.Width = 30; b.Height = tile; }
            foreach (var b in new[] { StatusBtn, WeatherBtn, ClockBtn, LangBtn }) { b.Width = double.NaN; b.Height = tile; }
            DesktopBtn.Width = 16;
            DesktopBtn.Height = tile;
        }
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
        UpdateLanguage();
        var now = DateTime.Now;
        TimeText.Text = now.ToString(S.Clock24 ? "HH:mm" : "h:mm tt");
        DateText.Text = now.ToString(Vertical ? "d MMM" : "ddd d MMM"); // (a side dock is narrow)
    }

    // ------------------------------------------------------------------ placement

    void Place()
    {
        if (ActualWidth <= 0) return;
        var screen = Native.ScreenDip(ScreenId);
        const double gap = 12;
        if (Vertical)
        {
            // Down a side: "Left / Center / Right" in settings mean top, middle and bottom of it
            Left = Side == "Left" ? screen.Left : screen.Right - ActualWidth;
            Top = docked ? screen.Top + (screen.Height - ActualHeight) / 2 : S.DockPosition switch
            {
                "Left" => screen.Top + gap,
                "Right" => screen.Bottom - ActualHeight - gap,
                _ => screen.Top + (screen.Height - ActualHeight) / 2,
            };
        }
        else
        {
            Left = docked ? screen.Left + (screen.Width - ActualWidth) / 2 : S.DockPosition switch
            {
                "Left" => screen.Left + gap,
                "Right" => screen.Left + screen.Width - ActualWidth - gap,
                _ => screen.Left + (screen.Width - ActualWidth) / 2,
            };
            Top = screen.Bottom - ActualHeight;
        }
        bool hidden = fullscreen || S.DockAutoHide && !shownByEdge;
        // The window stays put; the bar slides off its edge out of sight and back. (The window's empty, see-through
        // parts let clicks through, so a hidden dock doesn't get in the way.)
        Slide(hidden ? Depth : 0);
        if (appBar && !S.DockAutoHide)
        {
            int px = (int)Math.Ceiling(Depth * Dpi());
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
        if (to != slideTarget) Log.Trace($"dock: sliding {(to > 0 ? "away" : "in")}");
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
        // Off its own edge: down at the bottom, left or right on a side
        var along = Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        var across = Vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        if (Side == "Left") move.To = -to;
        slide.BeginAnimation(across, null);
        slide.SetValue(across, 0.0);
        slide.BeginAnimation(along, move);
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
        var mon = Native.ScreenOrPrimary(ScreenId).Bounds;
        double scale = Dpi();
        bool atEdge = Side switch
        {
            "Left" => pt.X <= mon.Left + 1 && pt.Y >= mon.Top && pt.Y < mon.Bottom,
            "Right" => pt.X >= mon.Right - 2 && pt.Y >= mon.Top && pt.Y < mon.Bottom,
            _ => pt.Y >= mon.Bottom - 2 && pt.X >= mon.Left && pt.X < mon.Right,
        };
        bool alongDock = Vertical ? pt.Y >= Top * scale - 10 && pt.Y <= (Top + ActualHeight) * scale + 10
                                  : pt.X >= Left * scale - 10 && pt.X <= (Left + ActualWidth) * scale + 10;
        bool overDock = IsMouseOver || alongDock && Side switch
        {
            "Left" => pt.X <= mon.Left + (ActualWidth + 6) * scale,
            "Right" => pt.X >= mon.Right - (ActualWidth + 6) * scale,
            _ => pt.Y >= mon.Bottom - (ActualHeight + 6) * scale,
        };

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
            if (slideTarget >= Depth && !slideRunning) Visibility = Visibility.Hidden;
            else Slide(Depth, fast: true, done: () => { if (fullscreen) Visibility = Visibility.Hidden; });
        }
        else if (!steppedAside) // (out of Windows' taskbar's way for now: it comes back by itself)
        {
            Visibility = Visibility.Visible;
            Place(); // slides back up (or stays tucked away with auto-hide)
        }
    }

    bool steppedAside;

    /// <summary>Windows' own taskbar is up for a moment (an icon only it can click): get out of its way, then come back.</summary>
    public async void StepAside(TimeSpan time)
    {
        steppedAside = true;
        Visibility = Visibility.Hidden;
        await System.Threading.Tasks.Task.Delay(time);
        steppedAside = false;
        if (!fullscreen) { Visibility = Visibility.Visible; Place(); }
    }

    public void EnsureTopmost()
    {
        if (steppedAside) return;
        // Don't jump in front of a hover label, a preview or an open panel
        if (HoverTip.IsShowing || Flyout.IsOpen) return;
        if (hwnd != IntPtr.Zero && IsVisible) Log.Trace("dock: back on top");
        if (hwnd != IntPtr.Zero && IsVisible)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------ app bar (keeps maximised windows above the dock)

    void SetupAppBar()
    {
        Log.Trace("dock: reserving its strip of the screen");
        if (hwnd == IntPtr.Zero || S.DockAutoHide || !S.DockReserve || ActualHeight <= 0) return;
        if (appBarMsg == 0) appBarMsg = Native.RegisterWindowMessage("BrimDockAppBar");
        var mon = Native.ScreenOrPrimary(ScreenId).Bounds;
        reservedPx = (int)Math.Ceiling(Depth * Dpi());
        // Straight to Explorer (it owns the work area), not through Brim's own tray window
        DockAppBar.Reserve(hwnd, reservedPx, direct: true, callback: appBarMsg, monitor: mon, alreadyRegistered: appBar, edge: AppBarEdge);
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
        if (!StripShort(out int have, out int want)) { reservationOk = true; return; }
        if (reservationOk) Log.Note("dock", $"strip for the dock wasn't reserved (had {have}px, needs {want}px): reserving it again");
        reservationOk = false;
        if (appBar) RemoveAppBar();
        SetupAppBar();
    }

    /// <summary>The work area on this dock's screen doesn't leave room for the dock (or nothing is reserved).</summary>
    bool StripShort(out int have, out int want)
    {
        have = want = 0;
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(Native.ScreenOrPrimary(ScreenId).Handle, ref mi)) return false;
        want = (int)Math.Ceiling(Depth * Dpi());
        have = Side switch { "Left" => mi.rcWork.Left - mi.rcMonitor.Left, "Right" => mi.rcMonitor.Right - mi.rcWork.Right, _ => mi.rcMonitor.Bottom - mi.rcWork.Bottom };
        return !appBar || have < want - 2;
    }

    bool posCheckQueued;

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (appBar && msg == appBarMsg)
        {
            switch (w.ToInt32())
            {
                // Another app bar moved (Explorer's taskbar, or the dock on another screen). Only reserve again when
                // this screen's strip is actually gone: re-reserving every time had the docks on two screens setting
                // each other off, over and over
                case Native.ABN_POSCHANGED:
                    if (posCheckQueued) break;
                    posCheckQueued = true;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        posCheckQueued = false;
                        if (appBar && !S.DockAutoHide && S.DockReserve && StripShort(out _, out _)) SetupAppBar();
                    }), DispatcherPriority.Background);
                    break;
                // ABN_FULLSCREENAPP is left alone on purpose: Windows' own guess disagreed with Brim's check
                // (which also runs on every focus change) and the two made the dock flicker over videos
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ apps

    /// <summary>
    /// How the dock recognises an app: its exe, ignoring version folders, so apps that update themselves into a new
    /// folder each time (Discord's app-1.0.9268, Slack…) still match their pin.
    /// </summary>
    static string KeyFor(string? path)
    {
        // Store apps: by app id, so the running window, a pin of its exe and a pin from the Start list all match
        // (and keep matching after the app updates into a new package folder)
        if (StoreApps.Find(path) is { } store) return store.Key;
        var p = (path ?? "").ToLowerInvariant();
        p = System.Text.RegularExpressions.Regex.Replace(p, @"\\app-\d+(\.\d+)+\\", "\\");
        p = System.Text.RegularExpressions.Regex.Replace(p, @"\\\d+(\.\d+){1,3}\\", "\\");
        return p;
    }

    static readonly Dictionary<IntPtr, string?> hostedIds = new();

    /// <summary>The app id of a Store app window drawn by ApplicationFrameHost (cached; it never changes).</summary>
    static string? HostedAumid(IntPtr hwnd)
    {
        if (hostedIds.TryGetValue(hwnd, out var id)) return id;
        id = StoreApps.AumidOfWindow(hwnd);
        // The frame shows before the app inside it says who it is: only remember a real answer
        if (id != null) { if (hostedIds.Count > 200) hostedIds.Clear(); hostedIds[hwnd] = id; }
        return id;
    }

    static readonly System.Text.RegularExpressions.Regex HelperExe =
        new(@"(?i)(webhelper|helper|renderer|browsersubprocess|cefsharp|crashpad_handler)[^\\]*\.exe$");

    /// <summary>
    /// The app a helper process belongs to (Steam's windows are drawn by steamwebhelper.exe): the exe named after a
    /// folder above it (Steam\steam.exe), else the process that started it. Anything else is its own app.
    /// </summary>
    static string OwningApp(string exe, uint pid)
    {
        if (!HelperExe.IsMatch(exe)) return exe;
        if (owners.TryGetValue(exe + "|" + pid, out var known)) return known;
        string result = AppAbove(exe) ?? ParentApp(pid) ?? exe;
        owners[exe + "|" + pid] = result;
        return result;
    }

    static readonly Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);

    static string? AppAbove(string exe)
    {
        var dir = System.IO.Path.GetDirectoryName(exe);
        for (int i = 0; i < 4 && dir != null && dir.Length > 3; i++)
        {
            var candidate = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(dir) + ".exe");
            if (System.IO.File.Exists(candidate) && !HelperExe.IsMatch(candidate)) return candidate;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        return null;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION { public IntPtr ExitStatus, Peb, Affinity, Priority, UniqueId, ParentId; }
    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int cls, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static string? ParentApp(uint pid)
    {
        var h = OpenProcess(0x1000 /* query limited */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(h, 0, ref info, System.Runtime.InteropServices.Marshal.SizeOf(info), out _) != 0) return null;
            var parent = Native.ProcessPath((uint)info.ParentId.ToInt64());
            return parent != null && !HelperExe.IsMatch(parent) &&
                   !System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(parent), @"(?i)^(explorer|svchost|services|sihost)\.exe$") ? parent : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>A pinned exe whose version folder is gone (the app updated): the same exe in its newest version folder.</summary>
    static string? NewestVersionOf(string exe)
    {
        try
        {
            var versionDir = System.IO.Path.GetDirectoryName(exe);
            var root = versionDir == null ? null : System.IO.Path.GetDirectoryName(versionDir);
            if (root == null || !System.IO.Directory.Exists(root)) return null;
            var name = System.IO.Path.GetFileName(exe);
            return System.IO.Directory.GetDirectories(root)
                .Where(d => System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(d), @"^(app-)?\d+(\.\d+)+$"))
                .Select(d => System.IO.Path.Combine(d, name)).Where(System.IO.File.Exists)
                .OrderByDescending(p => Version.TryParse(System.Text.RegularExpressions.Regex.Match(p, @"\d+(\.\d+)+").Value, out var v) ? v : new Version())
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Keep pins pointing at the real app: past a self-update, and away from helper processes.</summary>
    void HealPins(List<(string key, List<AppWindow> wins)> groups)
    {
        bool changed = false;
        foreach (var pin in S.DockPins)
        {
            if (!pin.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            string? better = null;
            if (HelperExe.IsMatch(pin.Path)) better = AppAbove(pin.Path);                      // pinned Steam's helper
            if (better == null)
            {
                // The app updated: the running copy, or the newest version folder
                var running = groups.FirstOrDefault(g => g.key == KeyFor(pin.Path)).wins?.FirstOrDefault()?.ExePath;
                if (running != null && HelperExe.IsMatch(running) == false && !string.Equals(running, pin.Path, StringComparison.OrdinalIgnoreCase)) better = running;
                else if (!System.IO.File.Exists(pin.Path))
                    // Store apps: wherever the package lives now (WindowsApps can't be listed to look for it)
                    better = StoreApps.Find(pin.Path, fresh: true)?.Exe ?? NewestVersionOf(pin.Path);
            }
            if (better == null || string.Equals(better, pin.Path, StringComparison.OrdinalIgnoreCase)) continue;
            Log.Note("dock", $"pin '{pin.Name}' now points at {better}");
            pinTargets.Remove(pin.Path);
            pin.Path = better;
            if (HelperExe.IsMatch(pin.Name) || pin.Name.Contains("Helper", StringComparison.OrdinalIgnoreCase)) pin.Name = IconHelper.NiceName(better);
            changed = true;
        }
        if (changed) SettingsStore.Save();
    }

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
            // Store apps drawn by ApplicationFrameHost (Calculator, Photos…) say which app they are on the window
            if (hosted && HostedAumid(w.Hwnd) is { } aumid) { w.ExePath = "shell:AppsFolder\\" + aumid; hosted = false; }
            // Windows drawn by a helper process count as the app it belongs to (Steam, not steamwebhelper)
            else if (!hosted && w.ExePath != null) w.ExePath = OwningApp(w.ExePath, w.Pid);
            string key = hosted || w.ExePath == null ? "hwnd:" + w.Hwnd : KeyFor(w.ExePath);
            var g = groups.FirstOrDefault(x => x.key == key);
            if (g.wins == null) groups.Add((key, new List<AppWindow> { w }));
            else g.wins.Add(w);
        }

        HealPins(groups);
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

        // A window you've gone to has had its attention; closed ones are forgotten
        lock (attention) { attention.Remove(fg); attention.RemoveWhere(h => !Native.IsWindow(h)); }
        foreach (var app in order)
        {
            app.TileSize = tile;
            app.IconSize = iconSize;
            app.Running = app.Windows.Count > 0;
            app.Active = app.Windows.Any(w => w.Hwnd == fg);
            lock (attention) app.Attention = !app.Active && app.Windows.Any(w => attention.Contains(w.Hwnd));
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

    /// <summary>Windows that asked for attention (they flash orange until you go to them), shared by every screen's dock.</summary>
    static readonly HashSet<IntPtr> attention = new();

    /// <summary>A window flashed for attention: true when it's new (and not the window you're already in).</summary>
    public static bool WantsAttention(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == Native.GetForegroundWindow()) return false;
        lock (attention) return attention.Add(hwnd);
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

    LiveReorder? reorder;
    Panel? reorderHost;
    internal LiveReorder? DebugReorder(out FrameworkElement? first, out Panel? host)
    {
        first = apps.Count > 0 ? Apps.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement : null;
        host = first == null ? null : VisualTreeHelper.GetParent(first) as Panel;
        if (host == null) return null;
        return new LiveReorder(host, _ => true, (_, from, to) => apps.Move(from, to), _ => { }, lift: -6, liftScale: 1.15);
    }

    void App_DragMove(object sender, MouseEventArgs e)
    {
        if (pressed == null || dragging || e.LeftButton != MouseButtonState.Pressed || S.DockLocked) return;
        var moved = e.GetPosition(Apps) - pressAt;
        if (Math.Abs(Vertical ? moved.Y : moved.X) < 8) return; // along the dock (down it, on a side)
        // Past the threshold: lift the icon and carry it (the others slide aside as it passes them)
        if (Apps.ItemContainerGenerator.ContainerFromItem(pressed) is not FrameworkElement box || VisualTreeHelper.GetParent(box) is not Panel host) return;
        dragging = true;
        previewDelay.Stop();
        WindowPreview.HideNow();
        if (reorderHost != host) reorder = null; // the icon row was rebuilt
        reorderHost = host;
        reorder ??= new LiveReorder(host, _ => true, (_, from, to) => apps.Move(from, to), changed =>
        {
            dragging = false;
            pressed = null;
            if (changed)
            {
                S.DockOrder = apps.Select(a => a.Key).ToList();
                SettingsStore.Save();
            }
            // The button would treat letting go as a click; skip that one
            swallowClick = true;
            Dispatcher.BeginInvoke(new Action(() => swallowClick = false), DispatcherPriority.Input);
        }, lift: -6, liftScale: 1.15);
        reorder.Vertical = Vertical;
        reorder.Begin(box, e.GetPosition(host));
    }

    void App_DragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) pressed = null;
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

    // ---------- drag a file over a running app: after a moment its window comes up, so you can drop the file into it
    // (like Windows' taskbar). Held there longer, an app with several windows shows the next one.

    readonly DispatcherTimer dragSwitch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    DockApp? dragOverApp;

    void App_DragEnter(object sender, DragEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not DockApp app || app.Windows.Count == 0) return;
        if (dragOverApp == app && dragSwitch.IsEnabled) return; // (moving across the icon's own parts)
        WindowPreview.HideNow();
        dragOverApp = app;
        dragSwitch.Tick -= DragSwitchTick;
        dragSwitch.Tick += DragSwitchTick;
        dragSwitch.Interval = TimeSpan.FromMilliseconds(500);
        dragSwitch.Stop();
        dragSwitch.Start();
    }

    void App_DragLeave(object sender, DragEventArgs e)
    {
        // Only when the pointer really left the icon (not just moved from its picture to its edge)
        if (sender is FrameworkElement tile && tile.Tag == dragOverApp)
        {
            var at = e.GetPosition(tile);
            if (at.X >= 0 && at.Y >= 0 && at.X < tile.ActualWidth && at.Y < tile.ActualHeight) return;
            dragSwitch.Stop();
            dragOverApp = null;
        }
    }

    void DragSwitchTick(object? s, EventArgs e)
    {
        var app = dragOverApp;
        if (app == null || app.Windows.Count == 0) { dragSwitch.Stop(); return; }
        var fg = Native.GetForegroundWindow();
        int idx = app.Windows.FindIndex(w => w.Hwnd == fg);
        if (idx < 0 || app.Windows.Count > 1)
        {
            keepVisibleUntil = DateTime.Now.AddSeconds(1.5);
            Native.ForceForeground(app.Windows[idx < 0 ? 0 : (idx + 1) % app.Windows.Count].Hwnd);
        }
        dragSwitch.Interval = TimeSpan.FromMilliseconds(1200); // still there: the next window after a bit longer
    }

    void App_Drop(object sender, DragEventArgs e)
    {
        dragSwitch.Stop();
        dragOverApp = null;
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
        // Like Windows' taskbar: Shift+click opens a new window, Ctrl+Shift+click opens it as administrator
        // (read straight from the keyboard: the dock never has the keyboard, so WPF's idea of it can be stale)
        if (Native.IsKeyDown(0x10 /* Shift */) && app.LaunchPath != null && !app.Key.StartsWith("hwnd:"))
        {
            if (Native.IsKeyDown(0x11 /* Ctrl */) && Launcher.CanRunAsAdmin(app.LaunchPath)) Launcher.RunAsAdmin(app.LaunchPath, app.LaunchArgs);
            else Launcher.Open(app.LaunchPath, app.LaunchArgs);
            return;
        }
        await Activate(app);
    }

    /// <summary>Win+1…9: the dock's 1st–9th app, the same as clicking it.</summary>
    public void OpenApp(int n)
    {
        if (n < 1 || n > apps.Count) return;
        _ = Activate(apps[n - 1]);
    }

    /// <summary>Not running: start it. Running: bring it up; already in front: minimise it, or its next window.</summary>
    async System.Threading.Tasks.Task Activate(DockApp app)
    {
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
        // End task, like Windows 11's taskbar: stops the app straight away (for one that's stuck or won't close)
        var targets = EndTaskTargets(app);
        if (targets.Count > 0)
            menu.Items.Add(Item("End task", () => EndTask(app, targets)));
        if (menu.Items.Count == 0) return;
        IsAnyMenuOpen = true;
        var tile = (UIElement)sender;
        HoverMenu.SetOwner(menu, (FrameworkElement)tile); // closes itself once the pointer leaves the app and the menu
        menu.Closed += (_, _) => IsAnyMenuOpen = false;
        menu.PlacementTarget = tile;
        menu.Placement = MenuPlacement;
        menu.IsOpen = true;
    }

    /// <summary>The processes behind an app's windows that End task may stop (never Windows' own or Brim).</summary>
    static List<int> EndTaskTargets(DockApp app)
    {
        var mine = Environment.ProcessId;
        // Store apps live inside a Windows host window: the real app is the one drawing inside it
        int RealPid(AppWindow w)
        {
            // (the window's own process, not ExePath: the dock names hosted windows by their app id)
            if (!string.Equals(System.IO.Path.GetFileName(Native.ProcessPath(w.Pid) ?? ""), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return (int)w.Pid;
            var inner = Native.FindWindowEx(w.Hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (inner == IntPtr.Zero) return 0;
            Native.GetWindowThreadProcessId(inner, out uint p);
            return (int)p;
        }
        // A window drawn by a helper (Steam's): the app itself too, or it would just start the helper again
        var owners = app.Windows.Where(w => w.ExePath != null && !w.ExePath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) &&
                                            !string.Equals(Native.ProcessPath(w.Pid), w.ExePath, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.ExePath!).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(path => System.Diagnostics.Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(path))
                .Where(p => string.Equals(Native.ProcessPath((uint)p.Id), path, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id))
            .ToList();
        return app.Windows.Select(RealPid).Concat(owners).Where(pid => pid > 4 && pid != mine).Distinct()
            .Where(pid => !System.Text.RegularExpressions.Regex.IsMatch(
                System.IO.Path.GetFileName(Native.ProcessPath((uint)pid) ?? ""),
                @"(?i)^(brim|explorer|applicationframehost|csrss|dwm|winlogon|lsass|services|svchost|sihost|shellexperiencehost|startmenuexperiencehost|searchhost|textinputhost|systemsettings)\.exe$"))
            .ToList();
    }

    /// <summary>For `--endtask-test`: which of these would End task stop, then stop them.</summary>
    internal static string EndTaskTest(List<AppWindow> windows)
    {
        var app = new DockApp { Key = "test", Title = "test", Windows = windows };
        var t = EndTaskTargets(app);
        var said = "would end: " + string.Join(", ", t.Select(p => System.IO.Path.GetFileName(Native.ProcessPath((uint)p) ?? p.ToString())));
        // Never actually end Brim or Explorer in a test, even if the rules above were wrong
        if (t.Any(p => System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(Native.ProcessPath((uint)p) ?? ""), @"(?i)^(brim|explorer)\.exe$"))) return "FAIL " + said;
        EndTask(app, t);
        return said;
    }

    static void EndTask(DockApp app, List<int> pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                Log.Note("dock", $"ended {System.IO.Path.GetFileName(Native.ProcessPath((uint)pid) ?? "?")} ({app.Title})");
            }
            catch (Exception ex) { Log.Note("dock", $"couldn't end {app.Title}: {ex.Message}"); }
        }
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

    // Windows' own Settings (Brim's are in the notch)
    void Settings_Click(object sender, RoutedEventArgs e) => Launcher.Open("ms-settings:");

    // ---------- right-click the dock's empty space, like Windows' taskbar

    void Bar_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return; // an app or the language has its own menu
        e.Handled = true;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Task Manager", () => TaskMgr_Click(this, new RoutedEventArgs())));
        menu.Items.Add(Item("Show desktop", () => Desktop_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new Separator());
        var locked = new MenuItem { Header = "Lock the icons in place", IsCheckable = true, IsChecked = S.DockLocked, ToolTip = "No dragging them into a new order by accident" };
        locked.Click += (_, _) => { S.DockLocked = !S.DockLocked; SettingsStore.Save(); };
        menu.Items.Add(locked);
        menu.Items.Add(Item("Dock settings…", () => ((App)Application.Current).OpenSettings("Taskbar")));
        menu.Items.Add(Item("Windows Settings", () => Launcher.Open("ms-settings:")));
        IsAnyMenuOpen = true;
        HoverMenu.SetOwner(menu, Bar);
        menu.Closed += (_, _) => IsAnyMenuOpen = false;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // ---------- drop a program or shortcut on the dock (not on an app) to pin it

    static List<string> Pinnable(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) && System.IO.File.Exists(f)).ToList()
            : new();

    void Bar_DragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Effects = Pinnable(e.Data).Count > 0 ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void Bar_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        int added = 0;
        foreach (var f in Pinnable(e.Data))
        {
            var key = KeyFor(PinTarget(new ShortcutItem { Path = f }) ?? f);
            if (S.DockPins.Any(p => KeyFor(PinTarget(p) ?? p.Path) == key)) continue; // already pinned
            S.DockPins.Add(new ShortcutItem { Name = IconHelper.NiceName(f), Path = f });
            added++;
        }
        if (added == 0) return;
        SettingsStore.Save();
        RefreshApps();
        ((App)Application.Current).RefreshSettingsWindow();
    }

    // ---------- the sound icon, like Windows' taskbar: scroll for the volume, middle-click to mute

    readonly DispatcherTimer volTextHide = new() { Interval = TimeSpan.FromMilliseconds(1400) };

    void Status_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        // 2% a notch (a smooth-scrolling touchpad sends smaller steps, so it moves in smaller ones)
        float step = 0.02f * Math.Clamp(e.Delta / 120f, -3f, 3f);
        float v = Math.Clamp(Audio.Volume + step, 0f, 1f);
        Audio.Volume = v;
        if (step > 0 && Audio.Muted) Audio.Muted = false; // turning it up unmutes, as in Windows
        VolGlyph.Text = Audio.Muted ? "" : "";
        VolText.Text = $"{Math.Round(v * 100)}";
        VolText.Visibility = Visibility.Visible;
        volTextHide.Stop();
        volTextHide.Tick -= HideVolText;
        volTextHide.Tick += HideVolText;
        volTextHide.Start();
    }

    void HideVolText(object? s, EventArgs e) { volTextHide.Stop(); VolText.Visibility = Visibility.Collapsed; }

    void Status_MiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        Audio.Muted = !Audio.Muted; // (the notch shows "Muted" / "Unmuted")
        VolGlyph.Text = Audio.Muted ? "" : "";
    }

    // ---------- keyboard language (ENG, ARA…), like Windows' taskbar

    KeyboardLanguage.Layout? shownLang;

    /// <summary>Every second: the language of the window you're typing in. Only shown when Windows has more than one.</summary>
    void UpdateLanguage()
    {
        var all = S.DockLanguage ? KeyboardLanguage.All() : new();
        if (all.Count < 2) { LangBtn.Visibility = Visibility.Collapsed; shownLang = null; return; }
        if (KeyboardLanguage.Current() is not { } now) return;
        LangBtn.Visibility = Visibility.Visible;
        if (now == shownLang) return;
        shownLang = now;
        LangText.Text = now.Short;
        LangRegion.Text = now.Region;
        LangRegion.Visibility = now.Region.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LangBtn.ToolTip = $"{now.Name}\nClick to switch · right-click to pick";
    }

    /// <summary>Show the new language straight away, not on the next second.</summary>
    async void LanguageSoon()
    {
        await System.Threading.Tasks.Task.Delay(120);
        shownLang = null;
        UpdateLanguage();
    }

    // Click: the next language, in the window you were typing in (the dock never takes the keyboard from it)
    void Lang_Click(object sender, RoutedEventArgs e)
    {
        var window = KeyboardLanguage.Window;
        KeyboardLanguage.Switch(window, KeyboardLanguage.Next(KeyboardLanguage.Current()));
        LanguageSoon();
    }

    void Lang_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var window = KeyboardLanguage.Window; // before the menu opens
        var now = KeyboardLanguage.Current();
        var menu = new ContextMenu();
        foreach (var l in KeyboardLanguage.All())
        {
            var layout = l;
            menu.Items.Add(Item((now?.Hkl == l.Hkl ? "✓  " : "     ") + l.Name, () => { KeyboardLanguage.Switch(window, layout.Hkl); LanguageSoon(); }));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Language settings…", () => Launcher.Open("ms-settings:regionlanguage")));
        menu.Items.Add(Item("Hide from the dock", () =>
        {
            S.DockLanguage = false;
            SettingsStore.Save();
            UpdateLanguage();
            ((App)Application.Current).RefreshSettingsWindow();
        }));
        IsAnyMenuOpen = true;
        HoverMenu.SetOwner(menu, LangBtn);
        menu.Closed += (_, _) => IsAnyMenuOpen = false;
        menu.PlacementTarget = LangBtn;
        menu.Placement = MenuPlacement;
        menu.IsOpen = true;
    }

    // Task Manager (Windows' own; on every Windows 10/11 PC)
    void TaskMgr_Click(object sender, RoutedEventArgs e) =>
        Launcher.Open(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe"));
    // Same as Windows' corner button: shows the desktop, and clicking again restores every window
    async void Desktop_Click(object sender, RoutedEventArgs e)
    {
        keepVisibleUntil = DateTime.Now.AddSeconds(1.5);
        AttentionWatcher.DesktopToggled(); // (the apps coming back flash for a moment: not a call for you)
        // If Start or Search is still open, close it and let it finish closing,
        // otherwise Explorer ignores the first toggle (that was the "needs a double click")
        var fg = Native.GetForegroundWindow();
        if (Native.ClassOf(fg) == "Windows.UI.Core.CoreWindow")
        {
            Native.TapKeys(0x1B /* Esc */);
            await System.Threading.Tasks.Task.Delay(220);
        }
        // Straight to the taskbar's own "show desktop" command (instant: no fake key presses a game or Start could eat,
        // and none of the shell automation object's start-up time)
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && Native.PostMessage(tray, 0x0111 /* WM_COMMAND */, new IntPtr(407 /* toggle desktop */), IntPtr.Zero)) return;
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
