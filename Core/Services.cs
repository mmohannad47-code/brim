using System.Collections.Generic;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Brim.Core;

/// <summary>Hidden message-only window for global hotkeys and clipboard notifications.</summary>
public sealed class MessageWindow : IDisposable
{
    readonly HwndSource src;
    public event Action<int>? Hotkey;
    public event Action? ClipboardChanged;

    public MessageWindow()
    {
        var p = new HwndSourceParameters("BrimMessages") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) };
        src = new HwndSource(p);
        src.AddHook(WndProc);
        Native.AddClipboardFormatListener(src.Handle);
    }

    public bool Register(int id, uint mods, uint vk) => Native.RegisterHotKey(src.Handle, id, mods | Native.MOD_NOREPEAT, vk);

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY) { Hotkey?.Invoke(w.ToInt32()); handled = true; }
        else if (msg == Native.WM_CLIPBOARDUPDATE) { ClipboardChanged?.Invoke(); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Native.RemoveClipboardFormatListener(src.Handle);
        for (int i = 1; i < 32; i++) Native.UnregisterHotKey(src.Handle, i);
        src.Dispose();
    }
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void RaiseChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ClipItem : Observable
{
    public string Text { get; init; } = "";
    public string[]? Files { get; init; }
    /// <summary>A copied picture or screenshot, kept PNG-compressed so it stays small in memory.</summary>
    public byte[]? Png { get; init; }
    public ImageSource? Thumb { get; init; }
    public System.Windows.Visibility ThumbVisibility => Thumb != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility GlyphVisibility => Thumb != null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public double RowHeight => Thumb != null ? 52 : 34;

    bool pinned;
    /// <summary>Pinned items stay at the top and are never pushed out by new copies.</summary>
    public bool Pinned
    {
        get => pinned;
        set { Set(ref pinned, value); RaiseChanged(nameof(PinVisibility)); }
    }
    public System.Windows.Visibility PinVisibility => pinned ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public DateTime When { get; init; } = DateTime.Now;
    public string Preview => Png != null ? Text : Files != null
        ? (Files.Length == 1 ? Path.GetFileName(Files[0]) : $"{Files.Length} files · {Path.GetFileName(Files[0])}…")
        : string.Join(" ", Text.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries)).Truncate(160);
    public string Glyph => Files != null ? "" : IconHelper.IsUrl(Text.Trim()) ? "" : "";
    public string Ago
    {
        get
        {
            var d = DateTime.Now - When;
            if (d.TotalMinutes < 1) return "now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours}h";
            return When.ToString("d MMM");
        }
    }
}

/// <summary>Remembers the last things you copied (in memory only, never written to disk).</summary>
public static class ClipboardHistory
{
    public static ObservableCollection<ClipItem> Items { get; } = new();
    const int Max = 40;

