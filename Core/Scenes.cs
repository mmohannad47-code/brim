using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Brim.Core;

/// <summary>
/// Animated wallpapers that Brim draws itself, so there is nothing to download and no account needed.
/// Every scene is a pure function of time (positions come from a seeded random set), which keeps them
/// cheap to draw and lets the gallery show any moment as a preview.
/// </summary>
public abstract class Scene
{
    public virtual int Fps => 30;
    public abstract void Draw(DrawingContext dc, double w, double h, double t, double pixelsPerDip);

    protected static SolidColorBrush Solid(string hex, double alpha = 1)
    {
        var c = ThemeManager.Parse(hex);
        var b = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    protected static RadialGradientBrush Glow(string hex, double centerAlpha, double midAlpha = -1)
    {
        var c = ThemeManager.Parse(hex);
        var b = new RadialGradientBrush();
        b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(centerAlpha * 255), c.R, c.G, c.B), 0));
        if (midAlpha >= 0) b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(midAlpha * 255), c.R, c.G, c.B), 0.45));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        b.Freeze();
        return b;
    }

    protected static LinearGradientBrush Vertical(string top, string bottom)
    {
        var b = new LinearGradientBrush(ThemeManager.Parse(top), ThemeManager.Parse(bottom), 90);
        b.Freeze();
        return b;
    }

    protected static double Frac(double v) => v - Math.Floor(v);
}

public sealed record SceneInfo(string Id, string Name, bool Oled, Func<Scene> Make);

public static class Scenes
{
    public static readonly SceneInfo[] All =
    {
        new("aurora", "Aurora", false, () => new AuroraScene("#03060C", "#2EE6A6", "#1FA2FF", "#8A5CFF", "#00D1B2")),
        new("aurora-sunset", "Sunset haze", false, () => new AuroraScene("#0C0507", "#FF7A59", "#FF3D7F", "#9B4DFF", "#FFC15E")),
        new("aurora-ocean", "Deep ocean", false, () => new AuroraScene("#020611", "#0A84FF", "#00C2FF", "#004E92", "#2EC5CE")),
        new("aurora-rose", "Rosé", false, () => new AuroraScene("#0D0609", "#FF8FAB", "#FFB3C6", "#C77DFF", "#FFC8A2")),
        new("aurora-oled", "Midnight glow", true, () => new AuroraScene("#000000", "#3A86FF", "#8338EC", "#06D6A0", "#3A0CA3", 0.4)),
        new("stars", "Starfield", true, () => new StarfieldScene()),
        new("fireflies", "Fireflies", true, () => new FirefliesScene()),
        new("waves", "Light waves", true, () => new WavesScene()),
        new("constellation", "Constellation", true, () => new ConstellationScene()),
        new("matrix", "Code rain", true, () => new MatrixScene()),
        new("rain", "Rainy night", false, () => new RainScene()),
        new("lava", "Lava lamp", false, () => new LavaScene()),
        new("bokeh", "Bokeh", false, () => new BokehScene()),
    };

    public const string Prefix = "scene:";

    public static bool IsScene(string? path) => path != null && path.StartsWith(Prefix, StringComparison.Ordinal);

    public static Scene? Create(string pathOrId)
    {
        var id = IsScene(pathOrId) ? pathOrId[Prefix.Length..] : pathOrId;
        return All.FirstOrDefault(s => s.Id == id)?.Make();
    }

    public static string DisplayName(string path)
    {
        if (!IsScene(path)) return System.IO.Path.GetFileName(path);
        var id = path[Prefix.Length..];
        return All.FirstOrDefault(s => s.Id == id)?.Name ?? id;
    }
}

/// <summary>Draws a scene, ticking at the scene's frame rate while not paused.</summary>
public sealed class SceneView : FrameworkElement
{
    readonly Scene scene;
    readonly DispatcherTimer timer;
    readonly DateTime start = DateTime.UtcNow;
    readonly double offset;
    double frozenAt;
    bool paused;

