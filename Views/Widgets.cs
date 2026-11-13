using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Views;

public interface IWidget
{
    void Tick();
}

/// <summary>How a widget card is painted.</summary>
public sealed record WidgetLook(string Name, string Bg, string Stroke, string Text, string Dim, string Track, bool Blur, bool Shadow, double StrokeWidth = 1);

public static class WidgetStyles
{
    public static readonly WidgetLook[] Looks =
    {
        new("Glass",   "#47101014", "#38FFFFFF", "#FFFFFFFF", "#C8FFFFFF", "#33FFFFFF", true,  false),
        new("Frost",   "#8CF4F4F8", "#80FFFFFF", "#FF141418", "#B3141418", "#26000000", true,  false),
        new("Tinted",  "#8C0E0E12", "#2EFFFFFF", "#FFF5F5F7", "#B3F5F5F7", "#33FFFFFF", false, false),
        new("Solid",   "",          "#1AFFFFFF", "",          "",          "#33FFFFFF", false, false),
        new("Light",   "#F2F7F7FA", "#33FFFFFF", "#FF16161A", "#FF5E5E66", "#26000000", false, false),
        new("Outline", "#01000000", "#B3FFFFFF", "#FFFFFFFF", "#D9FFFFFF", "#40FFFFFF", false, true, 1.5),
        new("Bare",    "#01000000", "#00000000", "#FFFFFFFF", "#D9FFFFFF", "#40FFFFFF", false, true, 0),
    };

    public static readonly (string name, string family)[] Fonts =
    {
        ("Default", "Segoe UI Variable Display, Segoe UI"),
        ("Modern", "Bahnschrift, Segoe UI"),
        ("Serif", "Georgia, Cambria, Times New Roman"),
        ("Mono", "Cascadia Mono, Consolas"),
        ("Hand", "Ink Free, Segoe Print"),
        ("Thin", "Segoe UI Light, Segoe UI"),
    };

    public static readonly (string name, double scale)[] Sizes = { ("S", 0.85), ("M", 1), ("L", 1.2), ("XL", 1.45) };
    public static readonly (string name, double radius)[] Corners = { ("Sharp", 6), ("Round", 22), ("Extra round", 34) };
    public static readonly string[] TextColors = { "#FFFFFF", "#16161A", "#FFD6A5", "#FFADAD", "#CAFFBF", "#9BF6FF", "#BDB2FF", "#FDFFB6" };

    public static WidgetLook For(WidgetItem item)
    {
        var name = item.Look ?? SettingsStore.Current.WidgetStyle;
        return Looks.FirstOrDefault(l => l.Name == name) ?? Looks[0];
    }

    public static string NameOf(WidgetItem item) =>
        string.Join(" + ", item.AllKinds.Select(WidgetFactory.NameOf));
}

/// <summary>A borderless window on the desktop (always below other windows) holding one widget or a combined card.</summary>
public sealed class WidgetWindow : Window
{
    public WidgetItem Item { get; }
    public string Signature { get; }
    readonly List<FrameworkElement> parts = new();
    readonly Border frame;
    readonly WidgetLook look;
    Backdrop? backdrop;
    IntPtr hwnd;
    static double nextX = double.NaN, nextY = double.NaN, columnWidth;

    static AppSettings S => SettingsStore.Current;
    static App AppRef => (App)Application.Current;

    public WidgetWindow(WidgetItem item)
    {
        Item = item;
        Signature = item.Signature + "|" + S.WidgetStyle + "|" + S.Theme;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        Topmost = false;
        UseLayoutRounding = true;
        Title = "Brim widget";

        var fontFamily = WidgetStyles.Fonts.FirstOrDefault(f => f.name == item.Font).family ?? WidgetStyles.Fonts[0].family;
        // The Headline is modelled on a wide geometric font; Bahnschrift is Windows' closest match
        if (item.Font == "Default" && item.AllKinds.Count == 1 && item.Kind == WidgetKind.Headline) fontFamily = "Bahnschrift Light, Bahnschrift, Segoe UI";
        FontFamily = new FontFamily(fontFamily);

        look = WidgetStyles.For(item);
        var palette = ThemeManager.Current;
        string text = item.TextColor ?? (look.Text.Length > 0 ? look.Text : palette.Text);
        var textColor = ThemeManager.Parse(text);
        Resources["Brim.WidgetText"] = new SolidColorBrush(textColor);
        Resources["Brim.WidgetDim"] = item.TextColor != null
            ? new SolidColorBrush(Color.FromArgb(185, textColor.R, textColor.G, textColor.B))
            : new SolidColorBrush(ThemeManager.Parse(look.Dim.Length > 0 ? look.Dim : palette.Dim));
        Resources["Brim.Track"] = new SolidColorBrush(ThemeManager.Parse(look.Track));

        // A lone Day widget reads best without a card unless a look was picked for it
        bool lonelyDay = item.AllKinds.Count == 1 && item.Kind is WidgetKind.Day or WidgetKind.Headline or WidgetKind.Custom && item.Look == null;
        var bg = lonelyDay ? "#01000000" : look.Bg.Length > 0 ? look.Bg : (palette.Surface == "#FF000000" ? "#FF141417" : palette.Surface);

        FrameworkElement content = BuildContent(item);
        if (look.Shadow || lonelyDay)
            content.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.45, Color = Colors.Black };

        frame = new Border
        {
            CornerRadius = new CornerRadius(item.Radius),
            Padding = lonelyDay ? new Thickness(8) : new Thickness(20, 16, 20, 18),
            BorderThickness = new Thickness(lonelyDay ? 0 : look.StrokeWidth),
            Background = new SolidColorBrush(ThemeManager.Parse(bg)),
            BorderBrush = new SolidColorBrush(ThemeManager.Parse(look.Stroke)),
            Child = content,
            Margin = new Thickness(6),
            LayoutTransform = Math.Abs(item.Scale - 1) > 0.01 ? new ScaleTransform(item.Scale, item.Scale) : null,
        };
        Content = frame;

