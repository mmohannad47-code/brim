using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Brim.Core;

public sealed class WallItem : Observable
{
    public string Id { get; init; } = "";
    public bool IsVideo { get; init; }
    public string? ThumbUrl { get; init; }
    public string? FullUrl { get; init; }
    public string Label { get; init; } = "";
    public string Credit { get; init; } = "";
    public string Ext { get; init; } = ".jpg";

    string? localPath;
    public string? LocalPath { get => localPath; set => Set(ref localPath, value); }

    public string FileName => Id + Ext;
}

/// <summary>
/// Browses wallpapers online and downloads only the one you pick.
/// Pictures come from wallhaven.cc (no account needed, safe-for-work only); live videos from Pexels (free API key).
/// </summary>
public static class Wallpapers
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
    static readonly SemaphoreSlim thumbGate = new(6);
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim", "Wallpapers");

    static Wallpapers() => http.DefaultRequestHeaders.UserAgent.ParseAdd("Brim/0.2 (desktop wallpaper picker)");

    /// <param name="oled">Only pictures that are mostly true black, which look best (and save power) on OLED screens.</param>
    public static async Task<List<WallItem>> Pictures(string? query, bool anime, int page, bool oled = false)
    {
        var url = new StringBuilder("https://wallhaven.cc/api/v1/search?purity=100&atleast=1920x1080&ratios=landscape");
        url.Append(anime ? "&categories=010" : oled ? "&categories=110" : "&categories=100");
        if (oled) url.Append("&colors=000000");
        if (string.IsNullOrWhiteSpace(query)) url.Append("&sorting=toplist&topRange=1M");
        else url.Append("&sorting=favorites&q=").Append(Uri.EscapeDataString(query));
        url.Append("&page=").Append(page);

        using var doc = JsonDocument.Parse(await http.GetStringAsync(url.ToString()));
        var list = new List<WallItem>();
        foreach (var w in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            string id = w.GetProperty("id").GetString()!;
            string path = w.GetProperty("path").GetString()!;
            list.Add(Mark(new WallItem
            {
                Id = "wh-" + id,
                FullUrl = path,
                ThumbUrl = w.GetProperty("thumbs").GetProperty("small").GetString(),
                Label = w.GetProperty("resolution").GetString() ?? "",
                Ext = Path.GetExtension(path),
                Credit = "wallhaven.cc",
            }));
        }
        return list;
    }

    /// <summary>Video wallpapers from Pixabay (free key from a Pixabay account).</summary>
    public static async Task<List<WallItem>> Videos(string? query, int page)
    {
        var key = ApiKeys.Pixabay;
        if (key.Length == 0) throw new InvalidOperationException("no key");
        string url = $"https://pixabay.com/api/videos/?key={Uri.EscapeDataString(key)}&per_page=24&page={page}&safesearch=true&order=popular" +
                     (string.IsNullOrWhiteSpace(query) ? "&q=loop+background" : "&q=" + Uri.EscapeDataString(query));
        using var res = await http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            if (body.Contains("key", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("bad key");
            res.EnsureSuccessStatusCode();
        }
        using var doc = JsonDocument.Parse(body);

        int maxWidth = Math.Max(1920, Native.GetSystemMetrics(0));
        var list = new List<WallItem>();
        foreach (var v in doc.RootElement.GetProperty("hits").EnumerateArray())
        {
            var sizes = v.GetProperty("videos");
            var files = new List<(int w, int h, string url, string? thumb)>();
            foreach (var name in new[] { "large", "medium", "small" })
            {
                if (!sizes.TryGetProperty(name, out var s)) continue;
                var link = s.TryGetProperty("url", out var u) ? u.GetString() : null;
                int w = s.TryGetProperty("width", out var wd) && wd.ValueKind == JsonValueKind.Number ? wd.GetInt32() : 0;
                int h = s.TryGetProperty("height", out var ht) && ht.ValueKind == JsonValueKind.Number ? ht.GetInt32() : 0;
                string? thumb = s.TryGetProperty("thumbnail", out var th) ? th.GetString() : null;
                if (!string.IsNullOrEmpty(link) && w >= h && w > 0) files.Add((w, h, link!, thumb));
            }
            if (files.Count == 0) continue;
            var pick = files.Where(f => f.w <= maxWidth).OrderByDescending(f => f.w).FirstOrDefault();
            if (pick.url == null) pick = files.OrderBy(f => f.w).First();
            string? thumbUrl = files.Select(f => f.thumb).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            if (thumbUrl == null && v.TryGetProperty("picture_id", out var pic) && pic.GetString() is { Length: > 0 } pid)
                thumbUrl = $"https://i.vimeocdn.com/video/{pid}_640x360.jpg";
            string author = v.TryGetProperty("user", out var user) ? user.GetString() ?? "" : "";
            list.Add(Mark(new WallItem
            {
                Id = "pb-" + v.GetProperty("id").GetInt64(),
                IsVideo = true,
                FullUrl = pick.url,
                ThumbUrl = thumbUrl,
                Label = $"{v.GetProperty("duration").GetInt32()}s · {pick.w}×{pick.h}",
                Ext = ".mp4",
                Credit = author.Length > 0 ? $"{author} on Pixabay" : "Pixabay",
            }));
        }
        return list;
    }

    static WallItem Mark(WallItem item)
    {
        var p = Path.Combine(Folder, item.FileName);
        if (File.Exists(p)) item.LocalPath = p;
        return item;
    }

    public static List<WallItem> Local()
    {
        var list = new List<WallItem>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var f in new DirectoryInfo(Folder).GetFiles().OrderByDescending(f => f.LastWriteTime))
        {
            var ext = f.Extension.ToLowerInvariant();
            bool video = ext is ".mp4" or ".webm" or ".mkv" or ".mov" or ".wmv" or ".avi";
            bool picture = ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp";
            if (!video && !picture) continue;
            if (picture && File.Exists(Path.ChangeExtension(f.FullName, ".mp4"))) continue; // that's a video's preview
            string? thumb = video ? Path.ChangeExtension(f.FullName, ".jpg") : f.FullName;
            if (thumb != null && !File.Exists(thumb)) thumb = null;
            list.Add(new WallItem
            {
                Id = Path.GetFileNameWithoutExtension(f.Name),
                IsVideo = video,
                ThumbUrl = thumb,
                Label = video ? "Live" : $"{f.Length / 1048576.0:0.0} MB",
                Ext = f.Extension,
                LocalPath = f.FullName,
            });
        }
        return list;
    }

    public static async Task<string> Download(WallItem item, IProgress<double> progress)
    {
        Directory.CreateDirectory(Folder);
        var dest = Path.Combine(Folder, item.FileName);
        if (File.Exists(dest)) return dest;
        var tmp = dest + ".part";
        using (var res = await http.GetAsync(item.FullUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            res.EnsureSuccessStatusCode();
            long total = res.Content.Headers.ContentLength ?? -1;
            await using var src = await res.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmp);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }
        File.Move(tmp, dest, true);
        if (item.IsVideo && item.ThumbUrl != null)
        {
            try { await File.WriteAllBytesAsync(Path.ChangeExtension(dest, ".jpg"), await http.GetByteArrayAsync(item.ThumbUrl)); }
            catch { }
        }
        item.LocalPath = dest;
        return dest;
    }

    public static async Task<ImageSource?> Thumb(string source, int width)
    {
        await thumbGate.WaitAsync();
        try
        {
            byte[] bytes = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await http.GetByteArrayAsync(source)
                : await File.ReadAllBytesAsync(source);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = width;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
        finally { thumbGate.Release(); }
    }

    // Setting the wallpaper makes Windows message every open window and wait for each to answer.
    // One frozen app (it was a stuck VS Code updater) would freeze Brim with it, so this always runs off the UI thread.

    public static void ApplyPicture(string path)
    {
        Task.Run(() =>
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    k?.SetValue("WallpaperStyle", "10"); // fill
                    k?.SetValue("TileWallpaper", "0");
                }
                Native.SystemParametersInfo(0x0014 /* SPI_SETDESKWALLPAPER */, 0, path, 0x1 | 0x2);
            }
            catch (Exception ex) { Log.Error("set wallpaper", ex); }
        });
    }

    /// <summary>Repaint the normal wallpaper (after a live one stops).</summary>
    public static void RefreshDesktop()
    {
        Task.Run(() =>
        {
            try
            {
                var sb = new StringBuilder(520);
                if (Native.SystemParametersInfoGet(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0))
                    Native.SystemParametersInfo(0x0014, 0, sb.ToString(), 0x2);
            }
            catch (Exception ex) { Log.Error("refresh wallpaper", ex); }
        });
    }

    public static string? Current()
    {
        var sb = new StringBuilder(520);
        return Native.SystemParametersInfoGet(0x0073, (uint)sb.Capacity, sb, 0) ? sb.ToString() : null;
    }
}

/// <summary>Runs the live wallpaper in its own small process so the main app stays light, and so video memory is only used while one is on.</summary>
public static class LiveWallpaperHost
{
    static Process? proc;

    public static bool Running => proc is { HasExited: false };

    public static bool IsValid(string? path) => path != null && (Scenes.Create(path) != null || File.Exists(path));

    public static void Start(string path)
    {
        Stop(refresh: false);
        if (!IsValid(path)) return;
        try
        {
            proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                ArgumentList = { "--live", path, Environment.ProcessId.ToString() },
            });
        }
        catch (Exception ex) { Log.Error("live start", ex); }
    }

    public static void Stop(bool refresh = true)
    {
        var old = proc;
        proc = null;
        try
        {
            if (old is { HasExited: false })
            {
                old.Kill();
                if (!refresh) old.WaitForExit(1500); // switching: let the old one leave the desktop first
            }
        }
        catch { }
        if (refresh) Wallpapers.RefreshDesktop();
    }
}
