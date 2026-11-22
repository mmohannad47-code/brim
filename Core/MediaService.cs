using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace Brim.Core;

/// <summary>Reads whatever is playing through Windows' media controls (Spotify, browsers, Apple Music, VLC...).</summary>
public sealed class MediaService
{
    public static MediaService Instance { get; } = new();

    public event Action? Changed;
    public event Action? TrackChanged;

    public bool HasSession => session != null || fake;
    bool fake;

    /// <summary>For snapshots: pretend a song is playing (no player needed).</summary>
    internal void DebugFake(string title, string artist, TimeSpan duration, TimeSpan at)
    {
        fake = true;
        Title = title; Artist = artist; Source = "Spotify"; IsPlaying = true; Duration = duration;
        position = at; positionStamp = DateTimeOffset.Now;
        Changed?.Invoke();
    }
    public bool IsPlaying { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Source { get; private set; } = "";
    public ImageSource? Art { get; private set; }
    public Color? ArtColor { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool CanSeek { get; private set; }

    TimeSpan position, rawPosition = TimeSpan.MinValue;
    DateTimeOffset positionStamp, rawStamp;
    GlobalSystemMediaTransportControlsSessionManager? manager;
    GlobalSystemMediaTransportControlsSession? session;
    string lastTrackKey = "";

    /// <summary>For dev checks: what the player last reported (position, and how long ago it said so).</summary>
    public string RawTimeline
    {
        get
        {
            try
            {
                var t = session?.GetTimelineProperties();
                return t == null ? "no timeline" : $"player said {Fmt(t.Position)} {(DateTimeOffset.Now - t.LastUpdatedTime).TotalSeconds:0.0}s ago";
            }
            catch { return "?"; }
        }
    }

    public TimeSpan Position
    {
        get
        {
            var p = position;
            if (IsPlaying && positionStamp != default)
                p += DateTimeOffset.Now - positionStamp;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    public async Task InitAsync()
    {
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            manager.CurrentSessionChanged += (_, _) => OnUi(PickSession);
            manager.SessionsChanged += (_, _) => OnUi(PickSession);
            PickSession();
        }
        catch (Exception ex) { Log.Error("media init", ex); }
    }

    static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    void PickSession()
    {
        if (manager == null) return;
        GlobalSystemMediaTransportControlsSession? pick = null;
        try
        {
            foreach (var s in manager.GetSessions())
            {
                if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                { pick = s; break; }
            }
            pick ??= manager.GetCurrentSession();
        }
        catch (Exception ex) { Log.Error("media pick", ex); }

        if (pick?.SourceAppUserModelId == session?.SourceAppUserModelId && pick != null && session != null)
        {
            // same app; still refresh in case the object changed
            if (!ReferenceEquals(pick, session)) Attach(pick);
            return;
        }
        Attach(pick);
    }

    void Attach(GlobalSystemMediaTransportControlsSession? s)
    {
        if (session != null)
        {
            session.MediaPropertiesChanged -= OnProps;
            session.PlaybackInfoChanged -= OnPlayback;
            session.TimelinePropertiesChanged -= OnTimeline;
        }
        session = s;
        rawPosition = TimeSpan.MinValue; // a new player: take its first report as new
        if (session == null)
        {
            Title = Artist = Source = "";
            Art = null; ArtColor = null; IsPlaying = false; Duration = TimeSpan.Zero; position = TimeSpan.Zero;
            lastTrackKey = "";
            Changed?.Invoke();
            return;
        }
        session.MediaPropertiesChanged += OnProps;
        session.PlaybackInfoChanged += OnPlayback;
        session.TimelinePropertiesChanged += OnTimeline;
        Source = PrettySource(session.SourceAppUserModelId);
        ReadPlayback();
        ReadTimeline();
        QueueProps();
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => OnUi(QueueProps);

    bool propsBusy, propsAgain;

    /// <summary>
    /// Read the song details once, and if they changed again meanwhile, once more a second later. A browser tab whose
    /// title counts up (a progress percentage) reports a change every moment; reading each one made a steady stream
    /// of Windows objects (picture streams) for .NET to clean up.
    /// </summary>
    async void QueueProps()
    {
        if (propsBusy) { propsAgain = true; return; }
        propsBusy = true;
        try
        {
            do
            {
                propsAgain = false;
                await ReadPropsAsync();
                if (propsAgain) await Task.Delay(1000);
            }
            while (propsAgain);
        }
        catch (Exception ex) { Log.Error("media props", ex); }
        finally { propsBusy = false; }
    }
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => OnUi(() => { ReadPlayback(); PickSession(); });
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => OnUi(() => { ReadTimeline(); Changed?.Invoke(); });

    void ReadPlayback()
    {
        try
        {
            var info = session?.GetPlaybackInfo();
            bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing != IsPlaying)
            {
                position = Position; // freeze extrapolation at the switch
                positionStamp = DateTimeOffset.Now;
            }
            IsPlaying = playing;
            CanSeek = info?.Controls?.IsPlaybackPositionEnabled ?? false;
            CanNext = info?.Controls?.IsNextEnabled ?? false;
            CanPrevious = info?.Controls?.IsPreviousEnabled ?? false;
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Error("media playback", ex); }
    }

    void ReadTimeline()
    {
        try
        {
            var t = session?.GetTimelineProperties();
            if (t == null) return;
            Duration = t.EndTime - t.StartTime;
            // Players report where they are only now and then (Spotify: on play, pause and seek), with the moment
            // they said it. Reading the same report again must not restart the count from it (lyrics fell behind).
            if (t.Position == rawPosition && t.LastUpdatedTime == rawStamp) return;
            rawPosition = t.Position;
            rawStamp = t.LastUpdatedTime;
            var now = DateTimeOffset.Now;
            position = t.Position;
            positionStamp = t.LastUpdatedTime == default ? now : t.LastUpdatedTime;
            // Count on from that moment, unless it's in the future or would run past the end of the song (a stamp
            // the app never updates); without a length, no further than 30 s
            var age = now - positionStamp;
            if (age < TimeSpan.Zero || (Duration > TimeSpan.Zero ? t.Position + age > Duration : age > TimeSpan.FromSeconds(30)))
                positionStamp = now;
        }
        catch (Exception ex) { Log.Error("media timeline", ex); }
    }

    async Task ReadPropsAsync()
    {
        var s = session;
        if (s == null) return;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (props == null || !ReferenceEquals(s, session)) return;
            Title = props.Title ?? "";
            Artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumTitle ?? "" : props.Artist;

            var key = Title + "\u0001" + Artist;
            bool newTrack = key != lastTrackKey;
            lastTrackKey = key;

            if (props.Thumbnail != null)
            {
                try
                {
                    using var ras = await props.Thumbnail.OpenReadAsync();
                    using var stream = ras.AsStreamForRead();
                    var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    ms.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 300;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    Art = bmp;
                    ArtColor = DominantColor(bmp);
                }
                catch { Art = null; ArtColor = null; }
            }
            else { Art = null; ArtColor = null; }

            ReadTimeline();
            Changed?.Invoke();
            if (newTrack && !string.IsNullOrEmpty(Title)) TrackChanged?.Invoke();
        }
        catch (Exception ex) { Log.Error("media props", ex); }
    }

    public async void PlayPause() { try { if (session != null) await session.TryTogglePlayPauseAsync(); } catch { } }
    public bool CanNext { get; private set; }
    public bool CanPrevious { get; private set; }

    /// <summary>
    /// Next track, or (when the player has none, like a single YouTube video) jump ahead 10 seconds.
    /// </summary>
    public async void Next()
    {
        try
        {
            if (session == null) return;
            if (CanNext && await session.TrySkipNextAsync()) return;
            if (CanSeek) Seek(Position + TimeSpan.FromSeconds(10));
            else Native.TapKeys(0xB0 /* media next track */);
        }
        catch { }
    }

    /// <summary>Previous track, or go back 10 seconds when the player has no previous track.</summary>
    public async void Previous()
    {
        try
        {
            if (session == null) return;
            if (CanPrevious && await session.TrySkipPreviousAsync()) return;
            if (CanSeek) Seek(Position - TimeSpan.FromSeconds(10) < TimeSpan.Zero ? TimeSpan.Zero : Position - TimeSpan.FromSeconds(10));
            else Native.TapKeys(0xB1 /* media previous track */);
        }
        catch { }
    }

    public async void Seek(TimeSpan to)
    {
        try
        {
            if (session == null) return;
            position = to; positionStamp = DateTimeOffset.Now;
            await session.TryChangePlaybackPositionAsync(to.Ticks);
        }
        catch { }
    }

    static string PrettySource(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        var a = aumid;
        int bang = a.LastIndexOf('!');
        if (bang >= 0) a = a[(bang + 1)..];
        if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) a = a[..^4];
        var l = a.ToLowerInvariant();
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("msedge")) return "Edge";
        if (l.Contains("firefox")) return "Firefox";
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("applemusic") || l.Contains("apple")) return "Apple Music";
        if (l.Contains("zune") || l.Contains("media")) return "Media Player";
        if (l.Contains("vlc")) return "VLC";
        return a.Length > 0 ? char.ToUpper(a[0]) + a[1..] : a;
    }

    static Color? DominantColor(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(src, new ScaleTransform(12.0 / src.PixelWidth, 12.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double B = px[i], G = px[i + 1], R = px[i + 2];
                double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                double sat = max == 0 ? 0 : (max - min) / max;
                double light = max / 255.0;
                double weight = 0.05 + sat * sat * (light > 0.15 ? 1 : 0.2);
                r += R * weight; g += G * weight; b += B * weight; total += weight;
            }
            if (total <= 0) return null;
            var c = Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total));
            // keep it readable behind white text: clamp brightness
            double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            if (lum > 150)
            {
                double k = 150 / lum;
                c = Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
            }
            return c;
        }
        catch { return null; }
    }

    public static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