    public static async void OnChanged()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return;
                // Password managers mark secrets like this; respect it
                if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") ||
                    data.GetDataPresent("Clipboard Viewer Ignore")) return;

                if (suppressNext) { suppressNext = false; return; }
                ClipItem? item = null;
                if (Clipboard.ContainsImage() && !data.GetDataPresent(DataFormats.FileDrop))
                {
                    item = ImageItem(Clipboard.GetImage());
                    if (item == null) return;
                    InsertTop(item);
                    // Pictures are big; keep only the newest few
                    foreach (var old in Items.Where(i => i.Png != null && !i.Pinned).Skip(MaxImages).ToList()) Items.Remove(old);
                    Trim();
                    ImageCaptured?.Invoke(item);
                    return;
                }
                if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                    item = new ClipItem { Files = files, Text = string.Join("\n", files) };
                else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string t && !string.IsNullOrWhiteSpace(t))
                    item = new ClipItem { Text = t };
                if (item == null) return;

                var existing = Items.FirstOrDefault(i => i.Png == null && i.Text == item.Text);
                if (existing != null)
                {
                    if (existing.Pinned) return; // already kept at the top
                    Items.Remove(existing);
                }
                InsertTop(item);
                Trim();
                Changed();
                return;
            }
            catch { await Task.Delay(60); }
        }
    }

    const int MaxImages = 8;
    static bool suppressNext;

    /// <summary>A picture or screenshot just landed on the clipboard (the screenshot button listens for this).</summary>
    public static event Action<ClipItem>? ImageCaptured;

    /// <summary>New items go just below the pinned ones.</summary>
    static void InsertTop(ClipItem item) => Items.Insert(Items.TakeWhile(i => i.Pinned).Count(), item);

    static void Trim()
    {
        while (Items.Count(i => !i.Pinned) > Max)
        {
            var last = Items.LastOrDefault(i => !i.Pinned);
            if (last == null) break;
            Items.Remove(last);
        }
    }

    public static void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        Items.Remove(item);
        if (item.Pinned) Items.Insert(0, item);
        else InsertTop(item);
        Changed();
    }

    // ---------- remembering the history across restarts (opt-in, encrypted for this Windows account) ----------

    static readonly string StoreFile = Path.Combine(SettingsStore.Folder, "clipboard.dat");
    static System.Windows.Threading.DispatcherTimer? saveTimer;

    sealed record Saved(string Text, string[]? Files, bool Pinned, DateTime When);

    static void Changed()
    {
        if (!SettingsStore.Current.ClipRemember) return;
        saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        saveTimer.Tick -= SaveTick;
        saveTimer.Tick += SaveTick;
        saveTimer.Stop();
        saveTimer.Start();
    }

    static void SaveTick(object? s, EventArgs e) { saveTimer!.Stop(); Save(); }

    static bool loaded;

    public static void Save()
    {
        try
        {
            if (!SettingsStore.Current.ClipRemember) { if (File.Exists(StoreFile)) File.Delete(StoreFile); return; }
            if (!loaded) return; // quit before the saved history was read back: don't overwrite it with nothing
            // Text and file lists only; pictures stay in memory
            var list = Items.Where(i => i.Png == null).Select(i => new Saved(i.Text, i.Files, i.Pinned, i.When)).ToList();
            var json = JsonSerializer.SerializeToUtf8Bytes(list);
            var sealedBytes = System.Security.Cryptography.ProtectedData.Protect(json, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(SettingsStore.Folder);
            File.WriteAllBytes(StoreFile, sealedBytes);
        }
        catch (Exception ex) { Log.Error("clip save", ex); }
    }

    public static void Load()
    {
        loaded = true;
        try
        {
            if (!SettingsStore.Current.ClipRemember || !File.Exists(StoreFile)) return;
            var json = System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(StoreFile), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var list = JsonSerializer.Deserialize<List<Saved>>(json) ?? new();
            foreach (var s in list)
                Items.Add(new ClipItem { Text = s.Text, Files = s.Files, When = s.When, Pinned = s.Pinned });
        }
        catch (Exception ex) { Log.Error("clip load", ex); }
    }

    public static void Remove(ClipItem item)
    {
        Items.Remove(item);
        Changed();
    }

    /// <summary>Ctrl+Alt+V: paste what's on the clipboard as plain text (no fonts, colours or links).</summary>
    public static async void PastePlain()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            suppressNext = true;
            Clipboard.SetText(text);
            // Wait for Ctrl+Alt to be let go, or the target app would see Ctrl+Alt+V again
            for (int i = 0; i < 40 && ((GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0); i++)
                await Task.Delay(25);
            Native.TapKeys(0x11 /* Ctrl */, (byte)'V');
        }
        catch (Exception ex) { Log.Error("paste plain", ex); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    static ClipItem? ImageItem(System.Windows.Media.Imaging.BitmapSource? bmp)
    {
        if (bmp == null || bmp.PixelWidth < 2) return null;
        try
        {
            // Some apps put pictures on the clipboard with an empty alpha channel; treat them as opaque
            var src = new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            var png = ms.ToArray();

            var thumb = new System.Windows.Media.Imaging.BitmapImage();
            thumb.BeginInit();
            thumb.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            thumb.DecodePixelHeight = 96;
            thumb.StreamSource = new MemoryStream(png);
            thumb.EndInit();
            thumb.Freeze();
            return new ClipItem { Png = png, Thumb = thumb, Text = $"Picture · {bmp.PixelWidth} × {bmp.PixelHeight}" };
        }
        catch (Exception ex) { Log.Error("clip image", ex); return null; }
    }

    /// <summary>Another app can be holding the clipboard for a moment; try a few times before giving up.</summary>
    public static void CopyBack(ClipItem item)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (TryCopyBack(item, attempt == 5)) return;
            System.Threading.Thread.Sleep(40);
        }
    }

    static bool TryCopyBack(ClipItem item, bool last)
    {
        try
        {
            CopyBackOnce(item);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException) when (!last) { return false; }
        catch (Exception ex) { Log.Error("clip copy", ex); return true; }
    }

    static void CopyBackOnce(ClipItem item)
    {
        {
            if (item.Png != null)
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.StreamSource = new MemoryStream(item.Png);
                img.EndInit();
                img.Freeze();
                suppressNext = true; // don't add it to the history a second time
                Clipboard.SetImage(img);
                Items.Remove(item);
                Items.Insert(0, item);
                return;
            }
            if (item.Files != null)
            {
                var sc = new System.Collections.Specialized.StringCollection();
                sc.AddRange(item.Files);
                Clipboard.SetFileDropList(sc);
            }
            else Clipboard.SetText(item.Text);
        }
    }
}