        MouseLeftButtonDown += OnDown;
        MouseRightButtonUp += OnRight;
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, false);
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            if (look.Blur && !lonelyDay) backdrop = new Backdrop();
        };
        Loaded += (_, _) => { PlaceInitially(); Tick(); if (Raised) ApplyRaised(); else SyncBackdrop(true); };
        LocationChanged += (_, _) => SyncBackdrop(false);
        frame.LayoutUpdated += (_, _) => SyncBackdrop(false);
        // Clicking or dragging a raised widget brings it to the very top; keep the notch and dock above it
        Activated += (_, _) => { if (Raised) WentOnTop?.Invoke(); };
        App.SecondTick += Tick;
        OledCare.Shift += OnOledShift;
        OledCare.Dim += OnOledDim;
        Closed += (_, _) =>
        {
            App.SecondTick -= Tick;
            OledCare.Shift -= OnOledShift;
            OledCare.Dim -= OnOledDim;
            backdrop?.Dispose();
            backdrop = null;
        };
    }

    FrameworkElement BuildContent(WidgetItem item)
    {
        var kinds = item.AllKinds;
        if (kinds.Count == 1)
        {
            var single = WidgetFactory.Create(kinds[0], item);
            parts.Add(single);
            return single;
        }
        bool row = item.Layout == "Row";
        var panel = new StackPanel { Orientation = row ? Orientation.Horizontal : Orientation.Vertical };
        for (int i = 0; i < kinds.Count; i++)
        {
            if (i > 0)
            {
                var line = new Border
                {
                    Width = row ? 1 : double.NaN,
                    Height = row ? double.NaN : 1,
                    Margin = row ? new Thickness(20, 4, 20, 4) : new Thickness(0, 16, 0, 16),
                };
                line.SetResourceReference(Border.BackgroundProperty, "Brim.Track");
                panel.Children.Add(line);
            }
            var part = WidgetFactory.Create(kinds[i], item);
            part.VerticalAlignment = VerticalAlignment.Center;
            parts.Add(part);
            panel.Children.Add(part);
        }
        return panel;
    }

    void Tick()
    {
        foreach (var p in parts) (p as IWidget)?.Tick();
    }

    // OLED care: drift a few pixels over time, fade when you're away
    void OnOledShift(double dx, double dy)
    {
        frame.RenderTransform = new TranslateTransform(dx, dy);
        SyncBackdrop(true);
    }

    void OnOledDim(bool dim) =>
        frame.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(dim ? 0.35 : 1, TimeSpan.FromMilliseconds(dim ? 1500 : 250)));

    void SyncBackdrop(bool force)
    {
        if (backdrop == null || hwnd == IntPtr.Zero || !IsVisible || PresentationSource.FromVisual(frame) == null) return;
        var tl = frame.PointToScreen(new Point(0, 0));
        var br = frame.PointToScreen(new Point(frame.ActualWidth, frame.ActualHeight));
        double scale = (br.X - tl.X) / Math.Max(1, frame.ActualWidth);
        backdrop.Place(hwnd, (int)Math.Round(tl.X), (int)Math.Round(tl.Y), (int)Math.Round(br.X - tl.X), (int)Math.Round(br.Y - tl.Y),
            (int)Math.Round(Item.Radius * scale), false, force);
    }

    public static void ResetCascade() { nextX = double.NaN; nextY = double.NaN; columnWidth = 0; }

    /// <summary>While the desktop is shown, widgets sit above it instead of at the bottom of the stack.</summary>
    public static bool Raised { get; set; }

    /// <summary>
    /// A widget just went into the top layer (raised over the desktop, or clicked/dragged there). The notch and dock
    /// live in that layer too and must stay above every widget, so they're put back on top straight after.
    /// </summary>
    public static event Action? WentOnTop;

    public void ApplyRaised()
    {
        if (hwnd == IntPtr.Zero) return;
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        if (Raised) { Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, flags); WentOnTop?.Invoke(); }
        else
        {
            Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, flags);
        }
        SyncBackdrop(true);
    }

    void PlaceInitially()
    {
        var wa = SystemParameters.WorkArea;
        if (!double.IsNaN(Item.X) && !double.IsNaN(Item.Y))
        {
            Left = Math.Clamp(Item.X, wa.Left - ActualWidth + 40, wa.Right - 40);
            Top = Math.Clamp(Item.Y, wa.Top, wa.Bottom - 40);
            return;
        }
        if (Item.AllKinds.Count == 1 && Item.Kind == WidgetKind.Headline)
        {
            // Centred across the top, just under the notch
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 64;
            Item.X = Left;
            Item.Y = Top;
            SettingsStore.Save();
            return;
        }
        if (double.IsNaN(nextX)) { nextX = wa.Right - 32; nextY = wa.Top + 72; columnWidth = 0; }
        if (nextY + ActualHeight > wa.Bottom - 24)
        {
            nextX -= columnWidth + 16;
            nextY = wa.Top + 72;
            columnWidth = 0;
        }
        Left = nextX - ActualWidth;
        Top = nextY;
        nextY += ActualHeight + 8;
        columnWidth = Math.Max(columnWidth, ActualWidth);
        Item.X = Left;
        Item.Y = Top;
        SettingsStore.Save();
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (S.WidgetsLocked) return;
        if (e.OriginalSource is DependencyObject d && (FindParent<TextBox>(d) != null || FindParent<ButtonBase>(d) != null)) return;
        try { DragMove(); } catch { return; }
        Item.X = Left;
        Item.Y = Top;
        SettingsStore.Save();
        SyncBackdrop(true);
    }

    void OnRight(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<TextBox>(d) != null) return;
        e.Handled = true;
        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("Customise…", () => AppRef.OpenSettings("Widgets", Item.Id)));

        var others = S.Widgets.Where(w => w.Id != Item.Id).ToList();
        if (others.Count > 0)
        {
            var combine = new MenuItem { Header = "Combine with" };
            foreach (var other in others)
            {
                var o = other;
                combine.Items.Add(MakeItem(WidgetStyles.NameOf(o), () => WidgetHost.Combine(Item, o)));
            }
            menu.Items.Add(combine);
        }
        if (Item.AllKinds.Count > 1) menu.Items.Add(MakeItem("Split apart", () => WidgetHost.Split(Item)));

        menu.Items.Add(MakeItem(S.WidgetsLocked ? "Unlock widgets" : "Lock widgets in place", () =>
        {
            S.WidgetsLocked = !S.WidgetsLocked;
            SettingsStore.Save();
            AppRef.RefreshSettingsWindow();
        }));
        menu.Items.Add(MakeItem("Remove widget", () =>
        {
            S.Widgets.RemoveAll(w => w.Id == Item.Id);
            SettingsStore.Save();
            AppRef.ApplyAll();
            AppRef.RefreshSettingsWindow();
        }));
        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    static MenuItem MakeItem(string header, Action a)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => a();
        return mi;
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_WINDOWPOSCHANGING && !Raised)
        {
            // Pin to the bottom of the stack so widgets sit on the wallpaper, under every app
            var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(l);
            wp.hwndInsertAfter = Native.HWND_BOTTOM;
            wp.flags &= ~Native.SWP_NOZORDER;
            Marshal.StructureToPtr(wp, l, false);
        }
        else if (msg == 0x0047 /* WM_WINDOWPOSCHANGED */)
        {
            // Keep the blur layer tucked directly underneath
            Dispatcher.BeginInvoke(new Action(() => SyncBackdrop(true)), DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }
}

