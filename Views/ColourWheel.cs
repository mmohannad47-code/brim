using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Brim.Views;

/// <summary>
/// Pick any colour: a hue/saturation wheel, a brightness bar and a hex box. <see cref="Changed"/> fires while you
/// drag (for a live preview); <see cref="Open"/> shows it in a pop-up next to a button.
/// </summary>
sealed class ColourWheel : StackPanel
{
    const double Size = 190;
    public event Action<Color>? Changed;

    double hue, sat, val = 1;
    readonly Image disc = new() { Width = Size, Height = Size, Source = Disc() };
    readonly Ellipse shade = new() { Width = Size, Height = Size, Fill = Brushes.Black, IsHitTestVisible = false };
    readonly Ellipse thumb = new() { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2.5, IsHitTestVisible = false,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6 } };
    readonly Canvas wheel = new() { Width = Size, Height = Size, Background = Brushes.Transparent, Cursor = Cursors.Cross };
    readonly Border bar = new() { Height = 14, CornerRadius = new CornerRadius(7), Width = Size };
    readonly Ellipse barThumb = new() { Width = 18, Height = 18, Stroke = Brushes.White, StrokeThickness = 2.5, IsHitTestVisible = false,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6 } };
    readonly Canvas barHost = new() { Width = Size, Height = 18, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    readonly Border preview = new() { Width = 30, Height = 30, CornerRadius = new CornerRadius(15) };
    readonly TextBox hex = new() { Width = 96, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxLength = 7 };
    bool typing;

    public Color Colour => FromHsv(hue, sat, val);

    public ColourWheel(Color start)
    {
        (hue, sat, val) = ToHsv(start);

        wheel.Children.Add(disc);
        wheel.Children.Add(shade);
        wheel.Children.Add(thumb);
        wheel.MouseLeftButtonDown += (_, e) => { wheel.CaptureMouse(); PickOnWheel(e.GetPosition(wheel)); e.Handled = true; };
        wheel.MouseMove += (_, e) => { if (wheel.IsMouseCaptured) PickOnWheel(e.GetPosition(wheel)); };
        wheel.MouseLeftButtonUp += (_, _) => wheel.ReleaseMouseCapture();
        Children.Add(wheel);

        Canvas.SetTop(bar, 2);
        barHost.Children.Add(bar);
        barHost.Children.Add(barThumb);
        barHost.Margin = new Thickness(0, 14, 0, 0);
        barHost.MouseLeftButtonDown += (_, e) => { barHost.CaptureMouse(); PickOnBar(e.GetPosition(barHost)); e.Handled = true; };
        barHost.MouseMove += (_, e) => { if (barHost.IsMouseCaptured) PickOnBar(e.GetPosition(barHost)); };
        barHost.MouseLeftButtonUp += (_, _) => barHost.ReleaseMouseCapture();
        Children.Add(barHost);

        hex.SetResourceReference(StyleProperty, "Field");
        hex.TextChanged += (_, _) =>
        {
            if (typing) return;
            var t = hex.Text.Trim().TrimStart('#');
            if (t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var n)) return;
            (hue, sat, val) = ToHsv(Color.FromRgb((byte)(n >> 16), (byte)(n >> 8), (byte)n));
            Refresh(updateText: false);
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(preview);
        row.Children.Add(hex);
        Children.Add(row);

        Refresh(updateText: true, notify: false);
    }

    void PickOnWheel(Point p)
    {
        double r = Size / 2, dx = p.X - r, dy = p.Y - r;
        hue = (Math.Atan2(-dy, dx) * 180 / Math.PI + 360) % 360;
        sat = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / r);
        if (val < 0.15) val = 1; // picking a colour on a black wheel would do nothing you can see
        Refresh(updateText: true);
    }

    void PickOnBar(Point p)
    {
        val = Math.Clamp(p.X / Size, 0, 1);
        Refresh(updateText: true);
    }

    void Refresh(bool updateText, bool notify = true)
    {
        var c = Colour;
        double r = Size / 2, a = hue * Math.PI / 180;
        Canvas.SetLeft(thumb, r + Math.Cos(a) * sat * r - thumb.Width / 2);
        Canvas.SetTop(thumb, r - Math.Sin(a) * sat * r - thumb.Height / 2);
        thumb.Fill = new SolidColorBrush(c);
        shade.Opacity = 1 - val;
        bar.Background = new LinearGradientBrush(Colors.Black, FromHsv(hue, sat, 1), 0);
        Canvas.SetLeft(barThumb, val * (Size - barThumb.Width));
        barThumb.Fill = new SolidColorBrush(c);
        preview.Background = new SolidColorBrush(c);
        if (updateText) { typing = true; hex.Text = Hex(c); typing = false; }
        if (notify) Changed?.Invoke(c);
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Shows the wheel in a pop-up under <paramref name="anchor"/>; <paramref name="done"/> gets the colour when it closes (if it changed).</summary>
    public static void Open(FrameworkElement anchor, Color start, Action<Color>? live, Action<Color> done)
    {
        var wheel = new ColourWheel(start);
        bool changed = false;
        wheel.Changed += c => { changed = true; live?.Invoke(c); };
        var card = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = wheel, Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.5 } };
        card.SetResourceReference(Border.BackgroundProperty, "Set.Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Set.Line");
        var pop = new Popup { Child = card, PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
        pop.Closed += (_, _) => { if (changed) done(wheel.Colour); };
        pop.IsOpen = true;
        wheel.hex.Focus();
        wheel.hex.SelectAll();
        wheel.hex.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Escape) pop.IsOpen = false; };
    }

    // ---------- colour maths

    public static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    static BitmapSource? discCache;

    /// <summary>The hue/saturation wheel at full brightness, drawn once at 2× for sharp edges.</summary>
    public static BitmapSource Disc()
    {
        if (discCache != null) return discCache;
        int n = (int)Size * 2;
        var px = new byte[n * n * 4];
        double r = n / 2.0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double dx = x + 0.5 - r, dy = y + 0.5 - r, d = Math.Sqrt(dx * dx + dy * dy);
                double alpha = Math.Clamp(r - d, 0, 1); // soft 1-pixel edge
                if (alpha <= 0) continue;
                var c = FromHsv((Math.Atan2(-dy, dx) * 180 / Math.PI + 360) % 360, Math.Min(1, d / r), 1);
                int i = (y * n + x) * 4;
                px[i] = (byte)(c.B * alpha); px[i + 1] = (byte)(c.G * alpha); px[i + 2] = (byte)(c.R * alpha); px[i + 3] = (byte)(255 * alpha);
            }
        var bmp = BitmapSource.Create(n, n, 192, 192, PixelFormats.Pbgra32, null, px, n * 4);
        bmp.Freeze();
        return discCache = bmp;
    }
}
