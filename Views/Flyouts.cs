using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Views;

/// <summary>A small rounded panel that pops up above a dock button and closes when you click away.</summary>
public sealed class Flyout : Window
{
    static Flyout? open;
    public static bool IsOpen => open != null;

    /// <summary>Close whatever panel is open (used before handing focus to another app's menu).</summary>
    public static void CloseOpen() => open?.SafeClose();

    bool holdForMenu;
    System.Windows.Threading.DispatcherTimer? holdTimer;

    /// <summary>
    /// Keep the open panel up while another app shows its own menu over it (right-clicking a tray icon), the way
    /// Windows' tray overflow does. Once that menu is gone: if nothing else took over, the panel takes focus back
    /// (so clicking elsewhere still closes it); if you picked something that opened a window, the panel closes.
    /// </summary>
    public static void HoldForMenu(IntPtr owner, uint pid) => open?.StartHold(owner, pid);

    void StartHold(IntPtr owner, uint pid)
    {
        holdForMenu = true;
        holdTimer?.Stop();
        var started = DateTime.Now;
        bool sawMenu = false;
        int goneTicks = 0;
        var me = new WindowInteropHelper(this).Handle;
        holdTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        holdTimer.Tick += (_, _) =>
        {
            if (open != this) { holdTimer.Stop(); return; }
            if (MenuShowing(pid, owner, me)) { sawMenu = true; goneTicks = 0; return; }
            if (!sawMenu && DateTime.Now - started < TimeSpan.FromSeconds(1.5)) return; // still waiting for it to appear
            if (++goneTicks < 2) return;
            holdTimer.Stop();
            holdForMenu = false;
            var fg = Native.GetForegroundWindow();
            if (fg == me) return;
            // Menu dismissed (Esc, or a click on this panel): the focus is left on the app's hidden tray window
            if (fg == IntPtr.Zero || fg == owner || !Native.IsWindowVisible(fg)) { Activate(); Native.ForceForeground(me); return; }
            SafeClose(); // you chose something that opened a window, or clicked another app
        };
        holdTimer.Start();
    }

    /// <summary>A standard Windows menu, or the app's own popup (apps that draw their own menus), is on screen.</summary>
    static bool MenuShowing(uint pid, IntPtr owner, IntPtr me)
    {
        var m = Native.FindWindow("#32768", null);
        if (m != IntPtr.Zero && Native.IsWindowVisible(m)) return true;
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == owner || fg == me || !Native.IsWindowVisible(fg)) return false;
        Native.GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid != pid) return false;
        long style = Native.GetWindowLongPtr(fg, -16).ToInt64();
        bool popup = (style & 0x80000000L) != 0 /* WS_POPUP */ && (style & 0x00C00000L) != 0x00C00000L; // no title bar
        long ex = Native.GetWindowLongPtr(fg, Native.GWL_EXSTYLE).ToInt64();
        return popup && ((ex & Native.WS_EX_TOOLWINDOW) != 0 || (ex & 0x8L /* WS_EX_TOPMOST */) != 0);
    }
    static string? lastKind;
    static DateTime lastClosed;
    readonly string kind;
    readonly FrameworkElement anchor;

    Flyout(string kind, FrameworkElement anchor, FrameworkElement content)
    {
        this.kind = kind;
        this.anchor = anchor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Left = -10000;
        Top = -10000;
        FontFamily = (FontFamily)FindResource("Brim.Font");
        SetResourceReference(ForegroundProperty, "Brim.Text");

        // Never taller than the screen: long content (app mixer, speaker list) scrolls instead of running off the bottom
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        var border = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };
        border.SetResourceReference(Border.BackgroundProperty, "Brim.Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Brim.Stroke");
        Content = border;

        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle, false);
        // Take focus properly (Windows can refuse a plain Activate), so clicking anywhere else closes the panel
        ContentRendered += (_, _) =>
        {
            Place();
            Activate();
            var h = new WindowInteropHelper(this).Handle;
            if (Native.GetForegroundWindow() != h) Native.ForceForeground(h);
        };
        // Growing (e.g. opening the mixer) re-anchors the panel above its button instead of spilling downwards
        SizeChanged += (_, _) => { if (IsLoaded) Place(); };
        // Closing deactivates the window, which would ask it to close a second time mid-close
        Closing += (_, _) => closing = true;
        Deactivated += (_, _) => { if (!holdForMenu) SafeClose(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };
        Closed += (_, _) =>
        {
            if (open == this) open = null;
            lastKind = kind;
            lastClosed = DateTime.Now;
            ((App)Application.Current).ScheduleTrim();
        };
    }

    bool closing;

    void SafeClose()
    {
        if (closing) return;
        closing = true;
        Close();
    }

    public static async void Toggle(string kind, FrameworkElement anchor, Func<FrameworkElement> content)
    {
        if (open != null)
        {
            bool same = open.kind == kind;
            open.SafeClose();
            if (same) return;
        }
        // The click that deactivated (and closed) the flyout lands on its own button a moment later
        if (lastKind == kind && (DateTime.Now - lastClosed).TotalMilliseconds < 400) return;

        // Start and Search draw in a layer above every app window, so no panel can sit over them.
        // Close them first (as Windows' own taskbar panels do), then open ours.
        if (Native.ClassOf(Native.GetForegroundWindow()) == "Windows.UI.Core.CoreWindow")
        {
            Native.TapKeys(0x1B /* Esc */);
            await System.Threading.Tasks.Task.Delay(180);
        }
        open = new Flyout(kind, anchor, content());
        open.Show();
    }

    void Place()
    {
        if (PresentationSource.FromVisual(anchor) is not { CompositionTarget: { } ct }) return;
        var topCentre = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, 0));
        var dip = ct.TransformFromDevice.Transform(topCentre);
        double left = dip.X - ActualWidth / 2;
        left = Math.Clamp(left, SystemParameters.VirtualScreenLeft + 8, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth - 8);
        Left = left;
        Top = Math.Max(8, dip.Y - ActualHeight - 8);
    }
}