public sealed class WidgetHost
{
    readonly Dictionary<string, WidgetWindow> windows = new();
    static AppSettings S => SettingsStore.Current;

    public void Sync()
    {
        var current = S.Widgets.ToDictionary(w => w.Id);
        foreach (var (id, win) in windows.ToList())
        {
            // Gone, or its look changed: rebuild it
            if (!current.TryGetValue(id, out var item) || win.Signature != item.Signature + "|" + S.WidgetStyle + "|" + S.Theme)
            {
                win.Close();
                windows.Remove(id);
            }
        }
        foreach (var item in S.Widgets)
        {
            if (windows.ContainsKey(item.Id)) continue;
            var w = new WidgetWindow(item);
            windows[item.Id] = w;
            w.Show();
        }
    }

    /// <summary>Fold <paramref name="other"/> into <paramref name="target"/> as one card.</summary>
    public static void Combine(WidgetItem target, WidgetItem other)
    {
        var kinds = target.AllKinds.Concat(other.AllKinds).ToList();
        target.Parts = kinds;
        target.Text ??= other.Text;
        S.Widgets.RemoveAll(w => w.Id == other.Id);
        SettingsStore.Save();
        var app = (App)Application.Current;
        app.ApplyAll();
        app.RefreshSettingsWindow();
    }

    public static void Split(WidgetItem item)
    {
        var kinds = item.AllKinds.ToList();
        item.Parts = null;
        item.Kind = kinds[0];
        double y = item.Y;
        foreach (var k in kinds.Skip(1))
        {
            y = double.IsNaN(y) ? double.NaN : y + 40;
            S.Widgets.Add(new WidgetItem
            {
                Kind = k, X = double.IsNaN(item.X) ? double.NaN : item.X + 40, Y = y,
                Look = item.Look, Font = item.Font, TextColor = item.TextColor, Scale = item.Scale, Radius = item.Radius,
                Text = k == WidgetKind.Note ? item.Text : null,
            });
        }
        SettingsStore.Save();
        var app = (App)Application.Current;
        app.ApplyAll();
        app.RefreshSettingsWindow();
    }

    /// <summary>Lift widgets above a shown desktop, or put them back on the bottom when an app comes forward.</summary>
    public void CheckDesktop()
    {
        bool raised = windows.Count > 0 && Native.DesktopRaised();
        if (raised == WidgetWindow.Raised) return;
        WidgetWindow.Raised = raised;
        foreach (var w in windows.Values) w.ApplyRaised();
    }

    public void ResetPositions()
    {
        CloseAll();
        foreach (var w in S.Widgets) { w.X = double.NaN; w.Y = double.NaN; }
        WidgetWindow.ResetCascade();
        Sync();
    }

    public void CloseAll()
    {
        foreach (var w in windows.Values) w.Close();
        windows.Clear();
    }
}

public static class WidgetFactory
{
    public static readonly (WidgetKind kind, string name, string about)[] Catalog =
    {
        (WidgetKind.Headline, "Headline", "Day, date and time across the top of the screen, with the song underneath"),
        (WidgetKind.Clock, "Clock", "Big time and date"),
        (WidgetKind.Day, "Day", "The weekday in wide letters"),
        (WidgetKind.Calendar, "Calendar", "This month at a glance"),
        (WidgetKind.Music, "Now playing", "Cover, song and controls"),
        (WidgetKind.Weather, "Weather", "Needs a city (below)"),
        (WidgetKind.WorldClock, "World clock", "Time and weather in other cities"),
        (WidgetKind.Stats, "PC stats", "CPU, memory and battery"),
        (WidgetKind.Progress, "Day progress", "How far through the day, week, month and year"),
        (WidgetKind.Note, "Quick note", "A sticky note that saves as you type"),
    };

    public static FrameworkElement Create(WidgetKind kind, WidgetItem item) => kind switch
    {
        WidgetKind.Clock => new ClockWidget(),
        WidgetKind.Day => new DayWidget(),
        WidgetKind.Calendar => new CalendarWidget(),
        WidgetKind.Music => new MusicWidget(),
        WidgetKind.Stats => new StatsWidget(),
        WidgetKind.Weather => new WeatherWidget(),
        WidgetKind.WorldClock => new WorldClockWidget(item),
        WidgetKind.Note => new NoteWidget(item),
        WidgetKind.Progress => new ProgressWidget(),
        WidgetKind.Headline => new HeadlineWidget(),
        WidgetKind.Custom => new CustomTextWidget(item),
        _ => new ClockWidget(),
    };

    public static string NameOf(WidgetKind k) => k == WidgetKind.Custom ? "Custom" : Catalog.FirstOrDefault(c => c.kind == k).name ?? k.ToString();

    public static TextBlock T(string text, double size, bool dim = false, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, dim ? "Brim.WidgetDim" : "Brim.WidgetText");
        return t;
    }

    public static ProgressBar Bar()
    {
        var b = new ProgressBar { Maximum = 100, Margin = new Thickness(0, 5, 0, 0) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "ThinBar");
        return b;
    }

    public static string Time(DateTime t) => t.ToString(SettingsStore.Current.Clock24 ? "HH:mm" : "h:mm");
}


