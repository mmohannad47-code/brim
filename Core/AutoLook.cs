using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Brim.Core;

/// <summary>
/// Accent colour picked from the wallpaper (a Windows picture, or the Wallpaper Engine scene/video that's showing),
/// and switching between a day and a night theme at sunrise and sunset.
/// </summary>
public static class AutoLook
{
    static readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(20) };
    static string lastSource = "";
    static AppSettings S => SettingsStore.Current;

    /// <summary>Raised when the theme or accent changed so windows can restyle.</summary>
    public static event Action? Changed;

    public static void Start()
    {
        timer.Tick += (_, _) => Check();
        timer.Start();
        Check();
    }

    /// <summary>Re-check now (after changing a setting, or a new wallpaper).</summary>
    public static void Check(bool force = false)
    {
        if (force) lastSource = "";
        try
        {
            bool changed = false;
            if (S.AutoTheme) changed |= ApplyDayNight();
            if (S.AccentFromWallpaper) changed |= ApplyWallpaperAccent();
            if (changed)
            {
                SettingsStore.Save();
                ThemeManager.Apply();
                Changed?.Invoke();
            }
        }
        catch (Exception ex) { Log.Error("auto look", ex); }
    }

    // ---------- day / night ----------

    public static bool IsDaytime()
    {
        var now = DateTime.Now;
        var w = WeatherService.Current;
        TimeSpan rise = w?.Sunrise?.TimeOfDay ?? new TimeSpan(7, 0, 0);
        TimeSpan set = w?.Sunset?.TimeOfDay ?? new TimeSpan(19, 0, 0);
        return now.TimeOfDay >= rise && now.TimeOfDay < set;
    }

    static bool ApplyDayNight()
    {
        var want = IsDaytime() ? S.DayTheme : S.NightTheme;
        if (string.IsNullOrEmpty(want) || S.Theme == want) return false;
        S.Theme = want;
        return true;
    }

    // ---------- accent from wallpaper ----------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(int action, int param, StringBuilder value, int winIni);

    /// <summary>The picture that best shows what's on the desktop right now.</summary>
    public static string? WallpaperSource()
    {
        // Wallpaper Engine draws over the Windows wallpaper while it runs
        if (WallpaperEngine.Running && WallpaperEngine.CurrentPreview() is { } we) return we;
        var sb = new StringBuilder(520);
        if (SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, sb.Capacity, sb, 0) && File.Exists(sb.ToString())) return sb.ToString();
        var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    static bool ApplyWallpaperAccent()
    {
        var src = WallpaperSource();
        if (src == null) return false;
        var stamp = src + "|" + File.GetLastWriteTimeUtc(src).Ticks;
        if (stamp == lastSource) return false;
        lastSource = stamp;
        var c = Pick(src);
        if (c == null) return false;
        var hex = $"#{c.Value.R:X2}{c.Value.G:X2}{c.Value.B:X2}";
        if (string.Equals(S.Accent, hex, StringComparison.OrdinalIgnoreCase)) return false;
        S.Accent = hex;
        return true;
    }

    /// <summary>The most common lively colour in the picture, tuned to read well on Brim's dark surfaces.</summary>
    public static Color? Pick(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 64; // tiny: fast and light on memory
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            // 36 hue buckets, weighted by how colourful each pixel is
            var weight = new double[36];
            var sumR = new double[36]; var sumG = new double[36]; var sumB = new double[36];
            double grey = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double sat = max == 0 ? 0 : (max - min) / max;
                if (sat < 0.25 || max < 0.25) { grey++; continue; }
                double hue = Hue(r, g, b, max, min);
                int k = (int)(hue / 10) % 36;
                double wgt = sat * sat * max;
                weight[k] += wgt; sumR[k] += r * wgt; sumG[k] += g * wgt; sumB[k] += b * wgt;
            }
            int best = -1; double bestW = 0;
            for (int k = 0; k < 36; k++)
            {
                // neighbours count too, so a gradient isn't split across buckets
                double wk = weight[k] + 0.5 * (weight[(k + 35) % 36] + weight[(k + 1) % 36]);
                if (wk > bestW) { bestW = wk; best = k; }
            }
            if (best < 0 || weight[best] <= 0) return Color.FromRgb(0xE6, 0xE6, 0xEA); // black-and-white wallpaper: soft white accent
            double R = sumR[best] / weight[best], G = sumG[best] / weight[best], B = sumB[best] / weight[best];
            return Readable(R, G, B);
        }
        catch (Exception ex) { Log.Error("accent pick", ex); return null; }
    }

    static double Hue(double r, double g, double b, double max, double min)
    {
        double d = max - min;
        if (d == 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>Keep the hue, but make it bright and colourful enough to stand out on black.</summary>
    static Color Readable(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = Hue(r, g, b, max, min);
        double s = max == 0 ? 0 : (max - min) / max;
        s = Math.Clamp(s, 0.45, 0.75);
        double v = Math.Clamp(max, 0.88, 1.0);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double rr, double gg, double bb) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((rr + m) * 255), (byte)((gg + m) * 255), (byte)((bb + m) * 255));
    }
}