/// <summary>Wi-Fi, Bluetooth, microphone, volume with output picker, and brightness.</summary>
public sealed class QuickPanel : StackPanel
{
    readonly bool compact;
    readonly Tile wifi, bluetooth, mic;
    readonly Slider volume = new() { Minimum = 0, Maximum = 100 };
    readonly Slider brightness = new() { Minimum = 0, Maximum = 100 };
    readonly Button muteBtn;
    readonly Grid brightnessRow;
    readonly StackPanel devices = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock footer = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    bool updating, draggingBrightness;

    public QuickPanel(bool compact)
    {
        this.compact = compact;
        Width = compact ? double.NaN : 330;
        footer.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");

        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(-4, -4, -4, 0) };
        wifi = new Tile("", "Wi-Fi", async on => { await Radios.SetWifi(on); Refresh(); }, () => Launcher.Open("ms-availablenetworks:"));
        bluetooth = new Tile("", "Bluetooth", async on => { await Radios.SetBluetooth(on); Refresh(); }, OpenBluetoothPanel);
        mic = new Tile("", "Microphone", on => { Audio.MicMuted = !on; Refresh(); return System.Threading.Tasks.Task.CompletedTask; }, () => Launcher.Open("ms-settings:sound"));
        tiles.Children.Add(wifi);
        tiles.Children.Add(bluetooth);
        tiles.Children.Add(mic);
        Children.Add(tiles);

        // Volume
        var volRow = new Grid { Margin = new Thickness(0, compact ? 10 : 14, 0, 0) };
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volRow.ColumnDefinitions.Add(new ColumnDefinition());
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        muteBtn = IconBtn("", () => { Audio.Muted = !Audio.Muted; Refresh(); }, "Mute");
        volRow.Children.Add(muteBtn);
        volume.SetResourceReference(StyleProperty, "BrimSlider");
        volume.VerticalAlignment = VerticalAlignment.Center;
        volume.Margin = new Thickness(8, 0, 8, 0);
        volume.ValueChanged += (_, e) =>
        {
            if (updating) return;
            Audio.Volume = (float)(e.NewValue / 100);
            if (Audio.Muted) Audio.Muted = false;
            UpdateMuteGlyph();
        };
        Grid.SetColumn(volume, 1);
        volRow.Children.Add(volume);
        if (!compact)
        {
            var extra = new StackPanel { Orientation = Orientation.Horizontal };
            extra.Children.Add(IconBtn("", ToggleApps, "Volume for each app"));
            extra.Children.Add(IconBtn("", ToggleDevices, "Choose speakers or headphones"));
            Grid.SetColumn(extra, 2);
            volRow.Children.Add(extra);
        }
        Children.Add(volRow);
        Children.Add(devices);
        Children.Add(apps);