sealed class ClockWidget : StackPanel, IWidget
{
    readonly TextBlock time = WidgetFactory.T("", 64, weight: FontWeights.Light);
    readonly TextBlock ampm = WidgetFactory.T("", 18, true);
    readonly TextBlock date = WidgetFactory.T("", 14, true);

    public ClockWidget()
    {
        MinWidth = 200;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        time.Margin = new Thickness(0, -10, 0, -6);
        ampm.VerticalAlignment = VerticalAlignment.Bottom;
        ampm.Margin = new Thickness(6, 0, 0, 10);
        row.Children.Add(time);
        row.Children.Add(ampm);
        Children.Add(row);
        Children.Add(date);
    }

    public void Tick()
    {
        var now = DateTime.Now;
        time.Text = WidgetFactory.Time(now);
        ampm.Text = SettingsStore.Current.Clock24 ? "" : now.ToString("tt");
        date.Text = now.ToString("dddd, d MMMM");
    }
}

sealed class DayWidget : StackPanel, IWidget
{
    readonly TextBlock day = WidgetFactory.T("", 76, weight: FontWeights.Light);
    readonly TextBlock sub = WidgetFactory.T("", 16, true);

    public DayWidget()
    {
        day.HorizontalAlignment = HorizontalAlignment.Center;
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        sub.Margin = new Thickness(0, 2, 0, 0);
        Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black };
        Children.Add(day);
        Children.Add(sub);
    }

    public void Tick()
    {
        var now = DateTime.Now;
        // WPF has no letter-spacing, so space the letters out by hand
        day.Text = string.Join("  ", now.ToString("dddd").ToUpperInvariant().ToCharArray());
        sub.Text = $"{now:d MMMM yyyy}  ·  {WidgetFactory.Time(now)}";
    }
}

sealed class CalendarWidget : StackPanel, IWidget
{
    readonly TextBlock header = WidgetFactory.T("", 15, weight: FontWeights.SemiBold);
    readonly UniformGrid grid = new() { Columns = 7 };
    DateTime shown;

    public CalendarWidget()
    {
        Width = 252;
        Children.Add(header);
        grid.Margin = new Thickness(0, 10, 0, 0);
        Children.Add(grid);
    }

    public void Tick()
    {
        var today = DateTime.Today;
        if (today == shown) return;
        shown = today;
        header.Text = today.ToString("MMMM yyyy");
        grid.Children.Clear();

        var culture = CultureInfo.CurrentCulture;
        var firstDow = culture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)(((int)firstDow + i) % 7);
            var t = WidgetFactory.T(culture.DateTimeFormat.GetShortestDayName(dow), 11, true);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 0, 0, 6);
            grid.Children.Add(t);
        }
        var first = new DateTime(today.Year, today.Month, 1);
        int offset = ((int)first.DayOfWeek - (int)firstDow + 7) % 7;
        for (int i = 0; i < offset; i++) grid.Children.Add(new Border());
        int days = DateTime.DaysInMonth(today.Year, today.Month);
        for (int d = 1; d <= days; d++)
        {
            var cell = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(1) };
            var t = WidgetFactory.T(d.ToString(), 12);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            if (d == today.Day)
            {
                cell.SetResourceReference(Border.BackgroundProperty, "Brim.Accent");
                t.SetResourceReference(TextBlock.ForegroundProperty, "Brim.OnAccent");
                t.FontWeight = FontWeights.SemiBold;
            }
            cell.Child = t;
            grid.Children.Add(cell);
        }
    }
}

sealed class MusicWidget : Grid, IWidget
{
    readonly Border art = new() { Width = 76, Height = 76, CornerRadius = new CornerRadius(14) };
    readonly ImageBrush artBrush = new() { Stretch = Stretch.UniformToFill };
    readonly TextBlock title = WidgetFactory.T("Nothing playing", 15, weight: FontWeights.SemiBold);
    readonly TextBlock artist = WidgetFactory.T("", 12.5, true);
    readonly ProgressBar progress = WidgetFactory.Bar();
    readonly Button play;

    static MediaService M => MediaService.Instance;

    public MusicWidget()
    {
        Width = 330;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        art.SetResourceReference(Border.BackgroundProperty, "Brim.Track");
        var artGrid = new Grid();
        artGrid.Children.Add(art);
        var artFill = new Border { CornerRadius = new CornerRadius(14), Background = artBrush };
        artGrid.Children.Add(artFill);
        Children.Add(artGrid);

        var right = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        SetColumn(right, 1);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        artist.TextTrimming = TextTrimming.CharacterEllipsis;
        artist.Margin = new Thickness(0, 1, 0, 0);
        right.Children.Add(title);
        right.Children.Add(artist);
        progress.Margin = new Thickness(0, 9, 0, 4);
        progress.Height = 3;
        right.Children.Add(progress);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 0, 0, 0) };
        controls.Children.Add(Btn("", () => M.Previous()));
        play = Btn("", () => M.PlayPause());
        controls.Children.Add(play);
        controls.Children.Add(Btn("", () => M.Next()));
        right.Children.Add(controls);
        Children.Add(right);

        M.Changed += Update;
        Unloaded += (_, _) => M.Changed -= Update;
        Update();
    }

    static Button Btn(string glyph, Action a)
    {
        var b = new Button { Content = glyph, Width = 32, Height = 30 };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.SetResourceReference(Control.ForegroundProperty, "Brim.WidgetText");
        b.Click += (_, _) => a();
        return b;
    }

    void Update()
    {
        bool has = M.HasSession && !string.IsNullOrEmpty(M.Title);
        title.Text = has ? M.Title : "Nothing playing";
        artist.Text = has ? M.Artist : "";
        artBrush.ImageSource = M.Art;
        play.Content = M.IsPlaying ? "" : "";
        Tick();
    }

    public void Tick()
    {
        progress.Value = M.Duration.TotalSeconds > 0 ? M.Position.TotalSeconds / M.Duration.TotalSeconds * 100 : 0;
        progress.Visibility = M.HasSession && M.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Hidden;
    }
}

sealed class StatsWidget : StackPanel, IWidget
{
    readonly List<(TextBlock value, ProgressBar bar, Func<(string, double)?> read)> rows = new();