    public SceneView(Scene scene, double startTime = 0, bool animate = true)
    {
        this.scene = scene;
        offset = startTime;
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / scene.Fps) };
        timer.Tick += (_, _) => InvalidateVisual();
        paused = !animate;
        frozenAt = startTime;
        Loaded += (_, _) => { if (!paused) timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }

    double Time => paused ? frozenAt : offset + (DateTime.UtcNow - start).TotalSeconds;

    public bool Paused
    {
        get => paused;
        set
        {
            if (value == paused) return;
            if (value) { frozenAt = Time; timer.Stop(); }
            paused = value;
            if (!value && IsLoaded) timer.Start();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        scene.Draw(dc, w, h, Time, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.Pop();
    }
}

// ============================================================ scenes

sealed class AuroraScene : Scene
{
    readonly SolidColorBrush bg;
    readonly RadialGradientBrush[] blobs;
    readonly LinearGradientBrush vignette;

    public AuroraScene(string bg, string a, string b, string c, string d, double strength = 0.55)
    {
        this.bg = Solid(bg);
        blobs = new[] { a, b, c, d, a }.Select(x => Glow(x, strength, strength * 0.45)).ToArray();
        vignette = Vertical("#00000000", "#99000000");
    }

    public override void Draw(DrawingContext dc, double w, double h, double t, double ppd)
    {
        var r = new Rect(0, 0, w, h);
        dc.DrawRectangle(bg, null, r);
        double m = Math.Max(w, h);
        for (int i = 0; i < blobs.Length; i++)
        {
            double cx = w * (0.5 + 0.38 * Math.Sin(t * 0.045 * (i + 1) + i * 1.7));
            double cy = h * (0.45 + 0.32 * Math.Cos(t * 0.037 * (i + 2) + i * 0.9));
            double rad = m * (0.32 + 0.08 * Math.Sin(t * 0.06 + i));
            dc.DrawEllipse(blobs[i], null, new Point(cx, cy), rad, rad * 0.7);
        }
        dc.DrawRectangle(vignette, null, r);
    }
}

sealed class StarfieldScene : Scene
{
    readonly (double a, double speed, double phase)[] stars;
    readonly SolidColorBrush[] levels;

    public StarfieldScene()
    {
        var rnd = new Random(7);
        stars = Enumerable.Range(0, 520).Select(_ => (rnd.NextDouble() * Math.PI * 2, 0.015 + rnd.NextDouble() * 0.05, rnd.NextDouble())).ToArray();
        levels = Enumerable.Range(1, 8).Select(i => Solid("#FFFFFF", i / 8.0)).ToArray();
    }

    public override void Draw(DrawingContext dc, double w, double h, double t, double ppd)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
        double cx = w / 2, cy = h / 2, maxR = Math.Sqrt(cx * cx + cy * cy);
        foreach (var (a, speed, phase) in stars)
        {
            double f = Frac(phase + t * speed);
            double d = f * f * maxR;
            double x = cx + Math.Cos(a) * d, y = cy + Math.Sin(a) * d;
            double size = 0.3 + 2.0 * f;
            int level = Math.Clamp((int)(f * 3 * 8), 0, 7);
            dc.DrawEllipse(levels[level], null, new Point(x, y), size, size);
        }
    }
}

sealed class FirefliesScene : Scene
{
    readonly (double x, double y, double f1, double f2, double f3, double p)[] flies;
    readonly RadialGradientBrush[] glows;
    readonly SolidColorBrush core = Solid("#FFF6C4");

    public FirefliesScene()
    {
        var rnd = new Random(11);
        flies = Enumerable.Range(0, 80).Select(_ => (rnd.NextDouble(), rnd.NextDouble(), 0.1 + rnd.NextDouble() * 0.25, 0.08 + rnd.NextDouble() * 0.2, 0.5 + rnd.NextDouble() * 1.4, rnd.NextDouble() * 10)).ToArray();
        glows = Enumerable.Range(1, 6).Select(i => Glow("#FFD45E", 0.12 * i, 0.04 * i)).ToArray();
    }

    public override void Draw(DrawingContext dc, double w, double h, double t, double ppd)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
        foreach (var (x0, y0, f1, f2, f3, p) in flies)
        {
            double x = w * (x0 + 0.06 * Math.Sin(t * f1 + p) + 0.03 * Math.Sin(t * f2 * 1.7 + p * 2));
            double y = h * (y0 + 0.05 * Math.Cos(t * f2 + p) + 0.02 * Math.Sin(t * f1 * 2.3));
            double glow = 0.5 + 0.5 * Math.Sin(t * f3 + p);
            int level = Math.Clamp((int)(glow * 6), 0, 5);
            dc.DrawEllipse(glows[level], null, new Point(x, y), 18, 18);
            dc.DrawEllipse(core, null, new Point(x, y), 1.4 + glow, 1.4 + glow);
        }
    }
}

sealed class WavesScene : Scene
{
    readonly Pen[] pens;
    readonly (double amp, double k, double speed, double phase, double offset)[] lines;

