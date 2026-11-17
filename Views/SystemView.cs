using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brim.Core;

namespace Brim.Views;

/// <summary>
/// The notch's System tab: four cards. CPU, memory and network with their last minute as a line; the network in
/// detail (down and up); each drive and how full it is; and quick controls (volume, brightness, mute, dark mode).
/// Updates once a second while it's showing.
/// </summary>
sealed class SystemView : Grid
{
    static readonly Color Blue = Color.FromRgb(0x6C, 0xA8, 0xFF), Green = Color.FromRgb(0x7B, 0xE0, 0xA0), Red = Color.FromRgb(0xFF, 0x6B, 0x6B);

    readonly TextBlock cpuText = Value(), ramText = Value(), netText = Value(13), downText = Big(Blue), upText = Big(Green), netName = Dim("");
    readonly Polyline cpuLine = Line(Blue), ramLine = Line(Blue), netLine = Line(Blue), downLine = Line(Blue, 2), upLine = Line(Green, 2);
    readonly StackPanel drives = new();
    readonly Slider volume = new() { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
    readonly Slider bright = new() { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock volText = Dim(""), brightText = Dim("");
    readonly Grid brightRow;
    readonly Button muteBtn, darkBtn;
    bool updating;
    int ticks;

    public SystemView()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        RowDefinitions.Add(new RowDefinition());

        // 1. At a glance
        var glance = new StackPanel();
        glance.Children.Add(StatRow("CPU", cpuText, cpuLine));
        glance.Children.Add(StatRow("MEMORY", ramText, ramLine, top: 8));
        glance.Children.Add(StatRow("NETWORK NOW", netText, netLine, top: 8));
        Add(Card(glance), 0, 0);

        // 2. Network
        var net = new StackPanel();
        net.Children.Add(Title("Network"));
        netName.Margin = new Thickness(0, 2, 0, 8);
        net.Children.Add(netName);
        var speeds = new Grid();
        speeds.ColumnDefinitions.Add(new ColumnDefinition());
        speeds.ColumnDefinitions.Add(new ColumnDefinition());
        speeds.Children.Add(downText);
        Grid.SetColumn(upText, 1);
        speeds.Children.Add(upText);
        net.Children.Add(speeds);
        var graph = new Grid { Height = 54, Margin = new Thickness(0, 8, 0, 0) };
        graph.Children.Add(downLine);
        graph.Children.Add(upLine);
        net.Children.Add(graph);
        linkNote.Margin = new Thickness(0, 6, 0, 0);
        net.Children.Add(linkNote);
        Add(Card(net), 0, 2);

        // 3. Storage
        var store = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("Brim.Icons"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(Title("Storage"));
        store.Children.Add(head);
        store.Children.Add(new ScrollViewer { Content = drives, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 116 });
        Add(Card(store), 2, 0);

        // 4. Control center
        var cc = new StackPanel();
        cc.Children.Add(Title("Control center"));
        cc.Children.Add(SliderRow("", volume, volText, top: 10));
        brightRow = SliderRow("", bright, brightText, top: 6);
        brightRow.Visibility = Visibility.Collapsed; // (shown once Windows says the screen has adjustable brightness)
        cc.Children.Add(brightRow);
        var buttons = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        muteBtn = Toggle("", "Mute", () => { Audio.Muted = !Audio.Muted; Refresh(); });
        darkBtn = Toggle("", "Dark", () => { DarkMode.Set(!DarkMode.On); Refresh(); });
        buttons.Children.Add(muteBtn);
        Grid.SetColumn(darkBtn, 2);
        buttons.Children.Add(darkBtn);
        cc.Children.Add(buttons);
        Add(Card(cc), 2, 2);

        volume.ValueChanged += (_, e) => { if (!updating) { Audio.Volume = (float)(e.NewValue / 100); volText.Text = $"{e.NewValue:0}%"; } };
        bright.ValueChanged += (_, e) => { if (!updating) { Brightness.Set((int)e.NewValue); brightText.Text = $"{e.NewValue:0}%"; } };

        Loaded += async (_, _) =>
        {
            App.SecondTick += Tick;
            Brightness.Changed += ShowBrightness;
            Refresh();
            if (await Brightness.Read() is int v) ShowBrightness(v);
        };
        Unloaded += (_, _) => { App.SecondTick -= Tick; Brightness.Changed -= ShowBrightness; };
    }

    /// <summary>Follow the screen's brightness, however it was changed (not while the slider is being dragged).</summary>
    void ShowBrightness(int v)
    {
        brightRow.Visibility = Visibility.Visible;
        if (bright.IsMouseCaptureWithin) return;
        updating = true; bright.Value = v; brightText.Text = $"{v}%"; updating = false;
    }

    void Add(UIElement el, int row, int col) { SetRow(el, row); SetColumn(el, col); Children.Add(el); }

    void Tick()
    {
        if (!IsVisible) return;
        Refresh();
    }

    void Refresh()
    {
        ticks++;
        cpuText.Text = $"{SystemInfo.Cpu:0}%";
        ramText.Text = $"{SystemInfo.RamUsedGb:0.0} / {SystemInfo.RamTotalGb:0} GB";
        netText.Text = $"↓ {SystemHistory.Speed(SystemHistory.DownNow)}   ↑ {SystemHistory.Speed(SystemHistory.UpNow)}";
        Plot(cpuLine, SystemHistory.Cpu, 100);
        Plot(ramLine, SystemHistory.Ram, 100);
        double netMax = Math.Max(50_000, SystemHistory.Down.Max() + SystemHistory.Up.Max());
        var sum = SystemHistory.Down.Zip(SystemHistory.Up, (d, u) => d + u).ToArray();
        Plot(netLine, sum, netMax);

        downText.Text = "↓ " + SystemHistory.Speed(SystemHistory.DownNow);
        upText.Text = "↑ " + SystemHistory.Speed(SystemHistory.UpNow);
        double bothMax = Math.Max(50_000, Math.Max(SystemHistory.Down.Max(), SystemHistory.Up.Max()));
        Plot(downLine, SystemHistory.Down, bothMax);
        Plot(upLine, SystemHistory.Up, bothMax);

        if (ticks % 10 == 1)
        {
            var n = Network.Current();
            netName.Text = n.Connected ? $"{(n.Wireless ? "Wi-Fi" : n.Name)}{(n.Wireless ? " · " + n.Name : "")}{Address()}" : "Not connected";
            // What's moving right now, not how fast the connection is: an idle PC moves little. Say so, with the link's speed
            linkNote.Text = LinkSpeed > 0 ? $"Data moving right now · link speed {Mbps(LinkSpeed)}" : "Data moving right now";
            ShowDrives();
        }

        updating = true;
        float vol = Audio.Volume;
        volume.Value = Math.Round(vol * 100);
        volText.Text = $"{vol * 100:0}%";
        updating = false;
        SetOn(muteBtn, Audio.Muted, Audio.Muted ? "Muted" : "Mute");
        SetOn(darkBtn, DarkMode.On, "Dark");
    }

    static long LinkSpeed;
    readonly TextBlock linkNote = Dim("");

    /// <summary>"866 Mbps", "1.2 Gbps".</summary>
    static string Mbps(long bitsPerSecond) => bitsPerSecond >= 1_000_000_000 ? $"{bitsPerSecond / 1e9:0.#} Gbps" : $"{bitsPerSecond / 1e6:0} Mbps";

    static string Address()
    {
        try
        {
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0);
            if (nic == null) return "";
            var ip = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address;
            LinkSpeed = nic.Speed > 0 ? nic.Speed : 0;
            return ip == null ? "" : " · " + ip;
        }
        catch { return ""; }
    }

    void ShowDrives()
    {
        drives.Children.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            try
            {
                if (!d.IsReady || d.TotalSize <= 0) continue;
                double used = 1 - (double)d.AvailableFreeSpace / d.TotalSize;
                bool low = d.AvailableFreeSpace < d.TotalSize * 0.12;
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition());
                row.RowDefinitions.Add(new RowDefinition());
                string label = d.VolumeLabel.Length > 0 ? d.VolumeLabel : d.DriveType == DriveType.Removable ? "USB drive" : "Local Disk";
                row.Children.Add(new TextBlock { Text = $"{label} ({d.Name.TrimEnd('\\')})", FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                var free = Dim($"{Size(d.AvailableFreeSpace)} free");
                if (low) free.Foreground = new SolidColorBrush(Red);
                Grid.SetColumn(free, 1);
                row.Children.Add(free);
                var track = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(0, 5, 0, 0) };
                track.SetResourceReference(Border.BackgroundProperty, "Brim.Track");
                var fill = new Border { CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(low ? Red : Blue) };
                track.Child = fill;
                track.SizeChanged += (_, _) => fill.Width = Math.Max(5, track.ActualWidth * used);
                Grid.SetRow(track, 1);
                Grid.SetColumnSpan(track, 2);
                row.Children.Add(track);
                drives.Children.Add(row);
            }
            catch { }
        }
    }