    public StatsWidget()
    {
        Width = 220;
        Add("CPU", () => ($"{SystemInfo.Cpu:0}%", SystemInfo.Cpu));
        Add("Memory", () => ($"{SystemInfo.RamUsedGb:0.0} / {SystemInfo.RamTotalGb:0} GB", SystemInfo.Ram));
        Add("Battery", () => SystemInfo.HasBattery
            ? ($"{SystemInfo.BatteryPercent}%{(SystemInfo.Charging ? " · charging" : "")}", SystemInfo.BatteryPercent)
            : null);
        Add("Uptime", () => (Uptime(), double.NaN));
    }

    static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{t.Hours}h {t.Minutes}m";
    }

    void Add(string label, Func<(string, double)?> read)
    {
        var row = new StackPanel { Margin = new Thickness(0, rows.Count == 0 ? 0 : 11, 0, 0) };
        var top = new Grid();
        top.Children.Add(WidgetFactory.T(label, 12, true));
        var value = WidgetFactory.T("", 12);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(value);
        row.Children.Add(top);
        var bar = WidgetFactory.Bar();
        row.Children.Add(bar);
        Children.Add(row);
        rows.Add((value, bar, read));
    }

    public void Tick()
    {
        foreach (var (value, bar, read) in rows)
        {
            var r = read();
            var parent = (FrameworkElement)value.Parent;
            var row = (FrameworkElement)parent.Parent;
            if (r == null) { row.Visibility = Visibility.Collapsed; continue; }
            row.Visibility = Visibility.Visible;
            value.Text = r.Value.Item1;
            if (double.IsNaN(r.Value.Item2)) bar.Visibility = Visibility.Collapsed;
            else bar.Value = r.Value.Item2;
        }
    }
}

/// <summary>Time and weather in other cities, side by side.</summary>
sealed class WorldClockWidget : StackPanel, IWidget
{
    sealed class Column
    {
        public CityItem City = null!;
        public TimeZoneInfo? Zone;
        public WeatherService.CityNow? Weather;
        public TextBlock Name = null!, Time = null!, Ampm = null!, When = null!, Glyph = null!, Temp = null!;
    }

    readonly List<Column> columns = new();
    int seconds;

    public WorldClockWidget(WidgetItem item)
    {
        var cities = item.Cities ?? new List<CityItem>();
        if (cities.Count == 0)
        {
            MinWidth = 220;
            Children.Add(WidgetFactory.T("World clock", 16, weight: FontWeights.SemiBold));
            var hint = WidgetFactory.T("Add cities: right-click → Widget settings", 12, true);
            hint.Margin = new Thickness(0, 4, 0, 0);
            Children.Add(hint);
            return;
        }

        var row = new StackPanel { Orientation = cities.Count > 2 ? Orientation.Vertical : Orientation.Horizontal };
        for (int i = 0; i < cities.Count; i++)
        {
            var c = cities[i];
            var col = new Column { City = c };
            try { if (c.TimeZone.Length > 0) col.Zone = TimeZoneInfo.FindSystemTimeZoneById(c.TimeZone); } catch { }

            var panel = new StackPanel { MinWidth = 150 };
            col.Name = WidgetFactory.T(c.Name.Split(',')[0], 13, true, FontWeights.SemiBold);
            col.Name.TextTrimming = TextTrimming.CharacterEllipsis;
            col.Name.MaxWidth = 170;
            col.Name.ToolTip = c.Name;
            panel.Children.Add(col.Name);

            var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, -2, 0, 0) };
            col.Time = WidgetFactory.T("", 40, weight: FontWeights.Light);
            col.Ampm = WidgetFactory.T("", 13, true);
            col.Ampm.VerticalAlignment = VerticalAlignment.Bottom;
            col.Ampm.Margin = new Thickness(4, 0, 0, 9);
            timeRow.Children.Add(col.Time);
            timeRow.Children.Add(col.Ampm);
            panel.Children.Add(timeRow);

            col.When = WidgetFactory.T("", 11.5, true);
            panel.Children.Add(col.When);

            var weather = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            col.Glyph = new TextBlock { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)Application.Current.FindResource("Brim.Symbols") };
            col.Glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brim.WidgetText");
            col.Temp = WidgetFactory.T("", 13);
            col.Temp.VerticalAlignment = VerticalAlignment.Center;
            col.Temp.Margin = new Thickness(6, 0, 0, 0);
            weather.Children.Add(col.Glyph);
            weather.Children.Add(col.Temp);
            panel.Children.Add(weather);

            if (i > 0)
            {
                // A thin line between cities
                var line = new Border { Opacity = 0.25 };
                line.SetResourceReference(Border.BackgroundProperty, "Brim.WidgetText");
                if (row.Orientation == Orientation.Horizontal) { line.Width = 1; line.Margin = new Thickness(18, 4, 18, 4); }
                else { line.Height = 1; line.Margin = new Thickness(0, 12, 0, 12); }
                row.Children.Add(line);
            }
            row.Children.Add(panel);
            columns.Add(col);
        }
        Children.Add(row);
        Tick();
        RefreshWeather();
    }

    async void RefreshWeather()
    {
        foreach (var col in columns)
        {
            col.Weather = await WeatherService.ForCity(col.City);
            if (col.Weather != null)
            {
                col.Glyph.Text = col.Weather.Glyph;
                col.Temp.Text = $"{Math.Round(col.Weather.Temp)}°  {col.Weather.Description}";
            }
        }
        Tick();
    }

    DateTime LocalTime(Column col)
    {
        var utc = DateTime.UtcNow;
        if (col.Zone != null) return TimeZoneInfo.ConvertTimeFromUtc(utc, col.Zone);
        if (col.Weather != null) return utc.AddSeconds(col.Weather.UtcOffsetSeconds);
        return DateTime.Now;
    }

    public void Tick()
    {
        var here = DateTime.Now;
        bool h24 = SettingsStore.Current.Clock24;
        foreach (var col in columns)
        {
            var t = LocalTime(col);
            col.Time.Text = t.ToString(h24 ? "HH:mm" : "h:mm");
            col.Ampm.Text = h24 ? "" : t.ToString("tt");
            var diff = Math.Round((t - here).TotalHours * 2) / 2; // half-hour zones exist (India, Adelaide…)
            string day = t.Date == here.Date ? "Today" : t.Date > here.Date ? "Tomorrow" : "Yesterday";
            string offset = diff == 0 ? "same time as you" : (diff > 0 ? "+" : "−") + Math.Abs(diff).ToString("0.#") + "h";
            col.When.Text = $"{day}, {offset}";
        }
        if (++seconds % 1200 == 0) RefreshWeather(); // every 20 minutes
    }
}