    public WavesScene()
    {
        string[][] colors =
        {
            new[] { "#00F5D4", "#00BBF9" }, new[] { "#9B5DE5", "#F15BB5" }, new[] { "#00BBF9", "#9B5DE5" },
            new[] { "#F15BB5", "#FEE440" }, new[] { "#00F5D4", "#9B5DE5" }, new[] { "#FEE440", "#F15BB5" }, new[] { "#4CC9F0", "#7209B7" },
        };
        pens = colors.Select((c, i) =>
        {
            var b = new LinearGradientBrush(ThemeManager.Parse(c[0]), ThemeManager.Parse(c[1]), 0);
            b.Freeze();
            var p = new Pen(b, 2.2 - i * 0.12) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            p.Freeze();
            return p;
        }).ToArray();
        var rnd = new Random(3);
        lines = pens.Select((_, i) => (60 + rnd.NextDouble() * 90, 0.002 + rnd.NextDouble() * 0.004, 0.25 + rnd.NextDouble() * 0.4, rnd.NextDouble() * 6, (i - 3) * 14.0)).ToArray();
    }

    public override void Draw(DrawingContext dc, double w, double h, double t, double ppd)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
        for (int i = 0; i < lines.Length; i++)
        {
            var (amp, k, speed, phase, off) = lines[i];
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                for (double x = -10; x <= w + 10; x += 14)
                {
                    double y = h * 0.55 + off + amp * Math.Sin(x * k + t * speed + phase) + amp * 0.35 * Math.Sin(x * k * 2.3 - t * speed * 0.6);
                    if (x < -9) ctx.BeginFigure(new Point(x, y), false, false);
                    else ctx.LineTo(new Point(x, y), true, true);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pens[i], g);
        }
    }
}

sealed class ConstellationScene : Scene
{
    readonly (double x, double y, double fx, double fy, double p)[] pts;
    readonly Pen[] pens;
    readonly SolidColorBrush dot = Solid("#CFE3FF", 0.9);
    readonly SolidColorBrush bg = Solid("#02040A");

    public ConstellationScene()
    {
        var rnd = new Random(5);
        pts = Enumerable.Range(0, 90).Select(_ => (rnd.NextDouble(), rnd.NextDouble(), 0.02 + rnd.NextDouble() * 0.05, 0.02 + rnd.NextDouble() * 0.05, rnd.NextDouble() * 10)).ToArray();
        pens = Enumerable.Range(1, 5).Select(i => { var p = new Pen(Solid("#7FB2FF", 0.09 * i), 1); p.Freeze(); return p; }).ToArray();
    }

    public override void Draw(DrawingContext dc, double w, double h, double t, double ppd)
    {
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        var p = pts.Select(q => new Point(w * (0.5 + 0.48 * Math.Sin(t * q.fx + q.p) * Math.Cos(q.x * 6)), h * (0.5 + 0.48 * Math.Sin(t * q.fy + q.p * 1.3) * Math.Cos(q.y * 6)))).ToArray();
        double link = Math.Max(w, h) * 0.12;
        for (int i = 0; i < p.Length; i++)
            for (int j = i + 1; j < p.Length; j++)
            {
                double dx = p[i].X - p[j].X, dy = p[i].Y - p[j].Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d >= link) continue;
                int level = Math.Clamp((int)((1 - d / link) * 5), 0, 4);
                dc.DrawLine(pens[level], p[i], p[j]);
            }
        foreach (var pt in p) dc.DrawEllipse(dot, null, pt, 1.8, 1.8);
    }
}

sealed class MatrixScene : Scene
{
    public override int Fps => 20;
    const string Glyphs = "ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜ0123456789";
    readonly Dictionary<(char, int), FormattedText> cache = new();
    readonly SolidColorBrush[] levels = Enumerable.Range(1, 6).Select(i => Solid("#00FF66", i / 6.0)).ToArray();
    readonly SolidColorBrush head = Solid("#D8FFE6");
    readonly Typeface face = new("MS Gothic");
    double cachedPpd;

    FormattedText Glyph(char c, int level, double ppd)
    {
        if (ppd != cachedPpd) { cache.Clear(); cachedPpd = ppd; }
        if (!cache.TryGetValue((c, level), out var ft))
        {
            ft = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 18,
                level < 0 ? head : levels[level], ppd);
            cache[(c, level)] = ft;
        }
        return ft;
    }

    static uint Hash(int a, int b, int c) { unchecked { uint x = (uint)(a * 73856093 ^ b * 19349663 ^ c * 83492791); x ^= x >> 13; x *= 0x5bd1e995; return x ^ (x >> 15); } }

    public override void Draw(DrawingContext dc, double w, double h, double t, double ppd)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
        const double cell = 24;
        int cols = (int)(w / cell) + 1, rows = (int)(h / cell) + 1;
        const int trail = 16;
        for (int c = 0; c < cols; c++)
        {
            uint seed = Hash(c, 1, 2);
            double speed = 6 + seed % 9;
            double headRow = Frac(t * speed / (rows + trail) + (seed % 1000) / 1000.0) * (rows + trail);
            int top = (int)headRow;
            for (int k = 0; k < trail; k++)
            {
                int row = top - k;
                if (row < 0 || row >= rows) continue;
                char ch = Glyphs[(int)(Hash(c, row, (int)(t * 3) + k / 5) % Glyphs.Length)];
                int level = k == 0 ? -1 : Math.Clamp(5 - k * 6 / trail, 0, 5);
                dc.DrawText(Glyph(ch, level, ppd), new Point(c * cell, row * cell));
            }
        }
    }
}