    static string Size(long bytes) => bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.0} TB" : $"{bytes / (double)(1L << 30):0} GB";

    // ---------------------------------------------------------------- pieces

    static Border Card(UIElement child) => Cards.Make(child, new Thickness(14, 12, 14, 12));

    static TextBlock Title(string t) => new() { Text = t, FontSize = 13.5, FontWeight = FontWeights.SemiBold };
    static TextBlock Value(double size = 15) => new() { FontSize = size, FontWeight = FontWeights.SemiBold };
    static TextBlock Big(Color c) => new() { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c) };
    static TextBlock Dim(string t)
    {
        var tb = new TextBlock { Text = t, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        return tb;
    }

    static Polyline Line(Color c, double thick = 1.6) => new()
    {
        Stroke = new SolidColorBrush(c), StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round,
        Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    static Grid StatRow(string label, TextBlock value, Polyline line, double top = 0)
    {
        var g = new Grid { Margin = new Thickness(0, top, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new StackPanel();
        var l = Dim(label);
        l.FontSize = 10;
        l.FontWeight = FontWeights.SemiBold;
        text.Children.Add(l);
        text.Children.Add(value);
        g.Children.Add(text);
        var host = new Grid { Height = 26, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 2) };
        host.Children.Add(line);
        Grid.SetColumn(host, 1);
        g.Children.Add(host);
        return g;
    }

    /// <summary>Draw <paramref name="data"/> (oldest first) across the line's box, 0 at the bottom and <paramref name="max"/> at the top.</summary>
    static void Plot(Polyline line, double[] data, double max)
    {
        if (line.Parent is not FrameworkElement box || box.ActualWidth < 2) return;
        double w = box.ActualWidth, h = box.ActualHeight - 2;
        int n = Math.Max(2, SystemHistory.Filled);
        var pts = new PointCollection(n);
        int start = data.Length - n;
        for (int i = 0; i < n; i++)
        {
            double v = Math.Clamp(data[start + i] / Math.Max(1e-9, max), 0, 1);
            pts.Add(new Point(w * i / (n - 1), 1 + h * (1 - v)));
        }
        line.Points = pts;
    }

    Grid SliderRow(string glyph, Slider s, TextBlock pct, double top)
    {
        var g = new Grid { Margin = new Thickness(0, top, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("Brim.Icons"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        s.SetResourceReference(StyleProperty, "BrimSlider");
        Grid.SetColumn(s, 1);
        g.Children.Add(s);
        pct.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(pct, 2);
        g.Children.Add(pct);
        return g;
    }

    static Button Toggle(string glyph, string text, Action click)
    {
        var label = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("Brim.Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        content.Children.Add(label);
        var b = new Button { Content = content, Height = 34, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 0, 12, 0), Tag = label };
        b.SetResourceReference(StyleProperty, "Pill");
        b.SetResourceReference(Control.BackgroundProperty, "Brim.Hover");
        b.Click += (_, _) => click();
        return b;
    }

    static void SetOn(Button b, bool on, string text)
    {
        if (b.Tag is TextBlock t) t.Text = text;
        if (on) { b.SetResourceReference(Control.BackgroundProperty, "Brim.Accent"); b.SetResourceReference(Control.ForegroundProperty, "Brim.OnAccent"); }
        else { b.SetResourceReference(Control.BackgroundProperty, "Brim.Hover"); b.SetResourceReference(Control.ForegroundProperty, "Brim.Text"); }
    }
}

/// <summary>Windows' dark mode for apps and the system (Settings › Personalisation › Colours).</summary>
public static class DarkMode
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool On
    {
        get
        {
            try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue("AppsUseLightTheme") is int v && v == 0; }
            catch { return false; }
        }
    }

    public static void Set(bool dark)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("AppsUseLightTheme", dark ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
            // Tell every window the colours changed, so apps and the taskbar switch straight away
            Native.SendMessageTimeoutStr(new IntPtr(0xFFFF), Native.WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", 2 /* ABORTIFHUNG */, 200, out _);
            Log.Note("system", $"dark mode {(dark ? "on" : "off")}");
        }
        catch (Exception ex) { Log.Error("dark mode", ex); }
    }
}
