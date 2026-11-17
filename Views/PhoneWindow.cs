using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Brim.Core;

namespace Brim.Views;

/// <summary>
/// The PC side of <see cref="PhoneLink"/>: the QR code the phone scans, whether a phone is connected, and a box to
/// send a link or text to it. Files go to the phone through the notch's Shelf.
/// </summary>
public sealed class PhoneWindow : Window
{
    readonly StackPanel body = new() { Margin = new Thickness(26, 22, 26, 22) };
    readonly System.Windows.Threading.DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(5) };
    TextBlock? statusText;
    Ellipse? statusDot;
    TextBox? sendBox;
    TextBlock? sentNote;

    static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3));

    public PhoneWindow()
    {
        Title = "Brim · Phone";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("Brim.Font");
        SetResourceReference(BackgroundProperty, "Set.Bg");
        Foreground = Brushes.White;
        UseLayoutRounding = true;
        Content = body;
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4);
        };
        PhoneLink.Watched = true;
        PhoneLink.Changed += Build;
        tick.Tick += (_, _) => UpdateStatus();
        tick.Start();
        Closed += (_, _) =>
        {
            PhoneLink.Watched = false;
            PhoneLink.Changed -= Build;
            tick.Stop();
            if (SettingsStore.Current.PhoneOnlyWhileOpen) PhoneLink.Stop(); // nothing comes or goes while this window is closed
        };
        if (!PhoneLink.On) PhoneLink.Start();
        Build();
    }

    void Build()
    {
        string draft = sendBox?.Text ?? "";
        body.Children.Clear();
        body.Children.Add(new TextBlock { Text = "Your phone", FontSize = 24, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = "Send photos, files, links and text between this PC and your iPhone or Android phone, over your Wi-Fi.",
            FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16),
        });

        if (!PhoneLink.On) { BuildProblem(); return; }

        // The QR code, on white so every phone camera reads it
        var qr = new Image { Source = Qr(PhoneLink.Url!), Width = 210, Height = 210 };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        var qrCard = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(16), Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Center, Child = qr };
        body.Children.Add(qrCard);
        body.Children.Add(new TextBlock
        {
            Text = "Scan it with your phone's camera", FontSize = 15, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 2),
        });
        body.Children.Add(new TextBlock
        {
            Text = "Your phone needs to be on the same Wi-Fi as this PC. Tip: add the page to your phone's home screen to come back with one tap.",
            FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(10, 0, 10, 0),
        });

        // Connected or not
        var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        statusDot = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        statusText = new TextBlock { FontSize = 13 };
        status.Children.Add(statusDot);
        status.Children.Add(statusText);
        body.Children.Add(status);
        UpdateStatus();

        // Send to the phone
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14, 16, 16), Margin = new Thickness(0, 18, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "Set.Card");
        var inner = new StackPanel();
        inner.Children.Add(new TextBlock { Text = "Send to your phone", FontSize = 15, FontWeight = FontWeights.SemiBold });
        inner.Children.Add(new TextBlock
        {
            Text = "A link or some text shows up on the phone's page to open or copy. Files on the notch's Shelf are there too; files from your phone go to Downloads and the Shelf.",
            FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 10),
        });
        sendBox = new TextBox { Text = draft.Length > 0 ? draft : ClipboardLink(), AcceptsReturn = false, MinHeight = 34 };
        sendBox.SetResourceReference(StyleProperty, "Field");
        sendBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) SendText(); };
        inner.Children.Add(sendBox);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var send = Pill("Send", SendText);
        send.SetResourceReference(BackgroundProperty, "Brim.Accent");
        send.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
        row.Children.Add(send);
        row.Children.Add(Pill("Add files…", AddFiles));
        sentNote = new TextBlock { FontSize = 12.5, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(sentNote);
        inner.Children.Add(row);
        card.Child = inner;
        body.Children.Add(card);

        // Off, or a fresh code
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        foot.Children.Add(Pill("Turn off", () => { PhoneLink.Stop(); Close(); }));
        var fresh = Pill("New QR code", PhoneLink.NewCode);
        fresh.ToolTip = "Make a new code. Phones that scanned the old one (or saved it to their home screen) can't connect until they scan again.";
        foot.Children.Add(fresh);
        body.Children.Add(foot);

        // Only while this window is open
        var only = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        only.ColumnDefinitions.Add(new ColumnDefinition());
        only.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var onlyText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        onlyText.Children.Add(new TextBlock { Text = "Only while this window is open", FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        onlyText.Children.Add(new TextBlock
        {
            Text = "Closing this window turns the link off, so nothing can be sent either way until you open it again.",
            FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
        });
        only.Children.Add(onlyText);
        var sw = new CheckBox { IsChecked = SettingsStore.Current.PhoneOnlyWhileOpen, VerticalAlignment = VerticalAlignment.Center };
        sw.SetResourceReference(StyleProperty, "Switch");
        TextBlock? offNote = null;
        void SetOnly(bool v)
        {
            SettingsStore.Current.PhoneOnlyWhileOpen = v;
            SettingsStore.Save();
            if (offNote != null) offNote.Text = OffNote();
        }
        sw.Checked += (_, _) => SetOnly(true);
        sw.Unchecked += (_, _) => SetOnly(false);
        Grid.SetColumn(sw, 1);
        only.Children.Add(sw);
        body.Children.Add(only);

        offNote = new TextBlock
        {
            Text = OffNote(),
            FontSize = 11.5, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        };
        body.Children.Add(offNote);
    }

    static string OffNote() =>
        (SettingsStore.Current.PhoneOnlyWhileOpen
            ? "The link turns off when you close this window."
            : "Brim turns this off by itself after 15 minutes without your phone.")
        + " If the page won't open on your phone, check that Windows allowed Brim through the firewall.";

    void BuildProblem()
    {
        var (title, text, fix) = PhoneLink.Problem switch
        {
            "public" => ("Your network is set to Public",
                         "Windows keeps other devices away from PCs on Public networks, so your phone couldn't reach this one. If this is your home Wi-Fi, set it to Private: Settings › Network & internet › Wi-Fi › your network › Private network.",
                         (Action?)(() => Launcher.Open("ms-settings:network-status"))),
            "port" => ("Couldn't start", "Other programs are using the network ports Brim needs. Try again in a moment.", null),
            _ => ("Not connected to a network", "Connect this PC to Wi-Fi (or a network cable) on the same network as your phone.", (Action?)(() => Launcher.Open("ms-settings:network-wifi"))),
        };
        body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (fix != null) row.Children.Add(Pill("Open network settings", fix));
        var again = Pill("Try again", () => PhoneLink.Start());
        again.SetResourceReference(BackgroundProperty, "Brim.Accent");
        again.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
        row.Children.Add(again);
        body.Children.Add(row);
    }

    void UpdateStatus()
    {
        if (statusText == null || statusDot == null) return;
        bool here = PhoneLink.LastSeen is DateTime seen && DateTime.Now - seen < TimeSpan.FromSeconds(15);
        statusDot.Fill = here ? new SolidColorBrush(Color.FromRgb(0x5F, 0xD3, 0x8D)) : Dim;
        statusText.Text = here ? $"{PhoneLink.PhoneKind} connected"
            : PhoneLink.LastSeen is DateTime s ? $"{PhoneLink.PhoneKind} last here at {s:HH:mm}"
            : "Waiting for your phone…";
    }

    void SendText()
    {
        if (sendBox == null || string.IsNullOrWhiteSpace(sendBox.Text)) return;
        PhoneLink.SendText(sendBox.Text);
        sendBox.Text = "";
        if (sentNote != null) sentNote.Text = "Sent ✓";
    }

    void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Send files to your phone", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        Shelf.Add(dlg.FileNames);
        if (sentNote != null) sentNote.Text = dlg.FileNames.Length == 1 ? "On your phone's page ✓" : $"{dlg.FileNames.Length} files on your phone's page ✓";
    }

    /// <summary>A link that's copied right now, ready to send.</summary>
    static string ClipboardLink()
    {
        try { if (Clipboard.ContainsText() && Clipboard.GetText().Trim() is var t && PhoneLink.IsLink(t)) return t; } catch { }
        return "";
    }

    static Button Pill(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Foreground = Brushes.White };
        b.SetResourceReference(StyleProperty, "Pill");
        b.SetResourceReference(BackgroundProperty, "Set.Field");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>The QR code as a small black-and-white picture (scaled up without blurring).</summary>
    internal static BitmapSource Qr(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var m = data.ModuleMatrix;
        int n = m.Count;
        const int scale = 6;
        int size = n * scale;
        var px = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = m[y / scale][x / scale] ? (byte)0 : (byte)255;
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, px, size);
        bmp.Freeze();
        return bmp;
    }
}