sealed class RainScene : Scene
{
    readonly (double x, double y, double speed, double len)[] drops;
    readonly Pen[] pens;
    readonly LinearGradientBrush bg = Vertical("#0A0F1A", "#18222F");
    readonly RadialGradientBrush glow = Glow("#3A6EA5", 0.25);

    public RainScene()
    {
        var rnd = new Random(9);
        drops = Enumerable.Range(0, 320).Select(_ => (rnd.NextDouble(), rnd.NextDouble(), 0.5 + rnd.NextDouble() * 0.7, 18 + rnd.NextDouble() * 38)).ToArray();
        pens = Enumerable.Range(1, 4).Select(i => { var p = new Pen(Solid("#A9C7E8", 0.1 * i + 0.05), 1.1); p.Freeze(); return p; }).ToArray();
    }

    public override void Draw(DrawingContext dc, double w, double h, double t, double ppd)
    {
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        dc.DrawEllipse(glow, null, new Point(w * 0.7, h * 0.25), w * 0.5, h * 0.5);
        for (int i = 0; i < drops.Length; i++)
        {
            var (x0, y0, speed, len) = drops[i];
            double y = (Frac(y0 + t * speed * 0.6) * 1.2 - 0.1) * h;
            double x = x0 * w * 1.1 + y * 0.12 - w * 0.05;
            dc.DrawLine(pens[i % 4], new Point(x, y), new Point(x - len * 0.12, y - len));
        }
    }
}

sealed class LavaScene : Scene
{
    readonly (double x, double size, double speed, double phase)[] blobs;
    readonly RadialGradientBrush[] brushes;
    readonly LinearGradientBrush bg = Vertical("#12040A", "#2A0A12");

    public LavaScene()
    {
        var rnd = new Random(13);
        blobs = Enumerable.Range(0, 9).Select(_ => (0.15 + rnd.NextDouble() * 0.7, 0.08 + rnd.NextDouble() * 0.1, 0.05 + rnd.NextDouble() * 0.07, rnd.NextDouble() * 10)).ToArray();
        brushes = new[] { "#FF5E3A", "#FF2A68", "#FF9F1C", "#FF4E50" }.Select(c => Glow(c, 0.85, 0.6)).ToArray();
    }

    public override void Draw(DrawingContext dc, double w, double h, double t, double ppd)
    {
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        double m = Math.Min(w, h);
        for (int i = 0; i < blobs.Length; i++)
        {
            var (x, size, speed, phase) = blobs[i];
            double cy = h * (0.5 + 0.48 * Math.Sin(t * speed + phase));
            double cx = w * (x + 0.04 * Math.Sin(t * speed * 2.1 + phase));
            double r = m * size * (1 + 0.15 * Math.Sin(t * 0.3 + i));
            dc.DrawEllipse(brushes[i % brushes.Length], null, new Point(cx, cy), r, r * 1.15);
        }
    }
}

sealed class BokehScene : Scene
{
    readonly (double x, double y, double r, double speed, double p)[] dots;
    readonly RadialGradientBrush[] brushes;
    readonly LinearGradientBrush bg = Vertical("#0B0718", "#170A1F");

    public BokehScene()
    {
        var rnd = new Random(21);
        dots = Enumerable.Range(0, 46).Select(_ => (rnd.NextDouble(), rnd.NextDouble(), 30 + rnd.NextDouble() * 120, 0.01 + rnd.NextDouble() * 0.025, rnd.NextDouble() * 10)).ToArray();
        brushes = new[] { "#FF9EC4", "#8EC5FF", "#FFD38E", "#B79CFF", "#7FFFD4" }.Select(c => Glow(c, 0.32, 0.22)).ToArray();
    }

    public override void Draw(DrawingContext dc, double w, double h, double t, double ppd)
    {
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        for (int i = 0; i < dots.Length; i++)
        {
            var (x, y, r, speed, p) = dots[i];
            double cy = (1.2 - Frac(y + t * speed) * 1.4) * h;
            double cx = w * (x + 0.03 * Math.Sin(t * 0.2 + p));
            dc.DrawEllipse(brushes[i % brushes.Length], null, new Point(cx, cy), r, r);
        }
    }
}
