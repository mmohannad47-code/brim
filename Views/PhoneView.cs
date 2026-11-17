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
/// The notch's Phone tab: the QR code to scan, whether a phone is connected, which network it's on, quick actions
/// (choose files, open what came in, copy the link, clear the list) and what the phone can pick up.
/// The link is on while this tab is showing; with "only while open" set, it goes off when you leave it.
/// </summary>
sealed class PhoneView : Grid
{
    static readonly Color Green = Color.FromRgb(0x5F, 0xD3, 0x8D);
    readonly Image qr = new() { Width = 148, Height = 148 };
    readonly Border qrCard;
    readonly TextBlock status = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
    readonly StackPanel right = new();
    readonly Action keepOpen, letGo;
    readonly Action<TextBox> takeKeyboard;
    string draft = "";
    bool showReceived;
    bool shown;
    int lastCount = -1;

    public PhoneView(Action keepOpen, Action letGo, Action<TextBox> takeKeyboard)
    {
        this.keepOpen = keepOpen;
        this.letGo = letGo;
        this.takeKeyboard = takeKeyboard;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        ColumnDefinitions.Add(new ColumnDefinition());

        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        qrCard = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center, Child = qr };
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        left.Children.Add(qrCard);
        left.Children.Add(status);
        Children.Add(left);
        Grid.SetColumn(right, 2);
        Children.Add(right);