sealed class WeatherWidget : Grid, IWidget
{
    readonly TextBlock glyph = new() { FontSize = 40 };
    readonly TextBlock temp = WidgetFactory.T("", 44, weight: FontWeights.Light);
    readonly TextBlock desc = WidgetFactory.T("", 14);
    readonly TextBlock place = WidgetFactory.T("", 12, true);
    readonly TextBlock range = WidgetFactory.T("", 12, true);

    public WeatherWidget()
    {
        Width = 230;
        glyph.FontFamily = (FontFamily)Application.Current.FindResource("Brim.Symbols");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brim.WidgetText");
        glyph.HorizontalAlignment = HorizontalAlignment.Right;
        glyph.VerticalAlignment = VerticalAlignment.Top;
        var stack = new StackPanel();
        temp.Margin = new Thickness(0, -8, 0, 0);
        stack.Children.Add(temp);
        stack.Children.Add(desc);
        range.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(range);
        place.Margin = new Thickness(0, 8, 0, 0);
        place.TextTrimming = TextTrimming.CharacterEllipsis;
        stack.Children.Add(place);
        Children.Add(stack);
        Children.Add(glyph);
        WeatherService.Updated += Update;
        Unloaded += (_, _) => WeatherService.Updated -= Update;
        Update();
        WeatherService.Refresh();
    }

    void Update()
    {
        var w = WeatherService.Current;
        if (w == null)
        {
            temp.Text = "--°";
            desc.Text = SettingsStore.Current.WeatherLat == null ? "Set your city" : "Loading…";
            range.Text = "";
            place.Text = SettingsStore.Current.WeatherLat == null ? "Right-click → Widget settings" : SettingsStore.Current.WeatherCity;
            glyph.Text = "";
            return;
        }
        temp.Text = $"{Math.Round(w.Temp)}°";
        desc.Text = w.Description;
        range.Text = $"H {Math.Round(w.High)}°   L {Math.Round(w.Low)}°";
        place.Text = w.Place;
        glyph.Text = w.Glyph;
    }

    int minutes;
    public void Tick()
    {
        if (++minutes % 60 == 0) WeatherService.Refresh();
    }
}

sealed class ProgressWidget : StackPanel, IWidget
{
    readonly List<(TextBlock value, ProgressBar bar, Func<double> read)> rows = new();

    public ProgressWidget()
    {
        Width = 230;
        Add("Today", () => DateTime.Now.TimeOfDay.TotalHours / 24);
        Add("This week", () =>
        {
            var now = DateTime.Now;
            int dow = ((int)now.DayOfWeek - (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
            return (dow + now.TimeOfDay.TotalHours / 24) / 7;
        });
        Add("This month", () =>
        {
            var now = DateTime.Now;
            return (now.Day - 1 + now.TimeOfDay.TotalHours / 24) / DateTime.DaysInMonth(now.Year, now.Month);
        });
        Add(DateTime.Now.Year.ToString(), () =>
        {
            var now = DateTime.Now;
            var start = new DateTime(now.Year, 1, 1);
            return (now - start).TotalDays / (start.AddYears(1) - start).TotalDays;
        });
    }

    void Add(string label, Func<double> read)
    {
        var row = new StackPanel { Margin = new Thickness(0, rows.Count == 0 ? 0 : 11, 0, 0) };
        var top = new Grid();
        top.Children.Add(WidgetFactory.T(label, 12, true));
        var value = WidgetFactory.T("", 12);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(value);
        row.Children.Add(top);
        var bar = WidgetFactory.Bar();
        row.Children.Add(bar);
        Children.Add(row);
        rows.Add((value, bar, read));
    }

    public void Tick()
    {
        foreach (var (value, bar, read) in rows)
        {
            double p = Math.Clamp(read(), 0, 1) * 100;
            value.Text = $"{p:0}%";
            bar.Value = p;
        }
    }
}

sealed class NoteWidget : StackPanel, IWidget
{
    readonly WidgetItem item;
    readonly DispatcherTimer save = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public NoteWidget(WidgetItem item)
    {
        this.item = item;
        Width = 240;
        Children.Add(WidgetFactory.T("Note", 12, true, FontWeights.SemiBold));
        var box = new TextBox
        {
            Text = item.Text ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 14,
            MinHeight = 120,
            MaxHeight = 320,
            Margin = new Thickness(-2, 6, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };
        box.SetResourceReference(TextBox.ForegroundProperty, "Brim.WidgetText");
        box.SetResourceReference(TextBox.CaretBrushProperty, "Brim.WidgetText");
        box.FocusVisualStyle = null;
        box.TextChanged += (_, _) => { item.Text = box.Text; save.Stop(); save.Start(); };
        save.Tick += (_, _) => { save.Stop(); SettingsStore.Save(); };
        Children.Add(box);
    }

    public void Tick() { }
}

/// <summary>
/// Text-only header in the style of the "castle w red flowers" wallpaper: | F R I D A Y | across the top,
/// the date and time under it, and the song and artist fading in underneath while music plays.
/// </summary>
sealed class HeadlineWidget : StackPanel, IWidget
{
    readonly TextBlock day = WidgetFactory.T("", 40, weight: FontWeights.Light);
    readonly TextBlock date = WidgetFactory.T("", 15);
    readonly TextBlock time = WidgetFactory.T("", 15, true);
    readonly TextBlock song = WidgetFactory.T("", 12.5, weight: FontWeights.Bold);
    readonly TextBlock artist = WidgetFactory.T("", 12.5, true);
    readonly StackPanel music = new() { Margin = new Thickness(0, 14, 0, 0), Opacity = 0 };

    static MediaService M => MediaService.Instance;

    public HeadlineWidget()
    {
        MinWidth = 420;
        foreach (var t in new[] { day, date, time, song, artist }) t.HorizontalAlignment = HorizontalAlignment.Center;
        // The day line keeps the window's chosen font; everything else is set small and quiet
        date.Margin = new Thickness(0, 6, 0, 0);
        time.Margin = new Thickness(0, 2, 0, 0);
        var mono = new FontFamily("Cascadia Mono, Consolas");
        song.FontFamily = mono;
        artist.FontFamily = mono;
        song.TextTrimming = artist.TextTrimming = TextTrimming.CharacterEllipsis;
        song.MaxWidth = artist.MaxWidth = 520;
        artist.Margin = new Thickness(0, 3, 0, 0);
        music.Children.Add(song);
        music.Children.Add(artist);
        Children.Add(day);
        Children.Add(date);
        Children.Add(time);
        Children.Add(music);
        M.Changed += UpdateMusic;
        Unloaded += (_, _) => M.Changed -= UpdateMusic;
        UpdateMusic();
    }

    void UpdateMusic()
    {
        bool show = M.HasSession && M.IsPlaying && !string.IsNullOrEmpty(M.Title);
        if (show)
        {
            song.Text = "\u266A " + M.Title;
            artist.Text = string.IsNullOrEmpty(M.Artist) ? "" : "\u203B " + M.Artist;
        }
        music.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(600)));
    }

    public void Tick()
    {
        var now = DateTime.Now;
        // Letter-spaced weekday between bars, like the wallpaper
        day.Text = "|  " + string.Join("\u2005", now.ToString("dddd").ToUpperInvariant().ToCharArray()) + "  |";
        date.Text = $"{now.Day}  {now.ToString("MMM").ToUpperInvariant()}  {now.Year}";
        time.Text = WidgetFactory.Time(now) + (SettingsStore.Current.Clock24 ? "" : " " + now.ToString("tt"));
    }
}

/// <summary>Fills {tokens} in designed widgets: {WEEKDAY}, {time}, {song}, {temp} and so on. UPPERCASE tokens give uppercase text.</summary>
public static class TextTokens
{
    public static readonly (string token, string meaning)[] Help =
    {
        ("{weekday}", "Friday"), ("{wd}", "Fri"), ("{day}", "13"), ("{month}", "November"), ("{mon}", "Nov"), ("{year}", "2026"),
        ("{date}", "13/11/2026"), ("{time}", "14:32"), ("{hour}", "14"), ("{minute}", "32"), ("{second}", "05"), ("{ampm}", "PM"),
        ("{greeting}", "Good afternoon"), ("{song}", "song title"), ("{artist}", "artist"), ("{player}", "Spotify"),
        ("{temp}", "13°"), ("{weather}", "Clear"), ("{high}", "21°"), ("{low}", "12°"), ("{city}", "your city"),
        ("{battery}", "78%"), ("{cpu}", "32%"), ("{ram}", "45%"), ("{uptime}", "5d 18h"),
    };

