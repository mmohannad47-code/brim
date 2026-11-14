using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Brim.Core;

public sealed record LyricLine(TimeSpan At, string Text);

/// <summary>
/// Time-synced lyrics for whatever is playing, from LRCLIB (free, no key). Each song is looked up once and kept in
/// %LOCALAPPDATA%\Brim\Lyrics (songs without synced lyrics are remembered too, and asked again after a few days).
/// </summary>
public static class Lyrics
{
    /// <summary>The playing song's lines (null: none, still loading, or lyrics are off).</summary>
    public static IReadOnlyList<LyricLine>? Lines { get; private set; }
    /// <summary>Fires on the UI thread when <see cref="Lines"/> changes.</summary>
    public static event Action? Changed;

    static readonly HttpClient http = MakeClient();
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim", "Lyrics");
    static string trackKey = "";
    static bool started;

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // LRCLIB asks apps to say who they are
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Brim/{Installer.Version} (desktop notch app)");
        return c;
    }

    public static void Start()
    {
        if (started) return;
        started = true;
        MediaService.Instance.Changed += Check;
        Check();
    }

    /// <summary>Look up the playing song if it changed (or lyrics were switched on or off).</summary>
    public static void Check()
    {
        var m = MediaService.Instance;
        var key = SettingsStore.Current.Lyrics && m.HasSession && m.Title.Length > 0 ? m.Title + "\u0001" + m.Artist : "";
        if (key == trackKey) return;
        trackKey = key;
        if (Lines != null) { Lines = null; Changed?.Invoke(); }
        if (key.Length == 0) return;
        var (title, artist) = (m.Title, m.Artist);
        _ = LoadAsync(key, title, artist);
    }

    static async Task LoadAsync(string key, string title, string artist)
    {
        List<LyricLine>? lines = null;
        try
        {
            // The song's length helps pick the right version; it can arrive a moment after the title
            var duration = MediaService.Instance.Duration;
            for (int i = 0; i < 10 && duration <= TimeSpan.Zero && key == trackKey; i++)
            {
                await Task.Delay(150);
                duration = MediaService.Instance.Duration;
            }
            lines = await FindAsync(title, artist, duration);
        }
        catch (Exception ex) { Log.Error("lyrics", ex); }
        if (key != trackKey) return; // the song changed meanwhile
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (key != trackKey) return;
            Lines = lines is { Count: > 0 } ? lines : null;
            Changed?.Invoke();
        });
    }

    /// <summary>For snapshots: these lines belong to what's "playing" now.</summary>
    internal static void DebugSet(List<LyricLine>? lines)
    {
        var m = MediaService.Instance;
        trackKey = m.Title + "\u0001" + m.Artist;
        Lines = lines;
        Changed?.Invoke();
    }

    /// <summary>Synced lines for a song: from the cache, else LRCLIB (null when it has none).</summary>
    public static async Task<List<LyricLine>?> FindAsync(string title, string artist, TimeSpan duration)
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, Hash(Normal(artist) + "|" + Normal(title)));
        if (File.Exists(file + ".lrc")) return Parse(await File.ReadAllTextAsync(file + ".lrc"));
        if (File.Exists(file + ".none") && DateTime.Now - File.GetLastWriteTime(file + ".none") < TimeSpan.FromDays(3)) return null;

        string? lrc = null;
        bool reached = false;
        foreach (var (t, a) in Guesses(title, artist))
        {
            var (found, ok) = await AskAsync(t, a, duration);
            reached |= ok;
            if (found != null) { lrc = found; break; }
        }
        if (lrc != null)
        {
            await File.WriteAllTextAsync(file + ".lrc", lrc);
            Log.Note("lyrics", $"found '{title}' by {artist}");
            return Parse(lrc);
        }
        // Only remember "none" when LRCLIB actually answered (not when offline)
        if (reached) { await File.WriteAllTextAsync(file + ".none", ""); Log.Note("lyrics", $"none for '{title}' by {artist}"); }
        return null;
    }

    /// <summary>The ways to ask: as the player names it, then tidied up (YouTube titles, "feat.", remasters…).</summary>
    static IEnumerable<(string title, string artist)> Guesses(string title, string artist)
    {
        var seen = new HashSet<string>();
        var a = CleanArtist(artist);
        var t = CleanTitle(title);
        foreach (var g in new[] { (title, artist), (t, a), (t, FirstArtist(a)) })
            if (g.Item1.Length > 0 && seen.Add(g.Item1 + "|" + g.Item2)) yield return g;
        // Videos: "Artist - Song (Official Video)" posted by a channel
        var dash = Regex.Match(t, @"^(?<a>[^-–—]+?)\s+[-–—]\s+(?<t>.+)$");
        if (dash.Success)
        {
            var g = (CleanTitle(dash.Groups["t"].Value), dash.Groups["a"].Value.Trim());
            if (seen.Add(g.Item1 + "|" + g.Item2)) yield return g;
        }
    }

    sealed class Hit
    {
        public string? trackName { get; set; }
        public string? artistName { get; set; }
        public double? duration { get; set; }
        public bool instrumental { get; set; }
        public string? syncedLyrics { get; set; }
    }

    /// <summary>One question to LRCLIB: the closest match with synced lyrics, and whether it answered at all.</summary>
    static async Task<(string? lrc, bool reached)> AskAsync(string title, string artist, TimeSpan duration)
    {
        var url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title) +
                  (artist.Length > 0 ? "&artist_name=" + Uri.EscapeDataString(artist) : "");
        List<Hit>? hits;
        try
        {
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return (null, (int)resp.StatusCode < 500);
            hits = JsonSerializer.Deserialize<List<Hit>>(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, false); }
        var best = hits?.Where(h => !h.instrumental && !string.IsNullOrWhiteSpace(h.syncedLyrics))
            // The same length (versions differ: radio edit, live…), then the closest name
            .Where(h => duration <= TimeSpan.Zero || h.duration == null || Math.Abs(h.duration.Value - duration.TotalSeconds) <= 4)
            .OrderBy(h => duration > TimeSpan.Zero && h.duration != null ? Math.Abs(h.duration.Value - duration.TotalSeconds) : 0)
            .ThenBy(h => Normal(h.trackName ?? "") == Normal(title) ? 0 : 1)
            .FirstOrDefault();
        return (best?.syncedLyrics, true);
    }

    /// <summary>"[01:23.45]text" lines (a line may carry several times), in order.</summary>
    public static List<LyricLine> Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var stamps = Regex.Matches(raw, @"\[(\d+):(\d+(?:[.:]\d+)?)\]");
            if (stamps.Count == 0) continue;
            var text = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match s in stamps)
            {
                var sec = double.Parse(s.Groups[2].Value.Replace(':', '.'), System.Globalization.CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromMinutes(int.Parse(s.Groups[1].Value)) + TimeSpan.FromSeconds(sec), text));
            }
        }
        return lines.OrderBy(l => l.At).ToList();
    }

    /// <summary>The line being sung at this moment (-1 before the first).</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        // A touch early, so the line shows as it's sung rather than just after
        var at = position + TimeSpan.FromMilliseconds(200);
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].At <= at) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>The line to show now: the sung line, "♪" in instrumental breaks, null before the singing starts.</summary>
    public static string? LineAt(TimeSpan position, int offset = 0)
    {
        var lines = Lines;
        if (lines == null) return null;
        int i = IndexAt(lines, position);
        if (i < 0 && offset == 0) return null;
        i += offset;
        if (i < 0 || i >= lines.Count) return null;
        return lines[i].Text.Length > 0 ? lines[i].Text : "♪";
    }

    static string CleanTitle(string t)
    {
        // (Official Video), [Lyrics], - Remastered 2011, (feat. X), | extras
        t = Regex.Replace(t, @"\s*[\(\[][^\)\]]*(official|video|audio|lyric|visuali[sz]er|remaster|hd|4k|mv|m/v|live|explicit|clean|feat\.?|ft\.?|with )[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+[-–—]\s+(\d{4}\s+)?(remaster(ed)?|radio edit|single version|mono|stereo)\b.*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+(feat\.?|ft\.?)\s+.*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\|.*$", "");
        return t.Trim();
    }

    static string CleanArtist(string a) =>
        Regex.Replace(a, @"(\s+-\s+Topic|VEVO|\s+Official)$", "", RegexOptions.IgnoreCase).Trim();

    static string FirstArtist(string a) =>
        Regex.Split(a, @"\s*(,|&|\bfeat\.?|\bft\.?|\bx\b|;)\s*", RegexOptions.IgnoreCase)[0].Trim();

    static string Normal(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    static string Hash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..20].ToLowerInvariant();

    /// <summary>Dev check: what's playing, the lyrics found for it and the line at each moment for a few seconds.</summary>
    public static async Task<string> SelfTestAsync()
    {
        var sb = new StringBuilder();
        var parsed = Parse("[00:01.00]one\n[00:03.50][00:10.00]two\n[00:05.00]\n[01:02.25]three");
        sb.AppendLine($"{(parsed.Count == 5 && parsed[2].Text == "" && parsed[^1].At == TimeSpan.FromSeconds(62.25) ? "ok  " : "FAIL")} parse ({parsed.Count} lines)");
        sb.AppendLine($"{(IndexAt(parsed, TimeSpan.FromSeconds(0.5)) == -1 && IndexAt(parsed, TimeSpan.FromSeconds(3.4)) == 1 && IndexAt(parsed, TimeSpan.FromSeconds(70)) == 4 ? "ok  " : "FAIL")} line at a moment");
        foreach (var (t, a) in new[] { ("Bohemian Rhapsody - Remastered 2011", "Queen"), ("Rick Astley - Never Gonna Give You Up (Official Music Video)", "Rick Astley") })
        {
            var lines = await FindAsync(t, a, TimeSpan.Zero);
            sb.AppendLine($"{(lines is { Count: > 10 } ? "ok  " : "FAIL")} '{t}' by {a}: {lines?.Count ?? 0} lines, e.g. \"{lines?.Skip(4).FirstOrDefault()?.Text}\"");
        }
        var m = MediaService.Instance;
        await m.InitAsync();
        await Task.Delay(1500);
        sb.AppendLine($"\nplaying: '{m.Title}' by {m.Artist} ({m.Source}), {(m.IsPlaying ? "playing" : "paused")}, {MediaService.Fmt(m.Position)} / {MediaService.Fmt(m.Duration)}");
        if (m.Title.Length > 0)
        {
            var lines = await FindAsync(m.Title, m.Artist, m.Duration);
            sb.AppendLine($"lyrics: {lines?.Count ?? 0} lines");
            if (lines != null)
                for (int i = 0; i < 8; i++)
                {
                    int k = IndexAt(lines, m.Position);
                    sb.AppendLine($"  {MediaService.Fmt(m.Position)} [{m.RawTimeline}] → {(k >= 0 ? lines[k].Text : "(before the first line)")}");
                    await Task.Delay(1000);
                }
        }
        return sb.ToString();
    }
}