        PhoneLink.Changed += Build;
        Shelf.Items.CollectionChanged += (_, _) => { if (shown) Build(); };
        App.SecondTick += () => { if (shown) { UpdateStatus(); if (PhoneLink.Sent().Count + PhoneLink.Offered().Count + PhoneLink.ReceivedFiles().Count * 1000 != lastCount) Build(); } };
        Build();
    }

    /// <summary>The tab came into view (true) or went out of it (the notch closed, or another tab).</summary>
    public void SetShown(bool now)
    {
        if (now == shown) return;
        shown = now;
        PhoneLink.Watched = now;
        if (now) { if (!PhoneLink.On) PhoneLink.Start(); Build(); }
        else if (SettingsStore.Current.PhoneOnlyWhileOpen) PhoneLink.Stop();
    }

    void Build()
    {
        bool refocus = right.IsKeyboardFocusWithin;
        right.Children.Clear();
        // Header: icon, title, on/off
        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 10, 0) };
        tile.SetResourceReference(Border.BackgroundProperty, "Brim.Hover");
        var glyph = Icon("", 16);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Accent");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        tile.Child = glyph;
        head.Children.Add(tile);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Transfer to phone", FontSize = 15, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(Dim("Same Wi-Fi · Phone ⇄ Computer", 11));
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        var onOff = PhoneLink.On
            ? SmallPill("Stop", () => PhoneLink.Stop(), danger: true)
            : SmallPill("Start", () => PhoneLink.Start(), accent: true);
        onOff.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(onOff, 2);
        head.Children.Add(onOff);
        right.Children.Add(head);

        if (!PhoneLink.On)
        {
            qrCard.Visibility = Visibility.Hidden;
            status.Text = "";
            var (title, text, fix) = PhoneLink.Problem switch
            {
                "public" => ("Your network is set to Public", "Windows keeps phones away from PCs on Public networks. If this is your home Wi-Fi, set it to Private.", (Action?)(() => Launcher.Open("ms-settings:network-status"))),
                "port" => ("Couldn't start", "Other programs are using the ports Brim needs. Try again in a moment.", null),
                "network" => ("Not connected to a network", "Connect this PC to the same Wi-Fi as your phone.", (Action?)(() => Launcher.Open("ms-settings:network-wifi"))),
                _ => ("Off", "Press Start to show the QR code. Nothing can be sent while it's off.", null),
            };
            right.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            var t = Dim(text, 12);
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, 3, 0, 10);
            right.Children.Add(t);
            if (fix != null) right.Children.Add(SmallPill("Open network settings", fix));
            lastCount = -1;
            return;
        }

        qrCard.Visibility = Visibility.Visible;
        qr.Source = PhoneWindow.Qr(PhoneLink.Url!);
        UpdateStatus();

        // Network
        var nets = PhoneLink.Networks();
        var here = new Uri(PhoneLink.Url!).Host;
        var netBtn = Action(nets.Count > 1 ? $"Network: {here}  ⌄" : $"Network: {here}", null);
        if (nets.Count > 1)
            netBtn.Click += (_, _) =>
            {
                var menu = new ContextMenu();
                foreach (var n in nets)
                {
                    var mi = new MenuItem { Header = $"{n.Name} · {n.Ip}", IsCheckable = true, IsChecked = n.Ip == here };
                    var ip = n.Ip;
                    mi.Click += (_, _) => PhoneLink.UseNetwork(ip);
                    menu.Items.Add(mi);
                }
                keepOpen();
                menu.Closed += (_, _) => letGo();
                menu.PlacementTarget = netBtn;
                menu.IsOpen = true;
            };
        netBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        right.Children.Add(netBtn);

        // Actions: pick files to send, or a new QR code (phones with the old one have to scan again)
        var grid = new UniformGrid2(2, 6, 6) { Margin = new Thickness(0, 6, 0, 0) };
        grid.Add(Action("Choose files", ChooseFiles));
        var fresh = Action("New QR code", PhoneLink.NewCode);
        fresh.ToolTip = "Make a new code. Phones that scanned the old one (or saved it to their home screen) have to scan again.";
        grid.Add(fresh);
        right.Children.Add(grid);

        // Send a link or some text: paste it in and press Enter (or Send)
        var sendRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        sendRow.ColumnDefinitions.Add(new ColumnDefinition());
        sendRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { Text = draft, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Paste a link or type something, then Enter" };
        box.SetResourceReference(StyleProperty, "Field");
        var hint = Dim("Paste a link or text to send…", 12);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(11, 0, 0, 0);
        hint.Visibility = draft.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => { draft = box.Text; hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
        // Typing here needs the keyboard (the notch doesn't take it otherwise); it stays open while you type
        box.PreviewMouseLeftButtonDown += (_, _) => { if (!box.IsKeyboardFocusWithin) takeKeyboard(box); };
        if (refocus) Dispatcher.BeginInvoke(new Action(() => { takeKeyboard(box); box.CaretIndex = box.Text.Length; }));
        void SendNow()
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            PhoneLink.SendText(box.Text);
            draft = "";
            Build();
        }
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { SendNow(); e.Handled = true; } };
        sendRow.Children.Add(box);
        sendRow.Children.Add(hint);
        var send = SmallPill("Send", SendNow, accent: true);
        send.Height = 32;
        send.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(send, 1);
        sendRow.Children.Add(send);
        right.Children.Add(sendRow);

        // What's waiting on the phone, or what came from it
        var files = PhoneLink.Offered();
        var sent = PhoneLink.Sent();
        var got = PhoneLink.ReceivedFiles();
        lastCount = files.Count + sent.Count + got.Count * 1000;
        var tabs = new Grid { Margin = new Thickness(0, 10, 0, 4) };
        tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabs.ColumnDefinitions.Add(new ColumnDefinition());
        tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toPhone = Chip($"To phone · {files.Count + sent.Count}", !showReceived, () => { showReceived = false; Build(); });
        var fromPhone = Chip($"From phone · {got.Count}", showReceived, () => { showReceived = true; Build(); });
        fromPhone.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(fromPhone, 1);
        tabs.Children.Add(toPhone);
        tabs.Children.Add(fromPhone);
        var clear = showReceived
            ? SmallPill("Open folder", () => Launcher.Open(Downloads.Folder))
            : SmallPill("Clear list", PhoneLink.ClearSent);
        clear.Height = 24;
        clear.FontSize = 11;
        Grid.SetColumn(clear, 3);
        tabs.Children.Add(clear);
        right.Children.Add(tabs);
        var list = new StackPanel();
        if (showReceived)
        {
            foreach (var (path, at) in got)
            {
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }
                var row = Row("", System.IO.Path.GetFileName(path), $"{Size(size)} · {at:HH:mm}");
                row.Cursor = System.Windows.Input.Cursors.Hand;
                row.ToolTip = path + "\nClick to open · right-click to show it in its folder · drag it anywhere";
                var p = path;
                Point? down = null;
                row.MouseLeftButtonDown += (_, e) => down = e.GetPosition(row);
                row.MouseMove += (_, e) =>
                {
                    if (down is not Point d || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || (e.GetPosition(row) - d).Length < 6) return;
                    down = null;
                    keepOpen();
                    try { DragDrop.DoDragDrop(row, new DataObject(DataFormats.FileDrop, new[] { p }), DragDropEffects.Copy | DragDropEffects.Move); }
                    finally { letGo(); }
                };
                row.MouseLeftButtonUp += (_, _) => { if (down != null) { down = null; Launcher.Open(p); } };
                row.MouseRightButtonUp += (_, e) => { e.Handled = true; Launcher.ShowInFolder(p); };
                list.Children.Add(row);
            }
            if (got.Count == 0)
            {
                var none = Dim("Nothing yet. Photos and files you send from your phone show up here (and in Downloads).", 11.5);
                none.TextWrapping = TextWrapping.Wrap;
                list.Children.Add(none);
            }
        }
        else
        {
            foreach (var p in files)
            {
                bool dir = Directory.Exists(p);
                long size = 0;
                try { if (!dir) size = new FileInfo(p).Length; } catch { }
                list.Children.Add(Row(dir ? "" : "", System.IO.Path.GetFileName(p.TrimEnd('\\')) + (dir ? ".zip" : ""), dir ? "folder" : Size(size)));
            }
            foreach (var s in sent) list.Children.Add(Row(s.IsLink ? "" : "", s.Text, s.At.ToString("HH:mm")));
            if (files.Count + sent.Count == 0)
            {
                var empty = Dim("Drop files on the Shelf, choose some, or paste a link above, and it shows up on your phone.", 11.5);
                empty.TextWrapping = TextWrapping.Wrap;
                list.Children.Add(empty);
            }
        }
        right.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 92 });
    }

    void UpdateStatus()
    {
        if (!PhoneLink.On) return;
        bool here = PhoneLink.LastSeen is DateTime seen && DateTime.Now - seen < TimeSpan.FromSeconds(15);
        status.Foreground = here ? new SolidColorBrush(Green) : new SolidColorBrush(Color.FromArgb(0xCC, Green.R, Green.G, Green.B));
        status.Text = here ? $"● {PhoneLink.PhoneKind} connected"
            : SettingsStore.Current.PhoneOnlyWhileOpen ? "Scan QR · on while this tab is open"
            : "Scan QR · valid for 15 minutes";
    }

    void ChooseFiles()
    {
        keepOpen();
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Send files to your phone", Multiselect = true };
            if (dlg.ShowDialog() == true) Shelf.Add(dlg.FileNames);
        }
        finally { letGo(); }
    }

    static string Size(long b) => b >= 1 << 30 ? $"{b / (double)(1 << 30):0.0} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, b / 1024)} KB";

    // ---------------------------------------------------------------- pieces

    static TextBlock Icon(string g, double size) => new()
    {
        Text = g, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
        FontFamily = (FontFamily)Application.Current.FindResource("Brim.Icons"),
    };

    static TextBlock Dim(string t, double size)
    {
        var tb = new TextBlock { Text = t, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        return tb;
    }

    static Border Row(string glyph, string name, string meta)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var i = Icon(glyph, 12);
        i.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        g.Children.Add(i);
        var n = new TextBlock { Text = name.Replace('\n', ' '), FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(n, 1);
        g.Children.Add(n);
        var m = Dim(meta, 11);
        m.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(m, 2);
        g.Children.Add(m);
        var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 4), Child = g };
        b.SetResourceReference(Border.BackgroundProperty, "Brim.Hover");
        return b;
    }

    static Button Action(string text, Action? click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
            Height = 32, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 0, 12, 0),
        };
        b.SetResourceReference(StyleProperty, "Pill");
        b.SetResourceReference(Control.BackgroundProperty, "Brim.Hover");
        b.SetResourceReference(Control.ForegroundProperty, "Brim.Text");
        if (click != null) b.Click += (_, _) => click();
        return b;
    }

    static Button SmallPill(string text, Action click, bool danger = false, bool accent = false)
    {
        var b = new Button { Content = text, Height = 28, Padding = new Thickness(12, 0, 12, 0), FontSize = 12, FontWeight = FontWeights.SemiBold };
        b.SetResourceReference(StyleProperty, "Pill");
        if (danger) { b.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x5A, 0x5A)); b.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x9A)); }
        else if (accent) { b.SetResourceReference(Control.BackgroundProperty, "Brim.Accent"); b.SetResourceReference(Control.ForegroundProperty, "Brim.OnAccent"); }
        else { b.SetResourceReference(Control.BackgroundProperty, "Brim.Hover"); b.SetResourceReference(Control.ForegroundProperty, "Brim.Text"); }
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A small rounded switch between the two lists (the one showing is filled in).</summary>
    static Button Chip(string text, bool on, Action click)
    {
        var b = new Button { Content = text, Height = 24, Padding = new Thickness(10, 0, 10, 0), FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        b.SetResourceReference(StyleProperty, "Pill");
        if (on) { b.SetResourceReference(Control.BackgroundProperty, "Brim.Accent"); b.SetResourceReference(Control.ForegroundProperty, "Brim.OnAccent"); }
        else { b.SetResourceReference(Control.BackgroundProperty, "Brim.Hover"); b.SetResourceReference(Control.ForegroundProperty, "Brim.Dim"); }
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Equal columns with gaps between them (WPF's UniformGrid has no gaps).</summary>
    sealed class UniformGrid2 : Grid
    {
        readonly int cols;
        readonly double gapX, gapY;
        int count;
        public UniformGrid2(int cols, double gapX, double gapY)
        {
            this.cols = cols; this.gapX = gapX; this.gapY = gapY;
            for (int c = 0; c < cols; c++)
            {
                if (c > 0) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gapX) });
                ColumnDefinitions.Add(new ColumnDefinition());
            }
        }
        public void Add(UIElement el)
        {
            int r = count / cols, c = count % cols;
            // Rows alternate: content (even), gap (odd)
            while (RowDefinitions.Count < r * 2 + 1)
                RowDefinitions.Add(new RowDefinition { Height = RowDefinitions.Count % 2 == 1 ? new GridLength(gapY) : GridLength.Auto });
            SetRow(el, r * 2);
            SetColumn(el, c * 2);
            Children.Add(el);
            count++;
        }
    }
}
