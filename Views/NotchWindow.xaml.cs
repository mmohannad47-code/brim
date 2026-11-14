using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Views;

public sealed class ShortcutVM : Observable
{
    public ShortcutItem? Item { get; init; }
    public string Name { get; init; } = "";
    ImageSource? icon;
    string? glyph;
    /// <summary>Settable: a website's logo arrives a moment after the tile is drawn</summary>
    public ImageSource? Icon { get => icon; set => Set(ref icon, value); }
    public string? Glyph { get => glyph; set => Set(ref glyph, value); }
    public string? Badge { get; init; }
    public string? Tip { get; init; }
    public bool IsAdd => Item == null;

    bool dragging;
    /// <summary>The tile you're dragging fades a little so you can see where it's going.</summary>
    public bool Dragging { get => dragging; set { Set(ref dragging, value); RaiseChanged(nameof(DragOpacity)); } }
    public double DragOpacity => dragging ? 0.45 : 1;
}

public partial class NotchWindow : Window
{
    enum Mode { Closed, Peek, Open, Activity }

    Mode mode = Mode.Closed;
    readonly DispatcherTimer hoverTimer = new(), leaveTimer = new(), peekTimer = new(), barsTimer = new(), noteTimer = new();
    readonly ObservableCollection<ShortcutVM> shortcuts = new();
    readonly double[] barLevels = new double[4];
    readonly Random rnd = new();
    int holdOpen;
    bool seeking, settingVolume, dragOver, draggingOut, fullscreenHidden;
    Point dragStart;
    Size lastClosed;
    IntPtr hwnd;

    const double OpenW = 580, OpenH = 244;