public sealed class FileItem : Observable
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    public ImageSource? Icon => IconHelper.ForPath(Path);

    bool selected;
    public bool Selected
    {
        get => selected;
        set { Set(ref selected, value); RaiseChanged(nameof(SelectedVisibility)); }
    }
    public System.Windows.Visibility SelectedVisibility => selected ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}

/// <summary>Files parked on the notch so you can drag them somewhere else later.</summary>
public static class Shelf
{
    public static ObservableCollection<FileItem> Items { get; } = new();

    public static void Add(string[] paths)
    {
        foreach (var p in paths)
            if (!Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase)))
                Items.Add(new FileItem { Path = p });
    }
}

public sealed class WeatherNow
{
    public double Temp, High, Low;
    public int Code;
    public bool IsDay = true;
    public string Place = "";
    public string Unit = "°";
    public DateTime? Sunrise, Sunset;
    public string Description => WeatherService.Describe(Code);
    public string Glyph => WeatherService.Glyph(Code, IsDay);
}

/// <summary>Open-Meteo: free, no key, only contacted when you set a city.</summary>
public static class WeatherService
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12) };
    public static WeatherNow? Current { get; private set; }
    public static event Action? Updated;
    static DateTime lastFetch;

    static WeatherService() => http.DefaultRequestHeaders.UserAgent.ParseAdd("Brim/0.1");

    public static async Task<(double lat, double lon, string name)?> Geocode(string city)
    {
        try
        {
            var json = await http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(city)}");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
            var r = res[0];
            string name = r.GetProperty("name").GetString() ?? city;
            if (r.TryGetProperty("country", out var c)) name += ", " + c.GetString();
            return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), name);
        }
        catch (Exception ex) { Log.Error("geocode", ex); return null; }
    }

    /// <summary>Find a city with its time zone (for the world clock).</summary>
    public static async Task<CityItem?> FindCity(string query)
    {
        try
        {
            var json = await http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(query)}");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
            var r = res[0];
            string name = r.GetProperty("name").GetString() ?? query;
            if (r.TryGetProperty("country", out var c) && c.GetString() is { Length: > 0 } country) name += ", " + country;
            return new CityItem
            {
                Name = name,
                Lat = r.GetProperty("latitude").GetDouble(),
                Lon = r.GetProperty("longitude").GetDouble(),
                TimeZone = r.TryGetProperty("timezone", out var tz) ? tz.GetString() ?? "" : "",
            };
        }
        catch (Exception ex) { Log.Error("find city", ex); return null; }
    }

    public sealed record CityNow(double Temp, int Code, bool IsDay, int UtcOffsetSeconds, DateTime Fetched)
    {
        public string Glyph => WeatherService.Glyph(Code, IsDay);
        public string Description => WeatherService.Describe(Code);
    }

    static readonly Dictionary<string, CityNow> cityCache = new();
    static readonly HashSet<string> cityFetching = new();
    static readonly Dictionary<string, TimeZoneInfo?> zones = new();

    /// <summary>Weather for a city without waiting: whatever is cached, and a refresh in the background when it's old.</summary>
    public static CityNow? CityCached(CityItem city)
    {
        var key = $"{city.Lat:0.###},{city.Lon:0.###},{SettingsStore.Current.Fahrenheit}";
        cityCache.TryGetValue(key, out var hit);
        if ((hit == null || DateTime.Now - hit.Fetched > TimeSpan.FromMinutes(20)) && cityFetching.Add(key))
            _ = ForCity(city).ContinueWith(_ => cityFetching.Remove(key), TaskScheduler.FromCurrentSynchronizationContext());
        return hit;
    }

    /// <summary>The time right now in a city (its own daylight saving included).</summary>
    public static DateTime CityTime(CityItem city)
    {
        if (!zones.TryGetValue(city.TimeZone, out var zone))
        {
            try { zone = city.TimeZone.Length > 0 ? TimeZoneInfo.FindSystemTimeZoneById(city.TimeZone) : null; } catch { zone = null; }
            zones[city.TimeZone] = zone;
        }
        if (zone != null) return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        var w = CityCached(city);
        return w != null ? DateTime.UtcNow.AddSeconds(w.UtcOffsetSeconds) : DateTime.Now;
    }

    /// <summary>Current weather for one city, fetched at most every 20 minutes.</summary>
    public static async Task<CityNow?> ForCity(CityItem city)
    {
        var key = $"{city.Lat:0.###},{city.Lon:0.###},{SettingsStore.Current.Fahrenheit}";
        cityCache.TryGetValue(key, out var hit);
        if (hit != null && DateTime.Now - hit.Fetched < TimeSpan.FromMinutes(20)) return hit;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var url = "https://api.open-meteo.com/v1/forecast?latitude=" + city.Lat.ToString(inv) + "&longitude=" + city.Lon.ToString(inv) +
                      "&current=temperature_2m,weather_code,is_day&timezone=auto" +
                      (SettingsStore.Current.Fahrenheit ? "&temperature_unit=fahrenheit" : "");
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var cur = doc.RootElement.GetProperty("current");
            var now = new CityNow(cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("weather_code").GetInt32(),
                cur.GetProperty("is_day").GetInt32() == 1, doc.RootElement.TryGetProperty("utc_offset_seconds", out var o) ? o.GetInt32() : 0, DateTime.Now);
            cityCache[key] = now;
            return now;
        }
        catch (Exception ex) { Log.Error("city weather", ex); return hit; }
    }

    public static async void Refresh(bool force = false)
    {
        var s = SettingsStore.Current;
        if (s.WeatherLat == null || s.WeatherLon == null) { Current = null; Updated?.Invoke(); return; }
        if (!force && DateTime.Now - lastFetch < TimeSpan.FromMinutes(20) && Current != null) return;
        lastFetch = DateTime.Now;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var url = "https://api.open-meteo.com/v1/forecast?latitude=" + s.WeatherLat.Value.ToString(inv) +
                      "&longitude=" + s.WeatherLon.Value.ToString(inv) +
                      "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min,sunrise,sunset&timezone=auto&forecast_days=1" +
                      (s.Fahrenheit ? "&temperature_unit=fahrenheit" : "");
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            Current = new WeatherNow
            {
                Temp = cur.GetProperty("temperature_2m").GetDouble(),
                Code = cur.GetProperty("weather_code").GetInt32(),
                IsDay = cur.GetProperty("is_day").GetInt32() == 1,
                High = daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                Low = daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                Place = s.WeatherCity,
                Sunrise = daily.TryGetProperty("sunrise", out var sr) && DateTime.TryParse(sr[0].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) ? a : null,
                Sunset = daily.TryGetProperty("sunset", out var ss) && DateTime.TryParse(ss[0].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var b) ? b : null,
            };
            Updated?.Invoke();
        }
        catch (Exception ex) { Log.Error("weather", ex); lastFetch = DateTime.Now.AddMinutes(-17); }
    }

    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        >= 61 and <= 67 => "Rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow showers",
        >= 95 => "Thunderstorm",
        _ => "—",
    };

    public static string Glyph(int code, bool day) => code switch
    {
        0 or 1 => day ? "☀" : "☾",
        2 => day ? "⛅" : "☁",
        3 => "☁",
        45 or 48 => "≈",
        >= 71 and <= 77 or 85 or 86 => "❄",
        >= 95 => "⚡",
        >= 51 => "☂",
        _ => "☁",
    };
}

public static class StringExt
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
