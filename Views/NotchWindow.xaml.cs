using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

    const double OpenW = 580, OpenH = 258;
    /// <summary>How big the open notch is for the tab showing: most fit the usual size, a few need more room.</summary>
    Size OpenSize => TabSystem?.IsChecked == true ? new Size(OpenW, 434) : TabPhone?.IsChecked == true ? new Size(OpenW, 392)
        : TabDocs?.IsChecked == true ? new Size(OpenW, 364)
        : TabMsgs?.IsChecked == true ? new Size(OpenW, 382) : new Size(OpenW, OpenH);
    PhoneView? phoneView;

    /// <summary>Open on one of your tabs (false when it's hidden, or the notch is away for a fullscreen app).</summary>
    public bool OpenTab(string id)
    {
        if (fullscreenHidden || TabFor(id) is not { Parent: not null } tab) return false;
        Open(tab);
        return true;
    }
    double OpenRadius => S.Shape == NotchShape.Island ? 30 : 26;

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

    double SpeedMs => S.Speed switch { "Quick" => 170, "Relaxed" => 400, _ => 250 };

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
        // On the cards, pills, keys and the selected tab take a lighter shade so they stand out from the card
        foreach (var onCard in new FrameworkElement[] { HeaderBar, HomePanel, AppsPanel, ShelfPanel, ClipPanel, CtrlPanel, TimerPanel, CalcPanel, PhonePanel })
            Cards.Lift(onCard);
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
        leaveTimer.Interval = TimeSpan.FromMilliseconds(90); // off it and it's closing almost at once
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
        lyricTimer.Tick += (_, _) => ShowLyric(force: false);
        Lyrics.Changed += OnLyrics;
        Loaded += (_, _) => OnLyrics(); // lyrics that arrived before this notch did (a screen plugged in mid-song)
        // A long line wraps onto a second row (it grows taller); then there's no room for the next one
        LyricNow.SizeChanged += (_, _) => LyricNext.Visibility = LyricNow.ActualHeight > 20 ? Visibility.Collapsed : Visibility.Visible;
        App.SecondTick += OnSecond;
        DeviceBattery.Changed += UpdateDeviceChips;
        Loaded += (_, _) => UpdateDeviceChips();

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, true);
            ApplyCapture();
            HwndSource.FromHwnd(hwnd).AddHook(GlanceHook);
        };
        Body.LayoutUpdated += (_, _) => SyncBackdrop();
        // Leaving the clipboard search (click elsewhere or Esc) gives the notch back its usual behaviour
        Deactivated += (_, _) => { if (typing) { StopTyping(); if (!Body.IsMouseOver) leaveTimer.Start(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && typing) { StopTyping(); Collapse(); e.Handled = true; } };
        // Clicking the Notes tab puts the cursor in the page straight away. Only on a click: a notch that merely
        // opens on hover with Notes already showing still leaves the keyboard with your game or app.
        // Drag a tab sideways to move it (it lifts and the others slide aside)
        foreach (var tab in AllTabs)
        {
            var t = tab;
            t.PreviewMouseLeftButtonDown += (_, e) => { tabPressed = t; tabPressAt = e.GetPosition(TabsRow); };
            t.PreviewMouseMove += (_, e) =>
            {
                if (tabPressed != t || e.LeftButton != MouseButtonState.Pressed || tabReorder?.Active == true) return;
                if (Math.Abs(e.GetPosition(TabsRow).X - tabPressAt.X) < 6) return;
                holdOpen++;
                tabReorder ??= new LiveReorder(TabsRow, c => c is RadioButton, null, TabsMoved, lift: 0, liftScale: 1.15);
                tabReorder.Begin(t, e.GetPosition(TabsRow));
            };
        }
        // Every tab has a right-click menu: its own quick actions (when there's something to act on), then moving or hiding the tab
        foreach (var tab in AllTabs)
        {
            var t = tab;
            t.MouseRightButtonUp += (_, e) => { e.Handled = true; TabMenu(t); };
        }
        // ...and anywhere in the tab's own area that doesn't have a menu of its own (a clip, a game, a shortcut keep theirs;
        // text you type in keeps cut / copy / paste)
        TabContent.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            for (var d = e.OriginalSource as DependencyObject; d != null && d != TabContent; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is System.Windows.Controls.Primitives.TextBoxBase) return;
            if (AllTabs.FirstOrDefault(x => x.IsChecked == true && x.Parent != null) is not { } current) return;
            e.Handled = true;
            TabMenu(current);
        };
        TabNotes.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (mode == Mode.Open && TabNotes.IsChecked == true && NotesPanel.Children.Count > 0 && NotesPanel.Children[0] is NotesView notes)
                notes.Focus(EnableTyping);
        }), System.Windows.Threading.DispatcherPriority.Input);
        // Same for the calculator: a click on its tab and you can type the sum straight away
        TabCalc.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (mode == Mode.Open && TabCalc.IsChecked == true && CalcPanel.Children.Count > 0 && CalcPanel.Children[0] is CalculatorView calc)
                calc.Focus(EnableTyping);
        }), System.Windows.Threading.DispatcherPriority.Input);

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
            (TabCalc, "Calculator · type a sum or tap the keys"),
            (TabSystem, "System · CPU, memory, network, storage"),
            (TabPhone, "Phone · swap files, photos and links"),
            (TabDocs, "Docs · Word to PDF, PDF to Word, edit PDFs"),
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
        if (S.NotchSurface == "Solid" || !Backdrop.Available) { backdrop?.Dispose(); backdrop = null; }
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
            Body.BorderThickness = new Thickness(0);
            Clearer(false);
            return;
        }
        // Your own colour for the closed notch (the open one keeps the theme's)
        Color? own = mode != Mode.Open && S.NotchColor != null ? ThemeManager.Parse(S.NotchColor) : null;
        ReadableOn(own);
        if (S.NotchSurface == "Solid")
        {
            if (own is Color oc) Body.Background = new SolidColorBrush(oc);
            else Body.SetResourceReference(Border.BackgroundProperty, "Brim.Surface");
            FaceText.Effect = null;
            Body.BorderThickness = new Thickness(0);
            Clearer(false);
            return;
        }
        if (ClearAndClosed)
        {
            // Invisible, but still there to be pointed at
            Body.Background = new SolidColorBrush(Color.FromArgb(2, 0, 0, 0));
            Body.BorderThickness = new Thickness(0);
            FaceText.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9, Color = Colors.Black };
            Clearer(false);
        }
        else
        {
            var c = own ?? ThemeManager.Parse(ThemeManager.Current.Surface);
            bool crystal = Crystal;
            byte alpha = crystal ? (own != null ? (byte)0x48 : (byte)0x12) : (own != null ? (byte)0x80 : (byte)0x30);
            Body.Background = Backdrop.Glass(c, alpha, litFromBelow: true);
            Body.BorderThickness = new Thickness(0);
            FaceText.Effect = null;
            Clearer(crystal);
        }
        SyncBackdrop();
    }

    bool Crystal => S.NotchSurface is "Crystal" or "Clear"; // (Clear opens as clear glass too)

    /// <summary>
    /// Crystal: the glass is nearly clear, so a frosted rim covers the thin strip along the sides and bottom that the
    /// blur doesn't reach (the top is the screen edge), and text and icons get a soft shadow to stay readable.
    /// </summary>
    void Clearer(bool on)
    {
        clearer = on;
        FitEdge();
        // (open, everything sits on cards; closed, the clock and song sit on the glass itself)
        ClosedView.Effect = on ? ClosedView.Effect ?? Backdrop.TextLift() : null;
    }

    bool clearer;

    /// <summary>The frosted rim's width follows the corners (rounder corners leave a wider strip the blur misses).</summary>
    void FitEdge()
    {
        double band = 0;
        if (clearer && PresentationSource.FromVisual(Body)?.CompositionTarget is { } ct)
            band = Backdrop.EdgeBand(Math.Max(0, Radius), ct.TransformToDevice.M11);
        bool island = S.Shape == NotchShape.Island;
        Edge.BorderThickness = band <= 0 ? new Thickness(0) : island ? new Thickness(band) : new Thickness(band, 0, band, band);
        Edge.BorderBrush = band > 0 ? Backdrop.CrystalEdge(litFromBelow: !island) : null;
    }

    /// <summary>Dark text on a light notch colour, so the song, clock and pop-ups stay readable.</summary>
    void ReadableOn(Color? own)
    {
        bool light = own is Color c && ThemeManager.IsLight(c);
        foreach (var view in new FrameworkElement[] { ClosedView, PeekView, ActivityView })
        {
            if (light)
            {
                view.Resources["Brim.Text"] = Brushes.Black;
                view.Resources["Brim.Dim"] = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4C));
                view.SetValue(TextElement.ForegroundProperty, Brushes.Black); // text that just inherits the window's colour
            }
            else { view.Resources.Remove("Brim.Text"); view.Resources.Remove("Brim.Dim"); view.ClearValue(TextElement.ForegroundProperty); }
        }
        // The little curves where the notch meets the screen edge take the same colour
        foreach (var ear in new[] { EarL, EarR })
        {
            if (own is Color oc) ear.Fill = new SolidColorBrush(oc);
            else ear.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brim.Surface");
        }
    }

    void SyncBackdrop()
    {
        if (backdrop == null) return;
        if (bodyAnimating) { backdrop.Hide(); return; } // (comes back once the notch has settled)
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
        if (hwnd != IntPtr.Zero && IsVisible) Log.Trace("notch: back on top");
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

    internal void DebugClose() => Collapse();
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
        "Calc" => TabCalc,
        _ => TabFor(tab) ?? TabHome,
    });

    RadioButton? TabFor(string id) => id switch
    {
        "Home" => TabHome, "Apps" => TabApps, "Games" => TabGames, "Messages" => TabMsgs, "Shelf" => TabShelf,
        "Clip" => TabClip, "Ctrl" => TabCtrl, "Timer" => TabTimer, "Notes" => TabNotes, "Calc" => TabCalc, "System" => TabSystem, "Phone" => TabPhone, "Docs" => TabDocs, _ => null,
    };

    IEnumerable<RadioButton> AllTabs => new[] { TabHome, TabApps, TabGames, TabMsgs, TabNotes, TabCalc, TabShelf, TabClip, TabCtrl, TabSystem, TabPhone, TabDocs, TabTimer };

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
        double w = wanted.Count <= 7 ? 40 : wanted.Count == 8 ? 37 : wanted.Count <= 10 ? 34 : 31;
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
        MusicCard.Visibility = MusicArea.Visibility;
        Grid.SetColumn(GlanceCard, music ? 2 : 0);
        Grid.SetColumnSpan(GlanceCard, music ? 1 : 3);
        Grid.SetColumn(GlanceScroll, music ? 2 : 0);
        Grid.SetColumnSpan(GlanceScroll, music ? 1 : 3);
        Grid.SetColumn(GlanceDots, music ? 2 : 0);
        Grid.SetColumnSpan(GlanceDots, music ? 1 : 3);
        glancePage = 0;

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
        const double narrow = 140, wide = 243, gutter = 30; // (inside the card's padding)
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
        GlanceCard.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (mode == Mode.Open) RefreshOpen();
        // As many parts as you like: they're shared out into pages once they're laid out
        Dispatcher.BeginInvoke(new Action(BuildGlancePages), DispatcherPriority.Loaded);
    }

    // ---------- the Home tab's glance column: as many parts as you like, a page at a time.
    // Scroll on it (or swipe sideways on a touchpad) to go through the pages; dots on its edge show which one you're on.

    /// <summary>For `--snapshot`: the next page of the glance column.</summary>
    internal void DebugPageGlance() => PageGlance(1);

    // Pages of whole parts: a part is never cut in half at the bottom; the next page slides in
    List<List<FrameworkElement>> glancePages = new();
    int glancePage;
    double wheelSum;
    DateTime lastWheelAt;
    bool gestureUsed;

    /// <summary>Share the parts out into pages that each fit the column (two side by side when there's no music).</summary>
    void BuildGlancePages()
    {
        var parts = Glance.Children.OfType<FrameworkElement>().ToList();
        foreach (var p in parts) p.Visibility = Visibility.Visible;
        double view = GlanceScroll.ActualHeight;
        if (view <= 0 || parts.Count == 0) { glancePages = new() { parts }; glancePage = 0; UpdateGlanceDots(1); return; }
        Glance.UpdateLayout();
        int perRow = MusicArea.Visibility == Visibility.Visible ? 1 : 2;
        var pages = new List<List<FrameworkElement>>();
        var current = new List<FrameworkElement>();
        double used = 0;
        foreach (var row in parts.Chunk(perRow))
        {
            // The first row on a page has no gap above it
            double first = row.Max(p => p.ActualHeight + ((Thickness)p.Tag).Bottom);
            double later = row.Max(p => p.ActualHeight + ((Thickness)p.Tag).Top + ((Thickness)p.Tag).Bottom);
            if (current.Count > 0 && used + later > view + 0.5) { pages.Add(current); current = new(); used = 0; }
            used += current.Count == 0 ? first : later;
            current.AddRange(row);
        }
        if (current.Count > 0) pages.Add(current);
        glancePages = pages;
        glancePage = Math.Min(glancePage, pages.Count - 1);
        ShowGlancePage(0);
    }

    void ShowGlancePage(int dir)
    {
        if (glancePages.Count == 0) return;
        var page = glancePages[glancePage];
        bool music = MusicArea.Visibility == Visibility.Visible;
        int perRow = music ? 1 : 2, i = 0;
        foreach (var p in Glance.Children.OfType<FrameworkElement>())
        {
            int at = page.IndexOf(p);
            p.Visibility = at >= 0 ? Visibility.Visible : Visibility.Collapsed;
            if (at < 0) continue;
            var m = (Thickness)p.Tag;
            p.Margin = new Thickness(0, at < perRow ? 0 : m.Top, !music && i % 2 == 0 ? 30 : 0, m.Bottom);
            i++;
        }
        UpdateGlanceDots(glancePages.Count);
        if (dir == 0) return;
        // The new page slides in from the way you scrolled
        var t = new TranslateTransform();
        Glance.RenderTransform = t;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dir * 26, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        Glance.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    void Glance_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (glancePages.Count < 2) return; // nothing more to see: leave the wheel alone
        e.Handled = true;
        GlanceGesture(-e.Delta);
    }

    /// <summary>One page per flick: a touchpad sends many small steps for one swipe, a mouse wheel one per notch.</summary>
    void GlanceGesture(int delta)
    {
        var now = DateTime.Now;
        bool sameGesture = now - lastWheelAt < TimeSpan.FromMilliseconds(180);
        lastWheelAt = now;
        if (!sameGesture) { wheelSum = 0; gestureUsed = false; }
        if (gestureUsed) return;
        wheelSum += delta;
        if (Math.Abs(wheelSum) < 100) return;
        gestureUsed = true;
        PageGlance(wheelSum > 0 ? 1 : -1);
    }

    void PageGlance(int dir)
    {
        if (glancePages.Count < 2) return;
        int to = Math.Clamp(glancePage + dir, 0, glancePages.Count - 1);
        if (to == glancePage) return;
        glancePage = to;
        ShowGlancePage(dir);
    }

    void Glance_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The column changed size (the notch opened, or the music part came or went): share the parts out again
        if (e.ViewportHeightChange != 0) Dispatcher.BeginInvoke(new Action(BuildGlancePages), DispatcherPriority.Loaded);
    }

    void UpdateGlanceDots(int count)
    {
        GlanceDots.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (GlanceDots.Children.Count != count)
        {
            GlanceDots.Children.Clear();
            for (int i = 0; i < count; i++)
                GlanceDots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 2.5, 0, 2.5) });
        }
        for (int i = 0; i < GlanceDots.Children.Count; i++)
        {
            var dot = (System.Windows.Shapes.Ellipse)GlanceDots.Children[i];
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, i == glancePage ? "Brim.Text" : "Brim.Dim");
            dot.Opacity = i == glancePage ? 0.9 : 0.45;
        }
    }

    /// <summary>A sideways swipe on a touchpad (Windows sends it as a horizontal wheel): the next or previous page.</summary>
    IntPtr GlanceHook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x020E /* WM_MOUSEHWHEEL */ && mode == Mode.Open && TabHome.IsChecked == true && GlanceScroll.IsMouseOver)
        {
            GlanceGesture((short)((w.ToInt64() >> 16) & 0xFFFF));
            handled = true;
        }
        return IntPtr.Zero;
    }

    void MessageBlock_Click(object sender, MouseButtonEventArgs e)
    {
        if (TabMsgs.Parent != null) TabMsgs.IsChecked = true;
        else if (MessageCenter.Visible.FirstOrDefault() is { } m) { MessageCenter.Open(m); Collapse(); }
    }

    internal void DebugPeek() { Collapse(animate: false); Peek(); }

    public bool IsOpen => mode == Mode.Open;

    /// <summary>Lyrics in the notch: lyrics on, and not switched off for the notch (the music widget can still show them).</summary>
    static bool NotchLyrics => SettingsStore.Current.Lyrics && SettingsStore.Current.NotchLyrics;

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

    /// <summary>Shows the notch, or tucks it away while an app covers its spot (each screen's notch on its own).</summary>
    public void SetAppInFront(bool app, bool mirrorsToo = true)
    {
        if (mirrorsToo) foreach (var m in MirrorsOf()) m.SetAppInFront(app);
        if (app == appInFront) return;
        appInFront = app;
        Log.Note("notch", (app ? "tucked away: app in front " : "back: desktop in front ") + Log.Foreground());
        if (S.HideOverApps && !S.HideUntilHover && mode == Mode.Closed) { lastClosed = default; Collapse(); }
    }

    /// <summary>
    /// The strip at the top of the screen the notch sits in, in screen pixels: an app window reaching into it
    /// (or maximised on this screen) tucks the notch away. Measured for the notch as it looks when shown.
    /// </summary>
    internal Native.RECT Zone()
    {
        var size = ShownClosedSize();
        double w = size.Width + 20; // the ears either side
        double h = size.Height + (S.Shape == NotchShape.Island ? S.IslandTop : 0) + 4;
        double x = ShapeRow.HorizontalAlignment switch
        {
            HorizontalAlignment.Left => 0,
            HorizontalAlignment.Right => ActualWidth - w,
            _ => (ActualWidth - w) / 2,
        };
        var tl = PointToScreen(new Point(x, 0));
        var br = PointToScreen(new Point(x + w, h));
        return new Native.RECT { Left = (int)tl.X, Top = (int)tl.Y, Right = (int)br.X, Bottom = (int)br.Y };
    }

    Size ClosedSize()
    {
        bool island = S.Shape == NotchShape.Island;
        if (HideMode) return new Size(island ? 160 : 180, 6);
        return ShownClosedSize();
    }

    Size ShownClosedSize()
    {
        bool island = S.Shape == NotchShape.Island;
        double h = island ? 34 : 32;
        if (SongActive) return new Size(NotchLyrics && Lyrics.Lines != null ? lyricWidth : 300, h);
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
        Veil.CornerRadius = cr;
        Edge.CornerRadius = cr;
        if (clearer) FitEdge();
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

    bool bodyAnimating;
    int bodyGen;

    void AnimateBody(Size to, double radius, double ms, bool bouncy)
    {
        Log.Trace($"notch: animating to {to.Width:0}x{to.Height:0}");
        var dur = new Duration(TimeSpan.FromMilliseconds(ms));
        // One smooth ease, no overshoot: quick off the mark and settling softly (growing), or a plain glide (shrinking)
        IEasingFunction ease = bouncy
            ? new QuarticEase { EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        // Glass while it grows or shrinks: the blur layer is a separate window, and moving and re-blurring it on every
        // frame made the animation stutter (most of all on 60 Hz screens). It steps away meanwhile and a veil in the
        // glass's colour covers the notch; once it settles the blur comes back and the veil fades off it, so it reads
        // as one surface turning to glass rather than a second window popping in.
        int gen = ++bodyGen;
        var surface = (mode != Mode.Open && S.NotchColor != null ? ThemeManager.Parse(S.NotchColor) : (Color?)null)
                      ?? ThemeManager.Parse(ThemeManager.Current.Surface);
        bool glass = S.NotchSurface != "Solid" && !(HideMode && sliverSettled);
        Veil.BeginAnimation(OpacityProperty, null);
        if (glass && !ClearAndClosed)
        {
            bodyAnimating = true;
            backdrop?.Hide();
            Veil.Background = new SolidColorBrush(Backdrop.Tint(surface, Crystal ? (byte)0xA0 : (byte)0xE6));
            Veil.Opacity = 1;
        }
        else if (glass)
        {
            // Clear closes into nothing: the card fades away as it shrinks
            bodyAnimating = false;
            Veil.Background = new SolidColorBrush(Backdrop.Tint(surface, 0xC0));
            Veil.Opacity = 1;
            Veil.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
        }
        else Veil.Opacity = 0;
        var grow = new DoubleAnimation(to.Width, dur) { EasingFunction = ease };
        grow.Completed += (_, _) =>
        {
            if (gen != bodyGen || !bodyAnimating) return; // a newer animation took over
            bodyAnimating = false;
            ApplySurface(); // the see-through tint and the blur behind it, at the final size
            Veil.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        };
        Body.BeginAnimation(WidthProperty, grow);
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
        bodyGen++;
        bodyAnimating = false;
        Veil.BeginAnimation(OpacityProperty, null);
        Veil.Opacity = 0;
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
        OpenView.Height = OpenSize.Height;
        AnimateBody(OpenSize, OpenRadius, SpeedMs, true);
        Fade(ClosedView, 0, 90);
        Fade(PeekView, 0, 90);
        Fade(ActivityView, 0, 90);
        activityTimer.Stop();
        // The content fades in as the notch finishes growing, briefly: a fade blends the whole content on every frame,
        // which is costly, so the fewer frames it runs for the smoother the opening (measured: about a fifth less work)
        Fade(OpenView, 1, 120, SpeedMs * 0.3);
        Tab_Checked(this, new RoutedEventArgs());
        RefreshOpen();
        UpdateTint();
        UpdateBarsTimer();
    }

    void Collapse(bool animate = true)
    {
        StopTyping();
        mode = Mode.Closed;
        phoneView?.SetShown(false);
        leaveTimer.Stop();
        peekTimer.Stop();
        ApplySurface();
        var size = ClosedSize();
        lastClosed = size;
        UpdateClosedContent(resize: false);

        if (animate) AnimateBody(size, ClosedRadius(size), SpeedMs * 0.85, false);
        else SnapBody(size, ClosedRadius(size));

        Fade(OpenView, 0, animate ? 70 : 0);
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
        Lyrics.Check(); // a new song drops the last one's lyrics before anything shows them
        UpdateClosedContent(resize: true);
        ShowLyric(force: true);
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
    string SongFace() => NotchLyrics && Lyrics.LineAt(M.Position) is { } line ? line : M.Title;

    // ------------------------------------------------------------------ lyrics

    readonly DispatcherTimer lyricTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    int shownLyric = int.MinValue;

    void OnLyrics()
    {
        // Only tick while there's something to keep in step (a few times a second is enough)
        if (Lyrics.Lines != null) lyricTimer.Start(); else lyricTimer.Stop();
        // One width per song, wide enough for its longest line (not a resize with every line, which twitched)
        lyricWidth = Lyrics.Lines == null ? 300 : Math.Clamp(Math.Ceiling((Lyrics.Lines.Max(l => TextWidth(l.Text, 12)) + ClosedChrome) / 20) * 20, 300, MaxClosedWidth);
        UpdateClosedContent(resize: true);
        ShowLyric(force: true);
    }

    double lyricWidth = 300;
    const double MaxClosedWidth = 560;
    // Around the text in the closed notch: side margins, cover, the text's own margin, the bars
    const double ClosedChrome = 24 + 20 + 16 + 22 + 6;

    double TextWidth(string text, double size, FontWeight? weight = null) =>
        new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FaceText.FontFamily, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    /// <summary>A lyric too long even for the widest notch gets a slightly smaller font before it's cut short.</summary>
    double FaceSizeFor(string text) =>
        NotchLyrics && Lyrics.Lines != null && TextWidth(text, 12) + ClosedChrome > lyricWidth ? 11 : 12;

    /// <summary>Show the line being sung: in the closed notch (fading in) and under the song in the open one.</summary>
    void ShowLyric(bool force)
    {
        var lines = NotchLyrics ? Lyrics.Lines : null;
        int now = lines == null ? int.MinValue : Lyrics.IndexAt(lines, M.Position);
        if (!force && now == shownLyric) return;
        bool changed = now != shownLyric;
        shownLyric = now;

        if (mode == Mode.Closed && SongActive && !FocusTimer.Running)
        {
            var text = SongFace();
            if (FaceText.Text != text)
            {
                FaceText.Text = text;
                FaceText.FontSize = FaceSizeFor(text);
                if (changed) FaceText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(220)));
            }
        }

        bool show = lines != null && M.HasSession && !string.IsNullOrEmpty(M.Title);
        LyricBlock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var pos = M.Position;
        LyricNow.Text = Lyrics.LineAt(pos) ?? "♪";
        LyricNext.Text = Lyrics.LineAt(pos, 1) ?? ""; // (before the singing starts: the first line)
    }

    void UpdateClosedContent(bool resize)
    {
        bool song = SongActive;
        MiniArt.Visibility = song && M.Art != null ? Visibility.Visible : Visibility.Collapsed;
        MiniArt.Background = ArtFill();
        MiniBars.Visibility = song ? Visibility.Visible : Visibility.Collapsed;
        FaceText.Text = song && !FocusTimer.Running ? SongFace() : FaceString();
        FaceText.FontSize = song ? FaceSizeFor(FaceText.Text) : 13;
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
    }

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
            || MsgPanel == null || TabMsgs == null || NotesPanel == null || TabNotes == null || CalcPanel == null || TabCalc == null || SystemPanel == null || TabSystem == null || PhonePanel == null || TabPhone == null || DocsPanel == null || TabDocs == null) return;
        bool notes = TabNotes.IsChecked == true;
        NotesPanel.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        if (notes && NotesPanel.Children.Count == 0) NotesPanel.Children.Add(new NotesView(EnableTyping));
        bool calc = TabCalc.IsChecked == true;
        CalcPanel.Visibility = calc ? Visibility.Visible : Visibility.Collapsed;
        if (calc && CalcPanel.Children.Count == 0) CalcPanel.Children.Add(new CalculatorView(EnableTyping));
        // Typing in Notes (or the calculator, or the clipboard search) holds the notch open and has the keyboard. Off
        // to a tab with nothing to type in: let go of both, so moving the mouse away closes it again
        if (typing && TabNotes.IsChecked != true && TabCalc.IsChecked != true && TabClip.IsChecked != true && TabPhone.IsChecked != true)
        {
            StopTyping();
            if (!Body.IsMouseOver) leaveTimer.Start();
        }
        bool docs = TabDocs.IsChecked == true;
        DocsPanel.Visibility = docs ? Visibility.Visible : Visibility.Collapsed;
        if (docs && DocsPanel.Children.Count == 0) DocsPanel.Children.Add(new DocsView(() => holdOpen++, () => { holdOpen = Math.Max(0, holdOpen - 1); if (!Body.IsMouseOver) leaveTimer.Start(); }));
        bool phone = TabPhone.IsChecked == true;
        PhonePanel.Visibility = phone ? Visibility.Visible : Visibility.Collapsed;
        if (phone && phoneView == null) PhonePanel.Children.Add(phoneView = new PhoneView(() => holdOpen++, () => { holdOpen = Math.Max(0, holdOpen - 1); if (!Body.IsMouseOver) leaveTimer.Start(); }, EnableTyping));
        phoneView?.SetShown(mode == Mode.Open && phone);
        bool sys = TabSystem.IsChecked == true;
        SystemPanel.Visibility = sys ? Visibility.Visible : Visibility.Collapsed;
        if (sys && SystemPanel.Children.Count == 0) SystemPanel.Children.Add(new SystemView());
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
        // Tabs whose content would sit straight on the glass get the shared card behind them
        PanelCard.Visibility = TabApps.IsChecked == true || TabShelf.IsChecked == true || TabClip.IsChecked == true || TabCtrl.IsChecked == true
            || TabTimer.IsChecked == true || calc || phone ? Visibility.Visible : Visibility.Collapsed;
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
        // A tab that needs more (or less) room: grow or shrink to it
        if (mode == Mode.Open && Math.Abs(OpenView.Height - OpenSize.Height) > 0.5)
        {
            OpenView.Height = OpenSize.Height;
            AnimateBody(OpenSize, OpenRadius, SpeedMs, true);
        }
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
                Tip = s.Path + (i <= 9 && HotkeyCatalog.ForShortcut(i) is { } k ? $"   ·   {k}" : ""),
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
        Launcher.Open(vm.Item!);
        Collapse();
    }

    // ---------- drag shortcuts to put them in your own order, with the same movement as the dock and the Games tab:
    // the tile lifts and follows the pointer across the rows, the others slide aside, and it settles where you let go

    ShortcutVM? tilePressed;
    Point tilePressAt;
    bool swallowTileClick;
    LiveReorder? tileReorder;
    Panel? tileHost;

    void AppTile_Press(object sender, MouseButtonEventArgs e)
    {
        tilePressed = ((FrameworkElement)sender).Tag as ShortcutVM;
        if (tilePressed?.IsAdd == true) tilePressed = null; // the "Add" tile stays last
        tilePressAt = e.GetPosition(AppsList);
    }

    void AppTile_DragMove(object sender, MouseEventArgs e)
    {
        if (tilePressed == null || e.LeftButton != MouseButtonState.Pressed || tileReorder?.Active == true) return;
        if ((e.GetPosition(AppsList) - tilePressAt).Length < 8) return;
        if (AppsList.ItemContainerGenerator.ContainerFromItem(tilePressed) is not FrameworkElement box || VisualTreeHelper.GetParent(box) is not Panel host) return;
        if (tileHost != host) tileReorder = null; // the tiles were rebuilt
        tileHost = host;
        holdOpen++; // keep the notch open while you drag
        tileReorder ??= new LiveReorder(host, c => c.DataContext is ShortcutVM { IsAdd: false }, (_, from, to) => shortcuts.Move(from, to), changed =>
        {
            holdOpen = Math.Max(0, holdOpen - 1);
            tilePressed = null;
            // The button would take letting go as a click: skip that one
            swallowTileClick = true;
            Dispatcher.BeginInvoke(new Action(() => swallowTileClick = false), DispatcherPriority.Input);
            if (changed)
            {
                // Save the new order (the Ctrl+Alt+number keys follow it), once the tile has settled
                var order = shortcuts.Where(s => s.Item != null).Select(s => s.Item!).ToList();
                S.Shortcuts.Clear();
                S.Shortcuts.AddRange(order);
                SettingsStore.Save();
                var renumber = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
                renumber.Tick += (_, _) => { renumber.Stop(); RebuildShortcuts(); ((App)Application.Current).RefreshSettingsWindow(); };
                renumber.Start();
            }
            if (!Body.IsMouseOver) leaveTimer.Start();
        }, lift: 0, liftScale: 1.08) { Wrap = true };
        tileReorder.Begin(box, e.GetPosition(host));
    }

    void AppTile_DragEnd(object sender, MouseButtonEventArgs e)
    {
        if (tileReorder?.Active != true) tilePressed = null; // a plain click (a drag ends in the reorder itself)
    }


    void AppTile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ShortcutVM { Item: { } item }) return;
        e.Handled = true;
        var menu = new ContextMenu();
        if (Launcher.CanRunAsAdmin(item.Path))
        {
            menu.Items.Add(MakeItem("Run as administrator", () => { Collapse(); Launcher.RunAsAdmin(item.Path, item.Args); }));
            var always = new MenuItem { Header = "Always run as administrator", IsCheckable = true, IsChecked = item.Admin };
            always.Click += (_, _) => { item.Admin = !item.Admin; SettingsStore.Save(); };
            menu.Items.Add(always);
            menu.Items.Add(new Separator());
        }
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

    // ---------- tab right-click menus and dragging tabs into a new order

    RadioButton? tabPressed;
    Point tabPressAt;
    LiveReorder? tabReorder;

    /// <summary>Snapshot check: pick up the first tab and carry it <paramref name="dx"/> to the right (null: let go).</summary>
    internal void DebugTabDrag(double? dx)
    {
        tabReorder ??= new LiveReorder(TabsRow, c => c is RadioButton, null, _ => { }, lift: 0, liftScale: 1.15);
        if (dx == null) { tabReorder.End(); return; }
        if (!tabReorder.Active)
        {
            var first = TabsRow.Children.OfType<RadioButton>().FirstOrDefault();
            if (first == null) return;
            debugDragStart = new Point(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(first).X + first.ActualWidth / 2, 10);
            tabReorder.Begin(first, debugDragStart);
        }
        tabReorder.Drag(new Point(debugDragStart.X + dx.Value, 10));
    }

    Point debugDragStart;

    void TabsMoved(bool changed)
    {
        tabPressed = null;
        holdOpen = Math.Max(0, holdOpen - 1);
        if (!Body.IsMouseOver) leaveTimer.Start();
        if (!changed) return;
        var ids = NotchCatalog.Tabs.Select(t => t.id).ToList();
        S.NotchTabs = TabsRow.Children.OfType<RadioButton>().Select(r => ids.FirstOrDefault(id => TabFor(id) == r)).OfType<string>().ToList();
        SettingsStore.Save();
        Log.Note("tabs", "new order: " + string.Join(", ", S.NotchTabs));
        // The notches on other screens and the settings page follow (after this one has settled)
        Dispatcher.BeginInvoke(new Action(() => ((App)Application.Current).ApplyAll()), DispatcherPriority.ApplicationIdle);
        ((App)Application.Current).RefreshSettingsWindow();
    }

    /// <summary>Right-click a tab: its own quick actions first (only those that apply right now), then moving, hiding and choosing tabs.</summary>
    void TabMenu(RadioButton tab)
    {
        var menu = new ContextMenu();
        void Add(string header, Action act) => menu.Items.Add(MakeItem(header, act));

        if (tab == TabClip)
        {
            int pinned = ClipboardHistory.Items.Count(i => i.Pinned), all = ClipboardHistory.Items.Count;
            if (all > pinned && pinned > 0) Add($"Clear all but the {pinned} pinned", () => ClipboardHistory.Clear(pinnedToo: false));
            if (all > 0) Add(pinned > 0 ? "Clear all, pinned too" : "Clear all", () => ClipboardHistory.Clear(pinnedToo: true));
        }
        else if (tab == TabMsgs)
        {
            if (MessageCenter.Visible.Count > 0) Add("Clear all messages", MessageCenter.DismissAll);
            if (MessageCenter.MutedUntil is DateTime until)
                Add($"Pop-ups snoozed until {(until.Date == DateTime.Today ? until.ToString("HH:mm") : until.ToString("ddd HH:mm"))}: turn back on",
                    () => { MessageCenter.MutedUntil = null; Note("Message pop-ups are back on"); });
            else
            {
                Add("Snooze pop-ups for 1 hour", () => { MessageCenter.MutedUntil = DateTime.Now.AddHours(1); Note("No pop-ups for an hour"); });
                Add("Snooze pop-ups until tomorrow", () => { MessageCenter.MutedUntil = DateTime.Today.AddDays(1).AddHours(8); Note("No pop-ups until 08:00 tomorrow"); });
            }
        }
        else if (tab == TabNotes)
        {
            if (NotesPanel.Children.Count == 0) NotesPanel.Children.Add(new NotesView(EnableTyping));
            if (NotesPanel.Children[0] is NotesView notes && !notes.IsEmpty)
            {
                Add("Copy all", () => { notes.CopyAll(); Note("Notes copied ✓"); });
                Add("Clear the page", () => { notes.Clear(); Note("Notes cleared (the old page is in notes.txt.bak)"); });
            }
        }
        else if (tab == TabCalc)
        {
            if (CalcPanel.Children.Count == 0) CalcPanel.Children.Add(new CalculatorView(EnableTyping));
            if (CalcPanel.Children[0] is CalculatorView calc)
            {
                if (calc.HasResult) Add("Copy the answer", calc.CopyResult);
                if (calc.HasHistory) Add("Clear the history", () => { calc.ClearHistory(); Note("Calculator history cleared"); });
            }
        }
        else if (tab == TabGames) Add("Look for new games now", () => { _ = GameLibrary.Refresh(); Note("Looking for new games…"); });
        else if (tab == TabShelf && Shelf.Items.Count > 0) Add("Clear the shelf", () => Shelf.Items.Clear());
        else if (tab == TabApps) Add("Add shortcuts…", () => { Collapse(); ((App)Application.Current).OpenSettings("Shortcuts"); });

        // Every tab: move it, hide it, or pick tabs in Settings
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var shown = TabsRow.Children.OfType<RadioButton>().ToList();
        int at = shown.IndexOf(tab);
        if (at > 0) Add("Move left", () => MoveTab(tab, -1));
        if (at >= 0 && at < shown.Count - 1) Add("Move right", () => MoveTab(tab, +1));
        if (shown.Count > 1) Add("Hide this tab", () =>
        {
            var id = IdOf(tab);
            if (id == null) return;
            S.NotchTabs.Remove(id);
            SettingsStore.Save();
            ((App)Application.Current).ApplyAll();
            ((App)Application.Current).RefreshSettingsWindow();
            Note("Tab hidden. Bring it back in Settings › Notch");
        });
        Add("Choose tabs…", () => { Collapse(); ((App)Application.Current).OpenSettings("Notch"); });
        ShowMenu(menu, tab);
    }

    string? IdOf(RadioButton tab) => NotchCatalog.Tabs.Select(t => t.id).FirstOrDefault(id => TabFor(id) == tab);

    void MoveTab(RadioButton tab, int by)
    {
        var id = IdOf(tab);
        int i = id == null ? -1 : S.NotchTabs.IndexOf(id);
        if (i < 0 || i + by < 0 || i + by >= S.NotchTabs.Count) return;
        (S.NotchTabs[i], S.NotchTabs[i + by]) = (S.NotchTabs[i + by], S.NotchTabs[i]);
        SettingsStore.Save();
        ((App)Application.Current).ApplyAll();
        ((App)Application.Current).RefreshSettingsWindow();
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
        ShelfInfo.Text = n == 0 ? "Click to select · drag out to copy" : $"{n} selected · drag one to take all";
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
        // Documents: convert or edit straight from the Shelf (the result lands on the Shelf too)
        if (Docs.IsPdf(item.Path))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem("Convert to Word", () => ConvertOnShelf(item.Path, Docs.ToWord)));
            menu.Items.Add(MakeItem("Edit PDF…", () => PdfEditorWindow.Open(item.Path)));
        }
        else if (Docs.CanMakePdf(item.Path) && System.IO.File.Exists(item.Path))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem("Convert to PDF", () => ConvertOnShelf(item.Path, Docs.ToPdf)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Remove from shelf", () => Shelf.Items.Remove(item)));
        ShowMenu(menu, (UIElement)sender);
    }

    async void ConvertOnShelf(string path, System.Func<string, System.Threading.Tasks.Task<string>> run)
    {
        ShowActivity("", "Converting…", System.IO.Path.GetFileName(path), null, 30);
        try
        {
            var result = await run(path);
            Shelf.Add(new[] { result });
            ShowAction("", "Done", System.IO.Path.GetFileName(result) + " is on the Shelf", () => Open(TabShelf), 5);
        }
        catch (System.Exception ex)
        {
            Log.Error("docs", ex);
            ShowActivity("", "Couldn't convert", ex.Message, null, 6);
        }
    }

    void ShelfClear_Click(object sender, RoutedEventArgs e) => Shelf.Items.Clear();

    void ShelfPhone_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenPhone();

    /// <summary>Open the notch on the Shelf (to show what just arrived).</summary>
    public void OpenShelf() => Open(TabShelf);

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
    IntPtr typingFrom;

    void ClipSearch_Click(object sender, MouseButtonEventArgs e) => EnableTyping(ClipSearch);

    void EnableTyping(TextBox box)
    {
        if (!typing)
        {
            typing = true;
            var fg = Native.GetForegroundWindow();
            typingFrom = fg != hwnd ? fg : IntPtr.Zero;
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
        // Still the window with the keyboard: give it back to the app you were in
        if (Native.GetForegroundWindow() == hwnd && typingFrom != IntPtr.Zero && Native.IsWindow(typingFrom)) Native.ForceForeground(typingFrom);
        typingFrom = IntPtr.Zero;
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