    /// <summary>{time} for here, {time@2} for the widget's second city.</summary>
    static readonly System.Text.RegularExpressions.Regex Token = new(@"\{(\w+)(?:@(\d))?\}");

    /// <summary>What a city piece can show.</summary>
    public static readonly (string key, string name)[] CityPieces =
    {
        ("city", "City name"), ("time", "Time"), ("ampm", "AM/PM"), ("weekday", "Weekday"), ("wd", "Weekday (short)"),
        ("when", "Today / Tomorrow"), ("offset", "Hours ahead or behind"), ("temp", "Temperature"), ("weather", "Sky"),
    };

    public static string Expand(string text, IList<CityItem>? cities = null) => Token.Replace(text, m =>
    {
        var key = m.Groups[1].Value;
        string? v;
        if (m.Groups[2].Success)
        {
            int n = int.Parse(m.Groups[2].Value) - 1;
            v = cities != null && n >= 0 && n < cities.Count ? CityValue(key.ToLowerInvariant(), cities[n]) : "(add a city)";
        }
        else v = Value(key) ?? Value(key.ToLowerInvariant());
        if (v == null) return m.Value;
        bool upper = key.Length > 1 && key.ToUpperInvariant() == key && key.ToLowerInvariant() != key;
        return upper ? v.ToUpperInvariant() : v;
    });

    static string? CityValue(string k, CityItem c)
    {
        var t = WeatherService.CityTime(c);
        var here = DateTime.Now;
        var w = WeatherService.CityCached(c);
        bool h24 = SettingsStore.Current.Clock24;
        return k switch
        {
            "city" => c.Name.Split(',')[0],
            "time" => t.ToString(h24 ? "HH:mm" : "h:mm"),
            "hour" => t.ToString(h24 ? "HH" : "h"),
            "minute" => t.ToString("mm"),
            "ampm" => h24 ? "" : t.ToString("tt"),
            "weekday" => t.ToString("dddd"),
            "wd" => t.ToString("ddd"),
            "day" => t.Day.ToString(),
            "when" => t.Date == here.Date ? "Today" : t.Date > here.Date ? "Tomorrow" : "Yesterday",
            "offset" => Math.Round((t - here).TotalHours * 2) / 2 is var d && d == 0 ? "same time" : (d > 0 ? "+" : "−") + Math.Abs(d).ToString("0.#") + "h",
            "temp" => w == null ? "--°" : $"{Math.Round(w.Temp)}°",
            "weather" => w?.Description ?? "",
            _ => null,
        };
    }

