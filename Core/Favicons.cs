using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Brim.Core;

/// <summary>
/// The logo of a website you added as a shortcut: read from the site itself (its touch icon, the icons its page
/// declares, or /favicon.ico; no third-party service), kept on disk so it's there straight away next time.
/// </summary>
public static class Favicons
{
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim", "Favicons");
    static readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(8) };
    static readonly Dictionary<string, ImageSource?> memory = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Task<ImageSource?>> pending = new(StringComparer.OrdinalIgnoreCase);

    static Favicons()
    {
        // Some sites only answer browsers
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    }

    static string? Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;

    static string FileFor(string host) => Path.Combine(Folder, Regex.Replace(host, "[^a-z0-9.-]", "_") + ".png");

    /// <summary>The logo if it's already been fetched (memory or disk), without waiting on the network.</summary>
    public static ImageSource? Cached(string url)
    {
        var host = Host(url);
        if (host == null) return null;
        if (memory.TryGetValue(host, out var hit)) return hit;
        var file = FileFor(host);
        if (!File.Exists(file)) return null;
        var img = Load(File.ReadAllBytes(file));
        memory[host] = img;
        return img;
    }

    /// <summary>Fetch the site's logo (once per site, however many shortcuts ask).</summary>
    public static Task<ImageSource?> GetAsync(string url)
    {
        var host = Host(url);
        if (host == null) return Task.FromResult<ImageSource?>(null);
        if (Cached(url) is { } have) return Task.FromResult<ImageSource?>(have);
        if (pending.TryGetValue(host, out var running)) return running;
        var t = Fetch(url, host);
        pending[host] = t;
        return t;
    }

    static async Task<ImageSource?> Fetch(string url, string host)
    {
        try
        {
            var page = new Uri(url);
            var candidates = new List<(Uri uri, int score)>();
            try
            {
                using var res = await http.GetAsync(page, HttpCompletionOption.ResponseHeadersRead);
                var final = res.RequestMessage?.RequestUri ?? page;
                var stream = await res.Content.ReadAsStreamAsync();
                var buf = new byte[256 * 1024]; // the <head> is enough
                int read = 0, n;
                while (read < buf.Length && (n = await stream.ReadAsync(buf.AsMemory(read, buf.Length - read))) > 0) read += n;
                var html = System.Text.Encoding.UTF8.GetString(buf, 0, read);
                foreach (Match m in Regex.Matches(html, "<link\\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    var tag = m.Value;
                    var rel = Regex.Match(tag, "rel\\s*=\\s*[\"']?([^\"'>]+)", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
                    if (!rel.Contains("icon")) continue;
                    var href = Regex.Match(tag, "href\\s*=\\s*[\"']?([^\"' >]+)", RegexOptions.IgnoreCase).Groups[1].Value;
                    if (href.Length == 0 || href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || href.StartsWith("data:")) continue;
                    if (!Uri.TryCreate(final, System.Net.WebUtility.HtmlDecode(href), out var abs)) continue;
                    int size = int.TryParse(Regex.Match(tag, "sizes\\s*=\\s*[\"']?(\\d+)", RegexOptions.IgnoreCase).Groups[1].Value, out var s) ? s : 0;
                    int score = (rel.Contains("apple-touch") ? 1000 : 0) + Math.Min(size, 512) + (abs.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? 50 : 0);
                    candidates.Add((abs, score));
                }
                candidates.Add((new Uri(final, "/apple-touch-icon.png"), 400));
                candidates.Add((new Uri(final, "/favicon.ico"), 10));
            }
            catch
            {
                candidates.Add((new Uri(page, "/favicon.ico"), 10));
            }

            foreach (var (uri, _) in candidates.OrderByDescending(c => c.score).DistinctBy(c => c.uri))
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(uri);
                    if (bytes.Length < 64) continue;
                    var img = Load(bytes);
                    if (img == null) continue;
                    Directory.CreateDirectory(Folder);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create((BitmapSource)img));
                    using (var f = File.Create(FileFor(host))) enc.Save(f);
                    memory[host] = img;
                    return img;
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Error("favicon " + host, ex); }
        finally { pending.Remove(host); }
        memory[host] = null;
        return null;
    }

    /// <summary>A picture from PNG / ICO / JPG / GIF bytes; for .ico files, the biggest size inside.</summary>
    static ImageSource? Load(byte[] bytes)
    {
        try
        {
            var dec = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = dec.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            if (frame == null || frame.PixelWidth < 8) return null;
            BitmapSource result = frame;
            if (frame.PixelWidth > 128) result = new TransformedBitmap(frame, new ScaleTransform(96.0 / frame.PixelWidth, 96.0 / frame.PixelWidth));
            result.Freeze();
            return result;
        }
        catch { return null; }
    }
}