    static AppSettings S => SettingsStore.Current;
    static MediaService M => MediaService.Instance;

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(NotchWindow),
        new PropertyMetadata(12.0, (d, _) => ((NotchWindow)d).ApplyRadius()));

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    double SpeedMs => S.Speed switch { "Quick" => 220, "Relaxed" => 560, _ => 380 };

    /// <summary>Notches on the other screens ("Every screen"): they show what the main one shows.</summary>
    public static readonly List<NotchWindow> Mirrors = new();
    /// <summary>The screen this notch sits on when it isn't the main one (null: the one picked in settings).</summary>
    readonly string? screenDevice;
    public bool IsMirror => screenDevice != null;
    public IntPtr Monitor => Native.ScreenOrPrimary(screenDevice ?? (S.NotchScreen == "*" ? "" : S.NotchScreen)).Handle;
    public string? ScreenDevice => screenDevice;

    /// <summary>A notch for another screen (copies the main one's pop-ups instead of watching the system itself).</summary>
    public NotchWindow(string device) : this() { screenDevice = device; watch.Stop(); }

    public NotchWindow()
    {
        InitializeComponent();
        // Clicking anywhere on these bars jumps there (by default a click only nudges by a step)
        Progress.IsMoveToPointEnabled = true;
        VolumeSlider.IsMoveToPointEnabled = true;
        // The slider marks a click on its track as handled (it moves itself there), so listen for handled ones too;
        // otherwise only dragging the knob ever told the player to skip
        Progress.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Progress_Down), handledEventsToo: true);
        Progress.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Progress_Up), handledEventsToo: true);

        AppsList.ItemsSource = shortcuts;
        ShelfList.ItemsSource = Shelf.Items;
        ClipList.ItemsSource = ClipboardHistory.Items;
        Shelf.Items.CollectionChanged += (_, _) => UpdateShelfState();
        ClipboardHistory.Items.CollectionChanged += (_, _) =>
            ClipEmpty.Visibility = ClipboardHistory.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        HoverMenu.SetRegion(this, Body); // its menus stay while the pointer is on the notch's card or the menu
        hoverTimer.Tick += (_, _) => { hoverTimer.Stop(); Open(); };
        leaveTimer.Interval = TimeSpan.FromMilliseconds(320);
        leaveTimer.Tick += (_, _) =>
        {
            leaveTimer.Stop();
            if (!Body.IsMouseOver && holdOpen == 0 && !dragOver) Collapse();
        };
        peekTimer.Interval = TimeSpan.FromSeconds(3.2);
        peekTimer.Tick += (_, _) => { peekTimer.Stop(); if (mode == Mode.Peek) Collapse(); };
        barsTimer.Interval = TimeSpan.FromMilliseconds(33);
        barsTimer.Tick += (_, _) => AnimateBars();
        noteTimer.Interval = TimeSpan.FromSeconds(1.4);
        noteTimer.Tick += (_, _) => { noteTimer.Stop(); UpdateHeader(); };
        activityTimer.Tick += (_, _) => { activityTimer.Stop(); if (mode == Mode.Activity) Collapse(); };
        watch.Tick += (_, _) => WatchSystem();
        watch.Start();
        FocusTimer.Changed += () => { if (mode == Mode.Closed) UpdateClosedContent(resize: false); };
        FocusTimer.Finished += () => { if (!IsMirror) ShowActivity("", "Time's up", "Focus done", null, 6); };

        Body.MouseEnter += (_, _) =>
        {
            leaveTimer.Stop();
            // A pop-up you can click (a stream went live, an update): stay put so the click lands on it
            if (mode == Mode.Activity && activityClick != null) { activityTimer.Stop(); return; }
            if (mode != Mode.Open && !fullscreenHidden)
            {
                hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, S.HoverDelayMs));
                hoverTimer.Start();
            }
        };
        Body.MouseLeave += (_, _) =>
        {
            hoverTimer.Stop();
            if (mode == Mode.Open && holdOpen == 0) leaveTimer.Start();
            if (mode == Mode.Activity && activityClick != null) { activityTimer.Interval = TimeSpan.FromSeconds(2); activityTimer.Start(); }
        };
        Body.MouseLeftButtonUp += (_, _) =>
        {
            if (mode == Mode.Activity && activityClick is { } act)
            {
                activityClick = null;
                activityTimer.Stop();
                Collapse();
                try { act(); } catch (Exception ex) { Log.Error("pop-up click", ex); }
                return;
            }
            if (mode != Mode.Open) Open();
        };
        Body.DragEnter += Body_DragEnter;
        Body.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !draggingOut ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Body.DragLeave += Body_DragLeave;
        Body.Drop += Body_Drop;

        M.Changed += OnMedia;
        M.TrackChanged += OnTrack;
        App.SecondTick += OnSecond;
        DeviceBattery.Changed += UpdateDeviceChips;
        Loaded += (_, _) => UpdateDeviceChips();

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, true);
            ApplyCapture();
        };
        Body.LayoutUpdated += (_, _) => SyncBackdrop();
        // Leaving the clipboard search (click elsewhere or Esc) gives the notch back its usual behaviour
        Deactivated += (_, _) => { if (typing) { StopTyping(); if (!Body.IsMouseOver) leaveTimer.Start(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && typing) { StopTyping(); Collapse(); e.Handled = true; } };

        // OLED care: tiny shifts over time, dim when you're away. A notch hanging from the top edge
        // only moves sideways or up, never leaving a gap above it.
        OledCare.Shift += (dx, dy) =>
        {
            ShapeRow.RenderTransform = new TranslateTransform(dx, S.Shape == NotchShape.Notch ? Math.Min(0, dy) : dy);
            SyncBackdrop();
        };
        OledCare.Dim += dim => ShapeRow.BeginAnimation(OpacityProperty, new DoubleAnimation(dim ? 0.35 : 1, TimeSpan.FromMilliseconds(dim ? 1500 : 250)));

        // Say what each tab is in the notch's own header while you point at it
        // (pop-up tooltips aren't reliable on a window that never takes focus)
        foreach (var (tab, hint) in new (RadioButton, string)[]
        {
            (TabHome, "Home · music, date and system"),
            (TabApps, "Shortcuts · your apps, folders and sites"),
            (TabGames, "Games · everything you have installed"),
            (TabMsgs, "Messages · Teams, Discord, Outlook…"),
            (TabShelf, "Shelf · drop files here for later"),
            (TabClip, "Clipboard · things you copied"),
            (TabCtrl, "Controls · Wi-Fi, Bluetooth and sound"),
            (TabTimer, "Focus timer"),
            (TabNotes, "Notes · type anything, it saves by itself"),
        })
        {
            tab.ToolTip = null;
            tab.MouseEnter += (_, _) => { noteTimer.Stop(); tabHint = true; HeaderInfo.Text = hint; };
            tab.MouseLeave += (_, _) => { tabHint = false; UpdateHeader(); };
        }
        Closed += (_, _) => { backdrop?.Dispose(); backdrop = null; };
        Loaded += (_, _) => { ApplySettings(); OnMedia(); OnSecond(); };
    }

    // ------------------------------------------------------------------ settings & placement

    public void ApplySettings()
    {
        foreach (var m in MirrorsOf()) m.ApplySettings();
        var screen = Native.ScreenDip(screenDevice ?? (S.NotchScreen == "*" ? "" : S.NotchScreen));
        // Left / Center / Right along the top edge. The window is wider than the open card, so the card is
        // pinned to the window's matching side and the window to the screen's (with a small gap from the corner).
        const double gap = 18;
        ShapeRow.HorizontalAlignment = S.NotchPosition switch
        {
            "Left" => HorizontalAlignment.Left,
            "Right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        Left = S.NotchPosition switch
        {
            "Left" => screen.Left + gap,
            "Right" => screen.Left + screen.Width - Width - gap,
            _ => screen.Left + (screen.Width - Width) / 2,
        };
        Top = screen.Top;
        ApplyTabs();
        ApplyHome();
        bool island = S.Shape == NotchShape.Island;
        OpenInner.Margin = island ? new Thickness(20, 16, 20, 18) : new Thickness(20, 10, 20, 18);
        RebuildShortcuts();
        ApplyCapture();
        UpdateShelfState();
        ClipEmpty.Visibility = ClipboardHistory.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        lastClosed = default;
        if (S.NotchSurface == "Solid") { backdrop?.Dispose(); backdrop = null; }
        else if (backdrop == null)
        {
            backdrop = new Backdrop();
            if (S.HideFromCapture) backdrop.SetCaptureHidden(true);
        }
        ApplyCapture();
        if (mode != Mode.Open) Collapse(animate: false);
        else { ApplySurface(); ApplyChrome(); }
        UpdateTint();
        ForceRedraw();
    }

    bool? appliedCapture;

    void ApplyCapture()
    {
        // Changing a see-through window's capture setting makes Windows drop its picture until it next draws
        // (the "opaque box" after adding a widget). So only touch it when it really changes, then redraw at once.
        if (hwnd == IntPtr.Zero || appliedCapture == S.HideFromCapture) return;
        bool first = appliedCapture == null;
        appliedCapture = S.HideFromCapture;
        if (first && !S.HideFromCapture) return; // nothing to change on a fresh window
        Native.SetWindowDisplayAffinity(hwnd, S.HideFromCapture ? 0x11u : 0u);
        backdrop?.SetCaptureHidden(S.HideFromCapture);
        ForceRedraw();
    }

    /// <summary>Make WPF push a fresh frame of this see-through window to the screen.</summary>
    void ForceRedraw()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var o = Body.Opacity;
            Body.Opacity = o > 0.5 ? o - 0.001 : o + 0.001;
            Dispatcher.BeginInvoke(new Action(() => Body.Opacity = o), System.Windows.Threading.DispatcherPriority.Render);
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    // ------------------------------------------------------------------ glass

    Backdrop? backdrop;
    bool ClearAndClosed => S.NotchSurface == "Clear" && mode == Mode.Closed;
    bool sliverSettled;

    /// <summary>Solid fill, or a see-through tint over the blur layer.</summary>
    void ApplySurface()
    {
        if (HideMode && mode == Mode.Closed && sliverSettled)
        {
            // The hidden strip fades to 2% opacity. It needs a fully opaque fill underneath, otherwise
            // (with Clear or Glass) it ends up 100% transparent and Windows stops sending it the mouse.
            Body.Background = Brushes.Black;
            FaceText.Effect = null;
            return;
        }
        if (S.NotchSurface == "Solid")
        {
            Body.SetResourceReference(Border.BackgroundProperty, "Brim.Surface");
            FaceText.Effect = null;
            return;
        }
        if (ClearAndClosed)
        {
            // Invisible, but still there to be pointed at
            Body.Background = new SolidColorBrush(Color.FromArgb(2, 0, 0, 0));
            FaceText.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9, Color = Colors.Black };
        }
        else
        {
            var c = ThemeManager.Parse(ThemeManager.Current.Surface);
            Body.Background = new SolidColorBrush(Color.FromArgb(0x70, c.R, c.G, c.B));
            FaceText.Effect = null;
        }
        SyncBackdrop();
    }

    void SyncBackdrop()
    {
        if (backdrop == null) return;
        if (!IsVisible || fullscreenHidden || Sliver || ClearAndClosed || Body.ActualWidth < 2 || PresentationSource.FromVisual(Body) == null)
        {
            backdrop.Hide();
            return;
        }
        var tl = Body.PointToScreen(new Point(0, 0));
        var br = Body.PointToScreen(new Point(Body.ActualWidth, Body.ActualHeight));
        double scale = (br.X - tl.X) / Math.Max(1, Body.ActualWidth);
        backdrop.Place(hwnd, (int)Math.Round(tl.X), (int)Math.Round(tl.Y), (int)Math.Round(br.X - tl.X), (int)Math.Round(br.Y - tl.Y),
            (int)Math.Round(Radius * scale), S.Shape == NotchShape.Notch);
    }

    IEnumerable<NotchWindow> MirrorsOf() => IsMirror ? Enumerable.Empty<NotchWindow>() : Mirrors.ToList();

    public void EnsureTopmost()
    {
        foreach (var m in MirrorsOf()) m.EnsureTopmost();
        // While a right-click menu, dialog or drag is up, raising the notch would push it back over the menu
        if (holdOpen > 0 || HoverTip.IsShowing || mode == Mode.Open && AnyPopupOpen()) return;
        if (hwnd != IntPtr.Zero && IsVisible)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void SetFullscreenHidden(bool hide)
    {
        if (hide == fullscreenHidden) return;
        fullscreenHidden = hide;
        Log.Note("notch", (hide ? "hidden: fullscreen " : "shown: no fullscreen ") + Log.Foreground());
        if (hide)
        {
            Collapse(animate: false);
            Visibility = Visibility.Hidden;
            backdrop?.Hide();
        }
        else
        {
            Visibility = Visibility.Visible;
            SyncBackdrop();
        }
    }

    /// <summary>True when any of this window's menus or tooltips is showing.</summary>
    bool AnyPopupOpen()
    {
        foreach (PresentationSource ps in PresentationSource.CurrentSources)
            if (ps.RootVisual is FrameworkElement root && root.Parent is System.Windows.Controls.Primitives.Popup { IsOpen: true } p &&
                p.PlacementTarget is DependencyObject t && IsInsideNotch(t)) return true;
        return false;
    }

    bool IsInsideNotch(DependencyObject d)
    {
        while (d != null)
        {
            if (d == this) return true;
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    internal void DebugOpen(string tab) => Open(tab switch
    {
        "Apps" => TabApps,
        "Shelf" => TabShelf,
        "Clip" => TabClip,
        "Ctrl" => TabCtrl,
        "Games" => TabGames,
        "Timer" => TabTimer,
        "Messages" => TabMsgs,
        "Notes" => TabNotes,
        _ => TabHome,
    });

    RadioButton? TabFor(string id) => id switch
    {
        "Home" => TabHome, "Apps" => TabApps, "Games" => TabGames, "Messages" => TabMsgs, "Shelf" => TabShelf,
        "Clip" => TabClip, "Ctrl" => TabCtrl, "Timer" => TabTimer, "Notes" => TabNotes, _ => null,
    };

    IEnumerable<RadioButton> AllTabs => new[] { TabHome, TabApps, TabGames, TabMsgs, TabNotes, TabShelf, TabClip, TabCtrl, TabTimer };

    /// <summary>The first tab you kept (where the notch opens when Home is hidden).</summary>
    RadioButton FirstTab => S.NotchTabs.Select(TabFor).FirstOrDefault(t => t != null) ?? TabHome;

    /// <summary>Show your tabs, in your order.</summary>
    void ApplyTabs()
    {
        var wanted = S.NotchTabs.Select(TabFor).Where(t => t != null).Cast<RadioButton>().Distinct().ToList();
        if (wanted.Count == 0) wanted.Add(TabHome);
        if (TabsRow.Children.Cast<RadioButton>().SequenceEqual(wanted)) return;
        TabsRow.Children.Clear();
        // Narrower buttons when there are many, so the battery and clock beside them keep their room
        double w = wanted.Count <= 7 ? 40 : wanted.Count == 8 ? 37 : 33;
        foreach (var t in wanted) { t.Width = w; TabsRow.Children.Add(t); }
        foreach (var t in AllTabs.Except(wanted)) t.IsChecked = false;
        if (!wanted.Any(t => t.IsChecked == true)) wanted[0].IsChecked = true;
        Tab_Checked(this, new RoutedEventArgs());
    }

    /// <summary>Show the Home parts you picked, in your order; without "Now playing" the rest gets the whole width.</summary>
    void ApplyHome()
    {
        var items = S.HomeItems;
        bool music = items.Contains("Music");
        MusicArea.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(GlanceScroll, music ? 2 : 0);
        Grid.SetColumnSpan(GlanceScroll, music ? 1 : 3);

        bool date = items.Contains("Date"), devices = items.Contains("Devices");
        DayText.Visibility = date ? Visibility.Visible : Visibility.Collapsed;
        DateText.Visibility = date ? Visibility.Visible : Visibility.Collapsed;
        DeviceChips.Visibility = devices ? Visibility.Visible : Visibility.Collapsed;

        var blocks = new (string id, FrameworkElement el)[]
        {
            ("Date", DateBlock), ("Weather", WeatherBlock), ("Battery", BatteryBlock), ("Cpu", CpuBlock),
            ("Memory", RamBlock), ("Volume", VolumeBlock), ("Message", MessageBlock),
        };
        foreach (var (_, el) in blocks) el.Tag ??= el.Margin; // remember the designed spacing
        Glance.Children.Clear();
        // Beside the music: one narrow column. On its own: two columns across the card.
        const double narrow = 140, wide = 255, gutter = 30;
        int index = 0;
        foreach (var id in items)
        {
            var key = id == "Devices" ? "Date" : id;
            var el = blocks.FirstOrDefault(b => b.id == key).el;
            if (el == null || Glance.Children.Contains(el)) continue;
            if (id == "Battery" && !SystemInfo.HasBattery) continue;
            var m = (Thickness)el.Tag;
            bool topRow = music ? index == 0 : index < 2;
            el.Width = music ? narrow : wide;
            el.Margin = new Thickness(0, topRow ? 0 : m.Top, !music && index % 2 == 0 ? gutter : 0, m.Bottom);
            Glance.Children.Add(el);
            index++;
        }
        if (mode == Mode.Open) RefreshOpen();
    }

    void MessageBlock_Click(object sender, MouseButtonEventArgs e)
    {
        if (TabMsgs.Parent != null) TabMsgs.IsChecked = true;
        else if (MessageCenter.Visible.FirstOrDefault() is { } m) { MessageCenter.Open(m); Collapse(); }
    }

    internal void DebugPeek() { Collapse(animate: false); Peek(); }

    public void ToggleOpen()
    {
        if (fullscreenHidden) return;
        if (mode == Mode.Open) Collapse(); else Open();
    }

    // ------------------------------------------------------------------ shape & animation

    bool SongActive => S.ShowSongWhenClosed && M.HasSession && M.IsPlaying && !string.IsNullOrEmpty(M.Title);
    bool Sliver => HideMode && mode == Mode.Closed;

    /// <summary>Hidden-until-pointed: always, or (optionally) only while an app window is in front.</summary>
    bool HideMode => S.HideUntilHover || (S.HideOverApps && appInFront);
    bool appInFront;

    /// <summary>Called when focus moves: shows the notch on the desktop, tucks it away over apps.</summary>
    public void SetAppInFront(bool app)
    {
        foreach (var m in MirrorsOf()) m.SetAppInFront(app);
        if (app == appInFront) return;
        appInFront = app;
        Log.Note("notch", (app ? "tucked away: app in front " : "back: desktop in front ") + Log.Foreground());
        if (S.HideOverApps && !S.HideUntilHover && mode == Mode.Closed) { lastClosed = default; Collapse(); }
    }

    Size ClosedSize()
    {
        bool island = S.Shape == NotchShape.Island;
        double h = island ? 34 : 32;
        if (HideMode) return new Size(island ? 160 : 180, 6);
        if (SongActive) return new Size(300, h);
        if (S.Face == RestingFace.Nothing) return new Size(island ? 120 : 180, h);
        return new Size(island ? 150 : 190, h);
    }

    double ClosedRadius(Size s) => S.Shape == NotchShape.Island ? s.Height / 2 : (HideMode ? 3 : 12);

    void ApplyRadius()
    {
        double r = Math.Max(0, Radius);
        var cr = S.Shape == NotchShape.Notch ? new CornerRadius(0, 0, r, r) : new CornerRadius(r);
        Body.CornerRadius = cr;
        Tint.CornerRadius = cr;
        SyncBackdrop();
    }

    void ApplyChrome()
    {
        bool island = S.Shape == NotchShape.Island;
        bool sliver = Sliver;
        var ears = !island && !sliver && S.NotchSurface == "Solid" ? Visibility.Visible : Visibility.Collapsed;
        EarL.Visibility = ears;
        EarR.Visibility = ears;
        ShapeRow.Margin = new Thickness(0, island && !sliver ? S.IslandTop : 0, 0, 0);
        ApplyRadius();
    }

    void AnimateBody(Size to, double radius, double ms, bool bouncy)
    {
        var dur = new Duration(TimeSpan.FromMilliseconds(ms));
        IEasingFunction ease = bouncy
            ? new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        Body.BeginAnimation(WidthProperty, new DoubleAnimation(to.Width, dur) { EasingFunction = ease });
        Body.BeginAnimation(HeightProperty, new DoubleAnimation(to.Height, dur) { EasingFunction = ease });
        BeginAnimation(RadiusProperty, new DoubleAnimation(radius, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void SnapBody(Size to, double radius)
    {
        Body.BeginAnimation(WidthProperty, null);
        Body.BeginAnimation(HeightProperty, null);
        BeginAnimation(RadiusProperty, null);
        Body.Width = to.Width;
        Body.Height = to.Height;
        Radius = radius;
    }

    static void Fade(UIElement el, double to, double ms, double delayMs = 0)
    {
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        });
        el.IsHitTestVisible = to > 0.5;
    }

    void Open(RadioButton? tab = null)
    {
        if (fullscreenHidden) return;
        peekTimer.Stop();
        hoverTimer.Stop();
        peekPending = false;
        if (tab != null && tab.Parent != null) tab.IsChecked = true;
        else if (mode != Mode.Open && (!S.ReopenLastTab || !AllTabs.Any(t => t.IsChecked == true && t.Parent != null))) FirstTab.IsChecked = true;

        mode = Mode.Open;
        // Opening always lifts the notch above everything in the top layer (raised widgets included)
        if (hwnd != IntPtr.Zero) Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        DeviceBattery.RefreshIfStale(); // controller just plugged in or switched on? show it now
        Body.BeginAnimation(OpacityProperty, null);
        Body.Opacity = 1;
        ApplySurface();
        ApplyChrome();
        AnimateBody(new Size(OpenW, OpenH), S.Shape == NotchShape.Island ? 30 : 26, SpeedMs, true);
        Fade(ClosedView, 0, 90);
        Fade(PeekView, 0, 90);
        Fade(ActivityView, 0, 90);
        activityTimer.Stop();
        Fade(OpenView, 1, 220, SpeedMs * 0.3);
        Tab_Checked(this, new RoutedEventArgs());
        RefreshOpen();
        UpdateTint();
        UpdateBarsTimer();
    }

    void Collapse(bool animate = true)
    {
        StopTyping();
        mode = Mode.Closed;
        leaveTimer.Stop();
        peekTimer.Stop();
        ApplySurface();
        var size = ClosedSize();
        lastClosed = size;
        UpdateClosedContent(resize: false);

        if (animate) AnimateBody(size, ClosedRadius(size), SpeedMs * 0.85, false);
        else SnapBody(size, ClosedRadius(size));

        Fade(OpenView, 0, animate ? 110 : 0);
        Fade(PeekView, 0, animate ? 110 : 0);
        Fade(ActivityView, 0, animate ? 110 : 0);
        Fade(ClosedView, HideMode ? 0 : 1, animate ? 200 : 0, animate ? SpeedMs * 0.4 : 0);

        if (HideMode)
        {
            // Fade to an invisible (but still hoverable) sliver once the shrink is done
            // Start fading straight away so the black fill never shows while it shrinks
            Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0.02, TimeSpan.FromMilliseconds(animate ? SpeedMs * 0.7 : 0)));
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(animate ? SpeedMs : 1) };
            sliverSettled = false;
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (mode != Mode.Closed) return;
                sliverSettled = true;
                ApplySurface();
                ApplyChrome();
            };
            t.Start();
        }
        else
        {
            Body.BeginAnimation(OpacityProperty, null);
            Body.Opacity = 1;
            ApplyChrome();
        }
        UpdateTint();
        UpdateBarsTimer();
        if (animate)
        {
            // Drop the on-demand controls panel and give memory back once the card is shut
            CtrlPanel.Children.Clear();
            GamesPanel.Children.Clear();
            TimerPanel.Children.Clear();
            MsgPanel.Children.Clear();
            ((App)Application.Current).ScheduleTrim();
        }
    }

    bool peekPending;

    void Peek()
    {
        if (fullscreenHidden || mode != Mode.Closed || HideMode) return;
        // Players often announce a new track a moment before its title: wait for it rather than pop up empty
        if (string.IsNullOrWhiteSpace(M.Title)) { peekPending = true; return; }
        peekPending = false;
        mode = Mode.Peek;
        ApplySurface();
        FillPeek();
        ApplyChrome();
        AnimateBody(new Size(380, 66), S.Shape == NotchShape.Island ? 24 : 20, SpeedMs, true);
        Fade(ClosedView, 0, 90);
        Fade(ActivityView, 0, 90);
        Fade(PeekView, 1, 200, SpeedMs * 0.25);
        UpdateTint();
        UpdateBarsTimer();
        peekTimer.Stop();
        peekTimer.Start();
    }

    void FillPeek()
    {
        PeekArt.Background = ArtFill();
        PeekTitle.Text = M.Title;
        PeekArtist.Text = string.IsNullOrEmpty(M.Artist) ? M.Source : M.Artist;
    }

    // ------------------------------------------------------------------ content

    void OnSecond()
    {
        UpdateClosedContent(resize: true);
        if (mode == Mode.Open) RefreshOpen();
    }

    void OnMedia()
    {
        UpdateClosedContent(resize: true);
        if (mode == Mode.Open) UpdateMusic();
        if (mode == Mode.Peek) FillPeek();              // the title or cover arrived after the pop-up opened
        else if (peekPending && S.SongPopup && mode == Mode.Closed && !string.IsNullOrWhiteSpace(M.Title)) Peek();
        UpdateTint();
        UpdateBarsTimer();
    }

    void OnTrack()
    {
        if (S.SongPopup && mode == Mode.Closed) Peek();
        else if (mode == Mode.Peek) FillPeek();
    }

    // ------------------------------------------------------------------ live activities

    readonly DispatcherTimer activityTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromMilliseconds(400) };
    float lastVolume = -1;
    bool lastMuted, lastCharging, lastMic, watchPrimed;
    string? lastOutput;
    int watchTicks;

    /// <summary>A new message: who wrote and from where, for a few seconds. Says what happened (for the log).</summary>
    public string ShowMessage(string who, string where, ImageSource? icon)
    {
        foreach (var m in MirrorsOf()) m.ShowMessage(who, where, icon);
        if (fullscreenHidden) return "not shown: something is fullscreen";
        if (mode == Mode.Open)
        {
            Note($"{who} · {where}"); // already open: say it in the header instead
            return "shown in the open notch's header";
        }
        ActivityCore("", who, where, null, 5);
        return IsVisible ? $"shown (notch at {Left:0},{Top:0}, mode {mode})" : "not shown: notch window hidden";
    }

    Action? activityClick;

    /// <summary>For `--mirror-test`.</summary>
    internal string DebugState => $"mode {mode}, clickable {activityClick != null}, watching the system {watch.IsEnabled}, title '{ActTitle.Text}'";

    /// <summary>
    /// A pop-up that does something when clicked (open a stream, install an update). Says what happened (for the log).
    /// </summary>
    public string ShowAction(string glyph, string title, string value, Action onClick, double seconds = 7)
    {
        foreach (var m in MirrorsOf()) m.ShowAction(glyph, title, value, onClick, seconds);
        if (fullscreenHidden) return "not shown: something is fullscreen";
        if (mode == Mode.Open) { Note($"{title} · {value}"); return "shown in the open notch's header"; }
        ActivityCore(glyph, title, value, null, seconds);
        activityClick = onClick;
        return "shown";
    }

    /// <summary>Pop the notch out briefly with a glyph, a title and an optional level bar.</summary>
    public void ShowActivity(string glyph, string title, string value = "", double? level = null, double seconds = 1.8, Brush? glyphBrush = null, bool symbolFont = false)
    {
        foreach (var m in MirrorsOf()) m.ActivityCore(glyph, title, value, level, seconds, glyphBrush, symbolFont);
        ActivityCore(glyph, title, value, level, seconds, glyphBrush, symbolFont);
    }

    /// <summary>Draw the pop-up on this notch only.</summary>
    void ActivityCore(string glyph, string title, string value = "", double? level = null, double seconds = 1.8, Brush? glyphBrush = null, bool symbolFont = false)
    {
        if (fullscreenHidden || mode == Mode.Open) return;
        activityClick = null; // a new pop-up replaces what a clickable one would have done
        ActGlyph.Text = glyph;
        ActGlyph.FontFamily = (FontFamily)FindResource(symbolFont ? "Brim.Symbols" : "Brim.Icons");
        if (glyphBrush != null) ActGlyph.Foreground = glyphBrush;
        else ActGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Text");
        ActTitle.Text = title;
        ActValue.Text = value;
        ActLevel.Visibility = level == null ? Visibility.Collapsed : Visibility.Visible;
        if (level != null) ActLevel.Value = level.Value;
        activityTimer.Stop();
        activityTimer.Interval = TimeSpan.FromSeconds(seconds);
        activityTimer.Start();
        if (mode == Mode.Activity) return; // just update what's showing
        peekTimer.Stop();
        mode = Mode.Activity;
        Body.BeginAnimation(OpacityProperty, null);
        Body.Opacity = 1;
        ApplySurface();
        ApplyChrome();
        AnimateBody(new Size(330, 46), S.Shape == NotchShape.Island ? 23 : 18, SpeedMs * 0.8, true);
        Fade(ClosedView, 0, 80);
        Fade(PeekView, 0, 80);
        Fade(ActivityView, 1, 160, SpeedMs * 0.25);
        UpdateTint();
        UpdateBarsTimer();
    }

    /// <summary>Watches volume, charger, audio output and microphone, and shows a live activity when one changes.</summary>
    void WatchSystem()
    {
        if (!S.LiveActivities) { watchPrimed = false; return; }
        float vol = Audio.Volume;
        bool muted = Audio.Muted;
        bool charging = SystemInfo.Charging;
        bool mic = Audio.HasMic && Audio.MicMuted;
        string? output = watchTicks++ % 10 == 0 ? Audio.DefaultOutputName() : lastOutput;
        if (watchPrimed)
        {
            // Only muting and unmuting pop the notch out (changing the volume level doesn't)
            if (muted != lastMuted)
                ShowActivity(muted || vol < 0.005 ? "" : "", muted ? "Muted" : "Unmuted", muted ? "" : $"{Math.Round(vol * 100)}", muted ? 0 : vol * 100);
            else if (SystemInfo.HasBattery && charging != lastCharging)
                ShowActivity(charging ? "⚡" : "▯", charging ? "Charging" : "On battery", $"{SystemInfo.BatteryPercent}%", SystemInfo.BatteryPercent, 2.4, null, true);
            else if (output != null && lastOutput != null && output != lastOutput)
                ShowActivity("", output, "", null, 2.4);
            else if (mic != lastMic)
                ShowMicActivity(mic);
        }
        lastVolume = vol; lastMuted = muted; lastCharging = charging; lastMic = mic;
        if (output != null) lastOutput = output;
        watchPrimed = true;
    }

    public void ShowMicActivity(bool muted)
    {
        foreach (var m in MirrorsOf()) m.ShowMicActivity(muted);
        lastMic = muted;
        ActivityCore(muted ? "" : "", muted ? "Microphone muted" : "Microphone on", "", null, 2,
            muted ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)) : null);
    }

    string FaceString()
    {
        var now = DateTime.Now;
        if (FocusTimer.Running) return "⏱ " + FocusTimer.Format(FocusTimer.Left);
        switch (S.Face)
        {
            case RestingFace.Date: return now.ToString("ddd d MMM");
            case RestingFace.Battery:
                if (SystemInfo.HasBattery) return $"{SystemInfo.BatteryPercent}%{(SystemInfo.Charging ? " ⚡" : "")}";
                return now.ToString(S.Clock24 ? "HH:mm" : "h:mm");
            case RestingFace.Weather:
                return WeatherService.Current is { } w ? $"{w.Glyph} {Math.Round(w.Temp)}°" : now.ToString(S.Clock24 ? "HH:mm" : "h:mm");
            case RestingFace.Cpu: return $"CPU {SystemInfo.Cpu:0}%";
            case RestingFace.Nothing: return "";
            default: return now.ToString(S.Clock24 ? "HH:mm" : "h:mm");
        }
    }

    /// <summary>What the closed notch says while music plays: the current lyric when lyrics are on, else the title.</summary>
    string SongFace()
    {
        return M.Title;
    }

    void UpdateClosedContent(bool resize)
    {
        bool song = SongActive;
        MiniArt.Visibility = song && M.Art != null ? Visibility.Visible : Visibility.Collapsed;
        MiniArt.Background = ArtFill();
        MiniBars.Visibility = song ? Visibility.Visible : Visibility.Collapsed;
        FaceText.Text = song && !FocusTimer.Running ? SongFace() : FaceString();
        FaceText.FontSize = song ? 12 : 13;
        FaceText.FontWeight = song ? FontWeights.Normal : FontWeights.SemiBold;

        var barBrush = BarBrush();
        foreach (var child in MiniBars.Children)
            if (child is Rectangle r) r.Fill = barBrush;

        if (resize && mode == Mode.Closed)
        {
            var size = ClosedSize();
            if (size != lastClosed)
            {
                lastClosed = size;
                AnimateBody(size, ClosedRadius(size), SpeedMs, true);
            }
        }
    }

    ImageSource? lastArt;
    Brush? lastArtBrush;

    Brush? ArtFill()
    {
        if (!ReferenceEquals(M.Art, lastArt))
        {
            lastArt = M.Art;
            if (M.Art == null) lastArtBrush = null;
            else
            {
                var b = new ImageBrush(M.Art) { Stretch = Stretch.UniformToFill };
                b.Freeze();
                lastArtBrush = b;
            }
        }
        return lastArtBrush;
    }

    Brush BarBrush()
    {
        if (S.AlbumColors && M.ArtColor is Color c)
        {
            var l = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.45), (byte)(c.G + (255 - c.G) * 0.45), (byte)(c.B + (255 - c.B) * 0.45));
            var b = new SolidColorBrush(l);
            b.Freeze();
            return b;
        }
        return (Brush)FindResource("Brim.Accent");
    }

    void UpdateTint()
    {
        double target = 0;
        if (S.AlbumColors && M.ArtColor is Color c && M.HasSession)
        {
            var brush = new LinearGradientBrush(
                Color.FromArgb(150, c.R, c.G, c.B), Color.FromArgb(25, c.R, c.G, c.B), 90);
            brush.Freeze();
            Tint.Background = brush;
            target = mode switch
            {
                Mode.Open => TabHome.IsChecked == true ? 1 : 0.35,
                Mode.Peek => 1,
                _ => SongActive && !HideMode ? 0.6 : 0,
            };
        }
        Tint.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(300)));
    }

    void UpdateBarsTimer()
    {
        bool want = mode == Mode.Closed && SongActive && !HideMode && IsVisible;
        if (want && !barsTimer.IsEnabled) barsTimer.Start();
        else if (!want && barsTimer.IsEnabled) barsTimer.Stop();
    }

    void AnimateBars()
    {
        double peak = M.IsPlaying ? Math.Min(1, Audio.Peak * 1.7) : 0;
        var bars = new[] { Bar0, Bar1, Bar2, Bar3 };
        for (int i = 0; i < 4; i++)
        {
            double target = 3 + peak * 13 * (0.4 + rnd.NextDouble() * 0.6);
            barLevels[i] += (target - barLevels[i]) * 0.45;
            bars[i].Height = Math.Max(3, barLevels[i]);
        }
        // Keep the lyric line in step with the song (a few times a second)
    }

    int lyricTick;

    void RefreshOpen()
    {
        UpdateHeader();
        UpdateMusic();
        var now = DateTime.Now;
        DayText.Text = now.ToString("dddd");
        DateText.Text = now.ToString("d MMM");
        CpuText.Text = $"{SystemInfo.Cpu:0}%";
        CpuBar.Value = SystemInfo.Cpu;
        RamText.Text = $"{SystemInfo.RamUsedGb:0.0} / {SystemInfo.RamTotalGb:0} GB";
        RamBar.Value = SystemInfo.Ram;
        if (WeatherBlock.Parent != null)
        {
            var w = WeatherService.Current;
            WeatherGlyph.Text = w?.Glyph ?? "";
            WeatherText.Text = w != null ? $"{Math.Round(w.Temp)}°" : "No weather yet";
            WeatherPlace.Text = w != null ? S.WeatherCity : "Set a city in Settings › Widgets";
        }
        if (BatteryBlock.Parent != null)
        {
            BatteryText.Text = $"{SystemInfo.BatteryPercent}%{(SystemInfo.Charging ? " ⚡" : "")}";
            BatteryBar.Value = SystemInfo.BatteryPercent;
        }
        if (MessageBlock.Parent != null)
        {
            var m = MessageCenter.Visible.FirstOrDefault();
            MsgFrom.Text = m != null ? $"{m.Sender}" : "No new messages";
            MsgLine.Text = m != null ? $"{m.App} · {m.Ago}{(m.Text.Length > 0 ? " · " + m.Text : "")}" : "Teams, Discord, Outlook…";
        }
        settingVolume = true;
        VolumeSlider.Value = Audio.Volume;
        settingVolume = false;
        MuteBtn.Content = Audio.Muted ? "" : "";
    }

    bool tabHint;

    void UpdateHeader()
    {
        if (noteTimer.IsEnabled || tabHint) return;
        var t = DateTime.Now.ToString(S.Clock24 ? "HH:mm" : "h:mm tt");
        HeaderInfo.Text = SystemInfo.HasBattery ? $"{SystemInfo.BatteryPercent}%{(SystemInfo.Charging ? " ⚡" : "")}   {t}" : t;
    }

    void Note(string text)
    {
        HeaderInfo.Text = text;
        noteTimer.Stop();
        noteTimer.Start();
    }

    /// <summary>Small "controller 82%" chips next to the day on Home (up to two devices; hover for details).</summary>
    void UpdateDeviceChips()
    {
        DeviceChips.Children.Clear();
        var devices = DeviceBattery.Devices
            .OrderByDescending(d => d.Glyph == DeviceBattery.ControllerGlyph) // controllers first
            .ThenBy(d => d.Name).Take(2).ToList();
        foreach (var d in devices)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Transparent };
            var g = new TextBlock { Text = d.Glyph, FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            g.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Text");
            chip.Children.Add(g);
            if (d.Battery >= 0)
            {
                var pct = new TextBlock { Text = $" {d.Battery}%", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
                if (d.Battery <= 15 && !d.Charging) pct.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
                else pct.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Text");
                chip.Children.Add(pct);
                if (d.Charging)
                {
                    var bolt = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
                    bolt.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Accent");
                    chip.Children.Add(bolt);
                }
            }
            else
            {
                // Connected, but it doesn't say how full it is: a small dot in the accent colour
                var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Border.BackgroundProperty, "Brim.Accent");
                chip.Children.Add(dot);
            }
            chip.ToolTip = $"{d.Name}: {d.BatteryText}";
            DeviceChips.Children.Add(chip);
        }
    }

    string recentShown = "";

    /// <summary>With nothing playing, the music area offers the games you played last.</summary>
    void UpdateRecentGames(bool show)
    {
        var recent = show
            ? GameLibrary.Current.Where(g => !g.Hidden && g.LastPlayed != default).OrderByDescending(g => g.LastPlayed).Take(4).ToList()
            : new List<GameInfo>();
        RecentGames.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var sig = string.Join("|", recent.Select(g => g.Key));
        if (sig == recentShown) return;
        recentShown = sig;
        RecentGamesList.Children.Clear();
        foreach (var g in recent)
        {
            var game = g;
            var iconPath = g.IconExe ?? g.Exe;
            var icon = iconPath != null && System.IO.File.Exists(iconPath) ? IconHelper.ForPath(iconPath) : null;
            var b = new Button { Width = 36, Height = 36, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(0) };
            b.SetResourceReference(StyleProperty, "IconButton");
            b.Content = icon != null
                ? new Image { Source = icon, Width = 24, Height = 24 }
                : new TextBlock { Text = g.Name.Length > 0 ? g.Name[..1].ToUpperInvariant() : "?", FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("Brim.Font") };
            var played = PlayTime.Format(g);
            b.ToolTip = $"Play {g.Name}" + (played.Length > 0 ? $"  ·  {played} played" : "") + $"  ·  {PlayTime.Ago(g.LastPlayed)}";
            b.Click += (_, _) => { GameLibrary.Launch(game); Collapse(); };
            RecentGamesList.Children.Add(b);
        }
    }

    void UpdateMusic()
    {
        bool has = M.HasSession && !string.IsNullOrEmpty(M.Title);
        SongTitle.Text = has ? M.Title : "Nothing playing";
        SongArtist.Text = has ? M.Artist : "Play something in Spotify, a browser or any player";
        SongSource.Text = has ? M.Source : "";
        ArtImage.Background = ArtFill();
        ArtPlaceholder.Visibility = M.Art == null ? Visibility.Visible : Visibility.Collapsed;
        PlayBtn.Content = M.IsPlaying ? "" : "";
        ProgressRow.Visibility = has && M.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Hidden;
        UpdateRecentGames(!has);
        Progress.IsEnabled = M.CanSeek;
        if (!seeking)
        {
            var pos = M.Position;
            Progress.Maximum = Math.Max(1, M.Duration.TotalSeconds);
            Progress.Value = pos.TotalSeconds;
            Elapsed.Text = MediaService.Fmt(pos);
            Remaining.Text = "-" + MediaService.Fmt(M.Duration - pos);
        }
    }

    // ------------------------------------------------------------------ tabs

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePanel == null || AppsPanel == null || ShelfPanel == null || ClipPanel == null || CtrlPanel == null || GamesPanel == null || TimerPanel == null
            || MsgPanel == null || TabMsgs == null || NotesPanel == null || TabNotes == null) return;
        bool notes = TabNotes.IsChecked == true;
        NotesPanel.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        if (notes && NotesPanel.Children.Count == 0) NotesPanel.Children.Add(new NotesView(EnableTyping));
        bool msgs = TabMsgs.IsChecked == true;
        MsgPanel.Visibility = msgs ? Visibility.Visible : Visibility.Collapsed;
        if (msgs && MsgPanel.Children.Count == 0) MsgPanel.Children.Add(new MessagesView(ShowMenu, () => Collapse()));
        HomePanel.Visibility = TabHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AppsPanel.Visibility = TabApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShelfPanel.Visibility = TabShelf.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ClipPanel.Visibility = TabClip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        bool ctrl = TabCtrl.IsChecked == true;
        CtrlPanel.Visibility = ctrl ? Visibility.Visible : Visibility.Collapsed;
        if (ctrl && CtrlPanel.Children.Count == 0) CtrlPanel.Children.Add(new QuickPanel(compact: true));
        bool timer = TabTimer.IsChecked == true;
        TimerPanel.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
        if (timer && TimerPanel.Children.Count == 0) TimerPanel.Children.Add(new FocusTimerView());
        bool games = TabGames.IsChecked == true;
        GamesPanel.Visibility = games ? Visibility.Visible : Visibility.Collapsed;
        if (games && GamesPanel.Children.Count == 0) GamesPanel.Children.Add(new GamesStrip(() => Collapse(), item =>
        {
            if (S.Shortcuts.Count >= 12) return;
            S.Shortcuts.Add(item);
            SettingsStore.Save();
            RebuildShortcuts();
        }, () => holdOpen++, () => { holdOpen--; if (!Body.IsMouseOver) leaveTimer.Start(); }));
        UpdateTint();
    }

    void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        ((App)Application.Current).TakeScreenshot();
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        ((App)Application.Current).OpenSettings();
    }

    // ------------------------------------------------------------------ music

    void Play_Click(object sender, RoutedEventArgs e) => M.PlayPause();

    void Next_Click(object sender, RoutedEventArgs e) => M.Next();
    void Prev_Click(object sender, RoutedEventArgs e) => M.Previous();
    void Progress_Down(object sender, MouseButtonEventArgs e) => seeking = true;

    void Progress_Up(object sender, MouseButtonEventArgs e)
    {
        if (!seeking) return;
        seeking = false;
        M.Seek(TimeSpan.FromSeconds(Progress.Value));
    }

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (settingVolume) return;
        Audio.Volume = (float)e.NewValue;
        if (Audio.Muted) { Audio.Muted = false; MuteBtn.Content = ""; }
    }

    void Mute_Click(object sender, RoutedEventArgs e)
    {
        Audio.Muted = !Audio.Muted;
        MuteBtn.Content = Audio.Muted ? "" : "";
    }

    // ------------------------------------------------------------------ shortcuts

    public void RebuildShortcuts()
    {
        shortcuts.Clear();
        int i = 0;
        foreach (var s in S.Shortcuts)
        {
            i++;
            bool url = IconHelper.IsUrl(s.Path);
            var icon = url ? Favicons.Cached(s.Path) : IconHelper.ForPath(s.Path);
            var vm = new ShortcutVM
            {
                Item = s,
                Name = s.Name,
                Icon = icon,
                Glyph = icon == null ? (url ? "" : "") : null,
                Badge = i <= 9 ? i.ToString() : null,
                Tip = s.Path + (i <= 9 ? $"   ·   Ctrl+Alt+{i}" : ""),
            };
            shortcuts.Add(vm);
            // A website's own logo, fetched once and kept (the globe shows until it arrives)
            if (url && icon == null)
                _ = Favicons.GetAsync(s.Path).ContinueWith(t =>
                {
                    if (t.Result is { } logo) { vm.Icon = logo; vm.Glyph = null; }
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }
        if (S.Shortcuts.Count < 12)
            shortcuts.Add(new ShortcutVM { Name = "Add", Glyph = "", Tip = "Add an app, file or folder (or drop one here)" });
    }

    public void AddShortcutItems(IEnumerable<ShortcutItem> items)
    {
        bool any = false;
        foreach (var item in items)
        {
            if (S.Shortcuts.Count >= 12) break;
            S.Shortcuts.Add(item);
            any = true;
        }
        if (!any) return;
        SettingsStore.Save();
        RebuildShortcuts();
        ((App)Application.Current).RefreshSettingsWindow();
    }

    public void AddShortcuts(string[] paths)
    {
        foreach (var p in paths)
        {
            if (S.Shortcuts.Count >= 12) break;
            S.Shortcuts.Add(new ShortcutItem { Name = IconHelper.NiceName(p), Path = p });
        }
        SettingsStore.Save();
        RebuildShortcuts();
        ((App)Application.Current).RefreshSettingsWindow();
    }

    void AppTile_Click(object sender, RoutedEventArgs e)
    {
        if (swallowTileClick) { swallowTileClick = false; return; }
        if (((FrameworkElement)sender).Tag is not ShortcutVM vm) return;
        if (vm.IsAdd)
        {
            holdOpen++;
            try
            {
                var picked = AppPicker.Pick(null, "Add shortcuts", "Add", S.Shortcuts.Select(s => s.Path), 12 - S.Shortcuts.Count);
                AddShortcutItems(picked);
            }
            finally { holdOpen--; }
            if (!Body.IsMouseOver) leaveTimer.Start();
            return;
        }
        Launcher.Open(vm.Item!.Path, vm.Item.Args);
        Collapse();
    }

    // ---------- drag shortcuts to put them in your own order (like the Games tab and the dock)

    ShortcutVM? tilePressed;
    Point tilePressAt;
    bool tileDragging, swallowTileClick;

    void AppTile_Press(object sender, MouseButtonEventArgs e)
    {
        tilePressed = ((FrameworkElement)sender).Tag as ShortcutVM;
        if (tilePressed?.IsAdd == true) tilePressed = null; // the "Add" tile stays last
        tilePressAt = e.GetPosition(AppsList);
    }

    void AppTile_DragMove(object sender, MouseEventArgs e)
    {
        if (tilePressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(AppsList);
        if (!tileDragging)
        {
            if ((at - tilePressAt).Length < 8) return;
            tileDragging = true;
            holdOpen++; // keep the notch open while you drag
            tilePressed.Dragging = true;
        }
        // Which tile is the pointer over? (tiles wrap onto several rows)
        int from = shortcuts.IndexOf(tilePressed), to = from;
        for (int i = 0; i < shortcuts.Count; i++)
        {
            if (shortcuts[i].IsAdd) continue;
            if (AppsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var r = new Rect(c.TranslatePoint(new Point(0, 0), AppsList), c.RenderSize);
            if (r.Contains(at)) { to = i; break; }
        }
        if (from >= 0 && to != from) shortcuts.Move(from, to);
    }

    void AppTile_DragEnd(object sender, MouseButtonEventArgs e)
    {
        var moved = tilePressed;
        tilePressed = null;
        if (!tileDragging) return;
        tileDragging = false;
        holdOpen = Math.Max(0, holdOpen - 1);
        if (moved != null) moved.Dragging = false;
        // Save the new order (and the Ctrl+Alt+number keys follow it)
        var order = shortcuts.Where(s => s.Item != null).Select(s => s.Item!).ToList();
        S.Shortcuts.Clear();
        S.Shortcuts.AddRange(order);
        SettingsStore.Save();
        swallowTileClick = true;
        Dispatcher.BeginInvoke(new Action(() => swallowTileClick = false), DispatcherPriority.Input);
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
        RebuildShortcuts(); // renumber the badges
        ((App)Application.Current).RefreshSettingsWindow();
        if (!Body.IsMouseOver) leaveTimer.Start();
    }

    void AppTile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ShortcutVM { Item: { } item }) return;
        e.Handled = true;
        var menu = new ContextMenu();
        if (!IconHelper.IsUrl(item.Path))
            menu.Items.Add(MakeItem("Open file location", () => Launcher.ShowInFolder(item.Path)));
        int index = S.Shortcuts.IndexOf(item);
        if (index > 0)
            menu.Items.Add(MakeItem("Move left", () =>
            {
                S.Shortcuts.RemoveAt(index);
                S.Shortcuts.Insert(index - 1, item);
                SettingsStore.Save();
                RebuildShortcuts();
            }));
        menu.Items.Add(MakeItem("Remove", () =>
        {
            S.Shortcuts.Remove(item);
            SettingsStore.Save();
            RebuildShortcuts();
            ((App)Application.Current).RefreshSettingsWindow();
        }));
        ShowMenu(menu, (UIElement)sender);
    }

    static MenuItem MakeItem(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }

    void ShowMenu(ContextMenu menu, UIElement target)
    {
        holdOpen++;
        menu.Closed += (_, _) =>
        {
            holdOpen--;
            if (!Body.IsMouseOver) leaveTimer.Start();
        };
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ shelf

    void UpdateShelfState()
    {
        bool any = Shelf.Items.Count > 0;
        ShelfEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ShelfFull.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        UpdateShelfBar();
    }

    List<FileItem> SelectedFiles() => Shelf.Items.Where(i => i.Selected).ToList();

    void UpdateShelfBar()
    {
        int n = SelectedFiles().Count;
        ShelfInfo.Text = n == 0 ? "Click files to select · drag to copy them anywhere" : $"{n} selected · drag any of them to take all {n}";
        ShelfCopyBtn.IsEnabled = ShelfRemoveBtn.IsEnabled = n > 0;
        ShelfCopyBtn.Opacity = ShelfRemoveBtn.Opacity = n > 0 ? 1 : 0.4;
        ShelfAllBtn.Content = n == Shelf.Items.Count && n > 0 ? "Select none" : "Select all";
    }

    bool shelfDragged;

    /// <summary>A click (not a drag) ticks or unticks a file.</summary>
    void ShelfItem_Click(object sender, RoutedEventArgs e)
    {
        if (shelfDragged) { shelfDragged = false; return; }
        if (((FrameworkElement)sender).Tag is not FileItem item) return;
        item.Selected = !item.Selected;
        UpdateShelfBar();
    }

    void ShelfAll_Click(object sender, RoutedEventArgs e)
    {
        bool all = Shelf.Items.Count > 0 && Shelf.Items.All(i => i.Selected);
        foreach (var i in Shelf.Items) i.Selected = !all;
        UpdateShelfBar();
    }

    void ShelfCopy_Click(object sender, RoutedEventArgs e)
    {
        var files = SelectedFiles();
        if (files.Count == 0) return;
        try
        {
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(files.Select(f => f.Path).ToArray());
            Clipboard.SetFileDropList(list);
            Note(files.Count == 1 ? "Copied · paste anywhere" : $"Copied {files.Count} files · paste anywhere");
        }
        catch (Exception ex) { Log.Error("shelf copy", ex); }
    }

    void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in SelectedFiles()) Shelf.Items.Remove(f);
        UpdateShelfBar();
    }

    void Body_DragEnter(object sender, DragEventArgs e)
    {
        if (draggingOut || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        dragOver = true;
        leaveTimer.Stop();
        if (mode != Mode.Open) Open(TabShelf);
        else if (TabApps.IsChecked != true) TabShelf.IsChecked = true;
    }

    void Body_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(Body);
        if (p.X <= 0 || p.Y <= 0 || p.X >= Body.ActualWidth || p.Y >= Body.ActualHeight)
        {
            dragOver = false;
            leaveTimer.Start();
        }
    }

    void Body_Drop(object sender, DragEventArgs e)
    {
        dragOver = false;
        e.Handled = true;
        if (draggingOut) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        if (TabApps.IsChecked == true) AddShortcuts(files);
        else
        {
            Shelf.Add(files);
            TabShelf.IsChecked = true;
        }
    }

    void ShelfItem_Down(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(this); shelfDragged = false; }

    void ShelfItem_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || draggingOut) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - dragStart.X) < 6 && Math.Abs(p.Y - dragStart.Y) < 6) return;
        if (((FrameworkElement)sender).Tag is not FileItem item) return;

        draggingOut = true;
        shelfDragged = true;
        holdOpen++;
        try
        {
            // Dragging a selected file takes every selected file; the files stay on the shelf afterwards
            var files = item.Selected ? SelectedFiles().Select(f => f.Path).ToArray() : new[] { item.Path };
            var data = new DataObject(DataFormats.FileDrop, files);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex) { Log.Error("shelf drag", ex); }
        finally
        {
            draggingOut = false;
            holdOpen--;
        }
        if (!Body.IsMouseOver) leaveTimer.Start();
    }

    void ShelfItem_Open(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is FileItem item) Launcher.Open(item.Path);
    }

    void ShelfItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not FileItem item) return;
        e.Handled = true;
        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("Open", () => Launcher.Open(item.Path)));
        menu.Items.Add(MakeItem("Show in folder", () => Launcher.ShowInFolder(item.Path)));
        menu.Items.Add(MakeItem("Remove from shelf", () => Shelf.Items.Remove(item)));
        ShowMenu(menu, (UIElement)sender);
    }

    void ShelfClear_Click(object sender, RoutedEventArgs e) => Shelf.Items.Clear();

    // ------------------------------------------------------------------ clipboard

    void Clip_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ClipItem item) return;
        ClipboardHistory.CopyBack(item);
        Note("Copied ✓");
    }

    void Clip_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).Tag is not ClipItem item) return;
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(MakeItem(item.Pinned ? "Unpin" : "Pin to the top", () => ClipboardHistory.TogglePin(item)));
        if (item.Png == null && item.Files == null)
            menu.Items.Add(MakeItem("Copy as plain text", () => { ClipboardHistory.CopyBack(item); Note("Copied as plain text ✓"); }));
        menu.Items.Add(MakeItem("Remove", () => ClipboardHistory.Remove(item)));
        ShowMenu(menu, (UIElement)sender);
    }

    // ---------- clipboard search (the notch normally never takes keyboard focus, so it opts in while you type)

    bool typing;

    void ClipSearch_Click(object sender, MouseButtonEventArgs e) => EnableTyping(ClipSearch);

    void EnableTyping(TextBox box)
    {
        if (!typing)
        {
            typing = true;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() & ~Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            holdOpen++;
        }
        Native.ForceForeground(hwnd);
        box.Focus();
        Keyboard.Focus(box);
    }

    void StopTyping()
    {
        if (!typing) return;
        typing = false;
        holdOpen = Math.Max(0, holdOpen - 1);
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        ClipSearch.Text = "";
    }

    void ClipSearch_Changed(object sender, TextChangedEventArgs e)
    {
        ClipSearchHint.Visibility = ClipSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var q = ClipSearch.Text.Trim();
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(ClipboardHistory.Items);
        view.Filter = q.Length == 0 ? null : o => o is ClipItem c && c.Preview.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }
}