        // Brightness (laptop screens)
        brightnessRow = new Grid { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brightnessRow.ColumnDefinitions.Add(new ColumnDefinition());
        brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sun = new TextBlock { Text = "", FontSize = 14, Width = 32, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sun.FontFamily = (FontFamily)Application.Current.FindResource("Brim.Icons");
        brightnessRow.Children.Add(sun);
        brightness.SetResourceReference(StyleProperty, "BrimSlider");
        brightness.VerticalAlignment = VerticalAlignment.Center;
        brightness.Margin = new Thickness(8, 0, compact ? 8 : 40, 0);
        brightness.PreviewMouseDown += (_, _) => draggingBrightness = true;
        brightness.PreviewMouseUp += (_, _) => draggingBrightness = false;
        brightness.ValueChanged += (_, e) => { if (!updating) Brightness.Set((int)e.NewValue); };
        Grid.SetColumn(brightness, 1);
        brightnessRow.Children.Add(brightness);
        Children.Add(brightnessRow);
        Children.Add(btList);

        if (!compact)
        {
            var foot = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            foot.Children.Add(footer);
            var more = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            more.Children.Add(IconBtn("", () => Launcher.Open("ms-settings:network"), "Network settings"));
            more.Children.Add(IconBtn("", () => Launcher.Open("ms-settings:"), "Windows Settings"));
            foot.Children.Add(more);
            Children.Add(foot);
        }

        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }

    /// <summary>Windows' own Bluetooth panel: your devices (click to connect) and "More Bluetooth settings".</summary>
    static async void OpenBluetoothPanel()
    {
        Flyout.CloseOpen();
        await System.Threading.Tasks.Task.Delay(150);
        Launcher.Open("ms-actioncenter:controlcenter/bluetooth");
    }

    static Button IconBtn(string glyph, Action click, string tip)
    {
        var b = new Button { Content = glyph, ToolTip = tip };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.Click += (_, _) => click();
        return b;
    }

    void UpdateMuteGlyph() => muteBtn.Content = Audio.Muted ? "" : "";

    async void Refresh()
    {
        await Radios.Load();
        var net = Network.Current();
        if (Radios.HasWifi)
        {
            wifi.Visibility = Visibility.Visible;
            wifi.Set(Radios.WifiOn, Radios.WifiOn ? (net.Wireless ? net.Name : net.Connected ? net.Name : "Not connected") : "Off", "");
        }
        else wifi.SetInfo(net.Wireless ? "" : "", net.Connected ? net.Name : "Offline", "Network");

        if (Radios.HasBluetooth) bluetooth.Set(Radios.BluetoothOn, Radios.BluetoothOn ? "On" : "Off", "");
        else bluetooth.SetInfo("", "Not available", "Bluetooth");

        if (Audio.HasMic) mic.Set(!Audio.MicMuted, Audio.MicMuted ? "Muted" : "On", "");
        else mic.SetInfo("", "No microphone", "Microphone");

        updating = true;
        volume.Value = Math.Round(Audio.Volume * 100);
        updating = false;
        UpdateMuteGlyph();

        if (!draggingBrightness)
        {
            var b = await Brightness.Read();
            brightnessRow.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
            if (b != null) { updating = true; brightness.Value = b.Value; updating = false; }
        }

        footer.Text = SystemInfo.HasBattery
            ? $"Battery {SystemInfo.BatteryPercent}%{(SystemInfo.Charging ? " · charging" : "")}"
            : $"Volume {Math.Round(Audio.Volume * 100)}%";
        if (devices.Visibility == Visibility.Visible) FillDevices();
        FillBluetooth();
    }

    readonly WrapPanel btList = new() { Margin = new Thickness(0, 8, 0, 0) };

    /// <summary>Connected Bluetooth headphones, mice and controllers with their battery level.</summary>
    void FillBluetooth()
    {
        btList.Children.Clear();
        foreach (var d in DeviceBattery.Devices)
        {
            var chip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6) };
            chip.SetResourceReference(Border.BackgroundProperty, "Brim.Hover");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var g = new TextBlock { Text = d.Glyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            g.FontFamily = (FontFamily)Application.Current.FindResource("Brim.Icons");
            row.Children.Add(g);
            row.Children.Add(new TextBlock { Text = d.Name, FontSize = 11.5, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var pct = new TextBlock { Text = $"  {d.Battery}%", FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            if (d.Battery <= 15) pct.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            row.Children.Add(pct);
            chip.Child = row;
            chip.ToolTip = $"{d.Name}: battery {d.Battery}%";
            btList.Children.Add(chip);
        }
        btList.Visibility = btList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    readonly StackPanel apps = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };

    void ToggleApps()
    {
        apps.Visibility = apps.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (apps.Visibility == Visibility.Visible) FillApps();
    }

    /// <summary>One slider per app that can play sound, like Windows' volume mixer.</summary>
    void FillApps()
    {
        apps.Children.Clear();
        var list = Audio.Apps();
        if (list.Count == 0)
        {
            var none = new TextBlock { Text = "No apps are using sound right now.", FontSize = 12, Margin = new Thickness(10, 4, 0, 4) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
            apps.Children.Add(none);
            return;
        }
        foreach (var app in list)
        {
            var a = app;
            var row = new Grid { Margin = new Thickness(4, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = a.ExePath != null ? IconHelper.ForPath(a.ExePath) : null;
            if (icon != null) row.Children.Add(new Image { Source = icon, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center });
            else
            {
                var g = new TextBlock { Text = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
                g.FontFamily = (FontFamily)Application.Current.FindResource("Brim.Icons");
                row.Children.Add(g);
            }

            var mid = new StackPanel { Margin = new Thickness(4, 0, 6, 0) };
            var name = new TextBlock { Text = a.Name, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
            mid.Children.Add(name);
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = Math.Round(a.Volume * 100) };
            slider.SetResourceReference(StyleProperty, "BrimSlider");
            slider.ValueChanged += (_, e) => { a.Volume = (float)(e.NewValue / 100); if (a.Muted) a.Muted = false; };
            mid.Children.Add(slider);
            Grid.SetColumn(mid, 1);
            row.Children.Add(mid);

            Button? mute = null;
            mute = IconBtn(a.Muted ? "" : "", () => { a.Muted = !a.Muted; mute!.Content = a.Muted ? "" : ""; }, "Mute " + a.Name);
            mute.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(mute, 2);
            row.Children.Add(mute);
            apps.Children.Add(row);
        }
    }

    void ToggleDevices()
    {
        devices.Visibility = devices.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (devices.Visibility == Visibility.Visible) FillDevices();
    }

    void FillDevices()
    {
        devices.Children.Clear();
        foreach (var d in Audio.Outputs())
        {
            var row = new Grid { Margin = new Thickness(10, 0, 10, 0), Height = 34 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var check = new TextBlock { Text = d.IsDefault ? "" : "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            check.FontFamily = (FontFamily)Application.Current.FindResource("Brim.Icons");
            check.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Accent");
            row.Children.Add(check);
            var name = new TextBlock { Text = d.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var btn = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            btn.SetResourceReference(StyleProperty, "Tile");
            var id = d.Id;
            btn.Click += (_, _) => { Audio.SetDefaultOutput(id); FillDevices(); };
            devices.Children.Add(btn);
        }
    }

    /// <summary>A Windows 11 style toggle tile: click to switch, the corner arrow opens the related settings.</summary>
    sealed class Tile : Border
    {
        readonly TextBlock glyph = new() { FontSize = 15 };
        readonly TextBlock label = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock sub = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.75 };
        readonly Func<bool, System.Threading.Tasks.Task> toggle;
        bool on, toggleable = true;

        public Tile(string icon, string name, Func<bool, System.Threading.Tasks.Task> toggle, Action more)
        {
            this.toggle = toggle;
            Height = 64;
            Margin = new Thickness(4);
            CornerRadius = new CornerRadius(12);
            ClipToBounds = true;
            glyph.Text = icon;
            glyph.FontFamily = (FontFamily)Application.Current.FindResource("Brim.Icons");
            label.Text = name;

            // Two separate halves, like Windows 11: the big left side switches it on/off,
            // the full-height strip on the right (behind a divider) opens the settings for it.
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

            var toggleArea = new Border { Background = Brushes.Transparent, Padding = new Thickness(11, 9, 4, 9), Cursor = Cursors.Hand };
            var left = new Grid();
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            text.Children.Add(label);
            text.Children.Add(sub);
            left.Children.Add(glyph);
            left.Children.Add(text);
            toggleArea.Child = left;
            g.Children.Add(toggleArea);

            var moreArea = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "More options", BorderThickness = new Thickness(1, 0, 0, 0) };
            moreArea.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
            var chevron = new TextBlock { Text = "", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            chevron.FontFamily = glyph.FontFamily;
            chevron.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(TextBlock.Foreground)) { Source = label });
            moreArea.Child = chevron;
            Grid.SetColumn(moreArea, 1);
            g.Children.Add(moreArea);
            Child = g;

            var hover = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            toggleArea.MouseEnter += (_, _) => toggleArea.Background = hover;
            toggleArea.MouseLeave += (_, _) => toggleArea.Background = Brushes.Transparent;
            moreArea.MouseEnter += (_, _) => moreArea.Background = hover;
            moreArea.MouseLeave += (_, _) => moreArea.Background = Brushes.Transparent;
            moreArea.MouseLeftButtonUp += (_, e) => { e.Handled = true; more(); };
            toggleArea.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                if (!toggleable) { more(); return; }
                on = !on;
                Paint();
                await toggle(on);
            };
            Paint();
        }

        static bool IsInside(DependencyObject d, DependencyObject target)
        {
            while (d != null) { if (d == target) return true; d = VisualTreeHelper.GetParent(d); }
            return false;
        }

        public void Set(bool isOn, string status, string icon)
        {
            toggleable = true;
            on = isOn;
            sub.Text = status;
            glyph.Text = icon;
            Opacity = 1;
            Paint();
        }

        public void SetInfo(string icon, string status, string name)
        {
            toggleable = false;
            on = false;
            glyph.Text = icon;
            sub.Text = status;
            label.Text = name;
            Paint();
        }

        void Paint()
        {
            SetResourceReference(BackgroundProperty, on ? "Brim.Accent" : "Brim.Raised");
            var fg = on ? "Brim.OnAccent" : "Brim.Text";
            glyph.SetResourceReference(TextBlock.ForegroundProperty, fg);
            label.SetResourceReference(TextBlock.ForegroundProperty, fg);
            sub.SetResourceReference(TextBlock.ForegroundProperty, fg);
        }
    }
}

/// <summary>Notification-area icons, shown by Brim instead of Windows' taskbar.</summary>
public sealed class TrayPanel : StackPanel
{
    readonly WrapPanel icons = new() { MaxWidth = 5 * 44 };
    readonly TrayService? tray = TrayService.Instance;

    public TrayPanel()
    {
        var head = new Grid { Margin = new Thickness(4, 0, 0, 8) };
        var title = new TextBlock { Text = "Running in the background", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        head.Children.Add(title);
        Children.Add(head);
        Children.Add(icons);
        MinWidth = 220;

        if (tray == null)
        {
            Children.Add(Note("Tray icons show here while the Brim dock is on."));
            return;
        }
        tray.Prune();
        Rebuild();
        tray.Icons.CollectionChanged += OnChanged;
        Unloaded += (_, _) => tray.Icons.CollectionChanged -= OnChanged;
    }

    static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 220, Margin = new Thickness(4, 4, 4, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        return t;
    }

    void OnChanged(object? s, NotifyCollectionChangedEventArgs e) => Rebuild();

    void Rebuild()
    {
        icons.Children.Clear();
        foreach (var item in tray!.Icons.Where(i => !i.Hidden))
        {
            var img = new Image { Width = 20, Height = 20 };
            img.SetBinding(Image.SourceProperty, new Binding(nameof(TrayIconItem.Icon)) { Source = item });
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var cell = new Border { Width = 40, Height = 40, Margin = new Thickness(2), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Child = img, Cursor = Cursors.Hand };
            cell.SetBinding(ToolTipProperty, new Binding(nameof(TrayIconItem.Tip)) { Source = item });
            cell.MouseEnter += (_, _) => cell.SetResourceReference(Border.BackgroundProperty, "Brim.Hover");
            cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
            // Close this panel first and let focus settle, then pass the click on, so the app's own menu or window
            // is the last thing to take focus (otherwise it closes again straight away)
            async void Forward(bool right, bool dbl)
            {
                Native.AllowSetForegroundWindow(item.Pid);
                if (right)
                {
                    // Right-click: the app's menu opens over this panel, and the panel stays open behind it
                    Flyout.HoldForMenu(new IntPtr(item.HWnd), item.Pid);
                    TrayService.Click(item, true);
                    return;
                }
                // Left-click usually opens the app's window: close the panel first and let focus settle
                Flyout.CloseOpen();
                await System.Threading.Tasks.Task.Delay(120);
                TrayService.Click(item, false, dbl);
            }
            cell.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { e.Handled = true; Forward(false, true); } };
            cell.MouseLeftButtonUp += (_, e) => { if (!e.Handled) Forward(false, false); };
            cell.MouseRightButtonUp += (_, e) => { e.Handled = true; Forward(true, false); };
            icons.Children.Add(cell);
        }
        if (icons.Children.Count == 0 && Children.Count == 2) Children.Add(Note("No apps have tray icons right now."));
    }
}