    static string? Value(string k)
    {
        var now = DateTime.Now;
        var m = MediaService.Instance;
        var w = WeatherService.Current;
        bool h24 = SettingsStore.Current.Clock24;
        return k switch
        {
            "weekday" => now.ToString("dddd"),
            "wd" => now.ToString("ddd"),
            "day" => now.Day.ToString(),
            "month" => now.ToString("MMMM"),
            "mon" => now.ToString("MMM"),
            "monthnum" => now.Month.ToString(),
            "year" => now.Year.ToString(),
            "date" => now.ToShortDateString(),
            "time" => now.ToString(h24 ? "HH:mm" : "h:mm"),
            "hour" => now.ToString(h24 ? "HH" : "h"),
            "minute" => now.ToString("mm"),
            "second" => now.ToString("ss"),
            "ampm" => h24 ? "" : now.ToString("tt"),
            "greeting" => now.Hour < 5 ? "Good night" : now.Hour < 12 ? "Good morning" : now.Hour < 18 ? "Good afternoon" : "Good evening",
            "song" => m.Title,
            "artist" => m.Artist,
            "player" => m.Source,
            "temp" => w == null ? "--°" : $"{Math.Round(w.Temp)}°",
            "weather" => w?.Description ?? "",
            "high" => w == null ? "" : $"{Math.Round(w.High)}°",
            "low" => w == null ? "" : $"{Math.Round(w.Low)}°",
            "city" => SettingsStore.Current.WeatherCity,
            "battery" => SystemInfo.HasBattery ? $"{SystemInfo.BatteryPercent}%" : "",
            "cpu" => $"{SystemInfo.Cpu:0}%",
            "ram" => $"{SystemInfo.Ram:0}%",
            "uptime" => Uptime(),
            _ => null,
        };
    }

    static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{t.Hours}h {t.Minutes}m";
    }

    public static string Format(TextLine line, IList<CityItem>? cities = null)
    {
        var s = Expand(line.Text, cities);
        s = line.Case switch { "UPPER" => s.ToUpperInvariant(), "lower" => s.ToLowerInvariant(), _ => s };
        // WPF has no letter-spacing, so spread letters with thin spaces
        return line.Spacing switch
        {
            "Wide" => string.Join("\u2005", s.ToCharArray()),
            "Wider" => string.Join("\u2005\u2005", s.ToCharArray()),
            _ => s,
        };
    }

    public static readonly Dictionary<string, Func<List<TextLine>>> Presets = new()
    {
        ["Blank"] = () => new() { new TextLine { Text = "{time}", Size = 48, Weight = "Light" } },
        ["Headline"] = () => new()
        {
            new TextLine { Text = "|  {WEEKDAY}  |", Font = "Modern", Size = 40, Weight = "Light", Spacing = "Wide" },
            new TextLine { Text = "{day}  {MON}  {year}", Font = "Modern", Size = 15 },
            new TextLine { Text = "{time} {ampm}", Font = "Modern", Size = 15, Opacity = 0.75 },
            new TextLine { Text = "\u266A {song}", Font = "Mono", Size = 12.5, Weight = "Bold", MusicOnly = true },
            new TextLine { Text = "\u203B {artist}", Font = "Mono", Size = 12.5, Opacity = 0.75, MusicOnly = true },
        },
        ["Big clock"] = () => new()
        {
            new TextLine { Text = "{time}", Size = 110, Weight = "Thin" },
            new TextLine { Text = "{weekday}, {day} {month}", Size = 18, Weight = "Light", Opacity = 0.85 },
        },
        ["Greeting"] = () => new()
        {
            new TextLine { Text = "{greeting}.", Size = 34, Weight = "Light" },
            new TextLine { Text = "It's {weekday}, {time} {ampm}  ·  {temp} and {weather}", Size = 15, Opacity = 0.8 },
        },
        ["Now playing"] = () => new()
        {
            new TextLine { Text = "NOW PLAYING", Size = 11, Weight = "SemiBold", Spacing = "Wide", Opacity = 0.6, MusicOnly = true },
            new TextLine { Text = "{song}", Size = 26, Weight = "SemiBold", MusicOnly = true },
            new TextLine { Text = "{artist}", Size = 15, Opacity = 0.8, MusicOnly = true },
        },
        ["Terminal"] = () => new()
        {
            new TextLine { Text = "> {wd} {day} {mon} {year}", Font = "Mono", Size = 16, Color = "#CAFFBF" },
            new TextLine { Text = "> {time}:{second}", Font = "Mono", Size = 16, Color = "#CAFFBF" },
            new TextLine { Text = "> cpu {cpu}  ram {ram}  up {uptime}", Font = "Mono", Size = 13, Color = "#CAFFBF", Opacity = 0.7 },
        },
    };
}

/// <summary>A widget made of text lines you design yourself.</summary>
public sealed class CustomTextWidget : StackPanel, IWidget
{
    readonly WidgetItem item;
    readonly List<(TextLine line, TextBlock block)> rows = new();
    static MediaService M => MediaService.Instance;

    public CustomTextWidget(WidgetItem item)
    {
        this.item = item;
        Build();
        M.Changed += Tick;
        Unloaded += (_, _) => M.Changed -= Tick;
    }

    public void Build()
    {
        Children.Clear();
        rows.Clear();
        var align = item.Align switch { "Left" => HorizontalAlignment.Left, "Right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        foreach (var line in item.Lines ?? new List<TextLine>())
        {
            var tb = new TextBlock
            {
                FontSize = Math.Clamp(line.Size, 6, 300),
                FontWeight = line.Weight switch
                {
                    "Thin" => FontWeights.Thin, "Light" => FontWeights.Light, "SemiBold" => FontWeights.SemiBold, "Bold" => FontWeights.Bold, _ => FontWeights.Normal,
                },
                Opacity = Math.Clamp(line.Opacity, 0.05, 1),
                HorizontalAlignment = align,
                TextAlignment = align == HorizontalAlignment.Left ? TextAlignment.Left : align == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Center,
            };
            if (line.Font != "Default")
            {
                var family = WidgetStyles.Fonts.FirstOrDefault(f => f.name == line.Font).family ?? line.Font;
                try { tb.FontFamily = new FontFamily(family); } catch { }
            }
            if (line.Color != null) tb.Foreground = new SolidColorBrush(ThemeManager.Parse(line.Color));
            else tb.SetResourceReference(TextBlock.ForegroundProperty, "Brim.WidgetText");
            Children.Add(tb);
            rows.Add((line, tb));
        }
        Tick();
    }

    public void Tick()
    {
        bool playing = M.HasSession && M.IsPlaying && !string.IsNullOrEmpty(M.Title);
        foreach (var (line, block) in rows)
        {
            if (line.MusicOnly && !playing) { block.Visibility = Visibility.Collapsed; continue; }
            block.Visibility = Visibility.Visible;
            block.Text = TextTokens.Format(line, item.Cities);
        }
    }
}
