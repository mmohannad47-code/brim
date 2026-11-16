using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Brim.Core;

/// <summary>
/// Files, photos, links and text between this PC and a phone (iPhone or Android) on the same Wi-Fi, with nothing to
/// install on the phone: Brim serves a small page on the home network, the phone opens it by scanning a QR code, and
/// the page sends things to the PC and picks up what the PC offers (the notch Shelf, links and text sent to it).
/// Off until asked for, locked with a secret code that's in the QR code, refused on networks marked Public, never
/// answers anything outside the home network, and switches itself off after 15 minutes nobody uses it.
/// </summary>
public static class PhoneLink
{
    public sealed record Post(long Id, string Text, bool IsLink, DateTime At);

    const int FirstPort = 48120;
    static readonly TimeSpan IdleOff = TimeSpan.FromMinutes(15);

    static TcpListener? listener;
    static CancellationTokenSource? cts;
    static System.Windows.Threading.DispatcherTimer? idleTimer;
    static DateTime lastUse;
    static string key = "";
    static volatile (string id, string path)[] offered = Array.Empty<(string, string)>();
    static readonly List<Post> posts = new();
    static long nextPost = 1;
    static readonly object gate = new();
    static readonly Dictionary<string, byte[]> thumbs = new();

    /// <summary>Developer tests: serve on this PC only (no firewall question) and save into this folder.</summary>
    internal static bool TestLocal;
    internal static string? TestSaveTo;

    public static bool On => listener != null;
    /// <summary>The address the phone opens (in the QR code).</summary>
    public static string? Url { get; private set; }
    /// <summary>Why it couldn't start: "network" (no home network), "public" (network marked Public), "port", or null.</summary>
    public static string? Problem { get; private set; }
    /// <summary>When a phone last asked for anything, and what kind of phone it is ("iPhone", "Android", "phone").</summary>
    public static DateTime? LastSeen { get; private set; }
    public static string PhoneKind { get; private set; } = "phone";
    /// <summary>Kept on while the phone window is open, even with no phone asking.</summary>
    public static bool Watched { get; set; }

    /// <summary>Started, stopped, or a phone came or went (UI thread).</summary>
    public static event Action? Changed;
    /// <summary>A phone connected (after being away a while) (UI thread).</summary>
    public static event Action<string>? Connected;
    /// <summary>Files from the phone, saved in Downloads (UI thread).</summary>
    public static event Action<string>? Received;
    /// <summary>Text or a link from the phone: text, is it a link, open it now (UI thread).</summary>
    public static event Action<string, bool, bool>? TextReceived;

    static System.Windows.Threading.Dispatcher Ui => System.Windows.Application.Current.Dispatcher;
    static void OnUi(Action a) => Ui.BeginInvoke(a);

    // ------------------------------------------------------------------ on and off

    public static bool Start()
    {
        if (On) { lastUse = DateTime.Now; return true; }
        Problem = null;
        var (ip, ifIndex) = TestLocal ? ("127.0.0.1", -1) : HomeAddress();
        if (ip == null) { Problem = "network"; Log.Note("phone", "no home network to use"); Changed?.Invoke(); return false; }
        if (!TestLocal && IsPublic(ifIndex)) { Problem = "public"; Log.Note("phone", $"not starting: the network on {ip} is marked Public"); Changed?.Invoke(); return false; }

        var s = SettingsStore.Current;
        if (string.IsNullOrEmpty(s.PhoneKey))
        {
            s.PhoneKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            SettingsStore.Save();
        }
        key = s.PhoneKey;

        // The same port every time, so a page saved on the phone's home screen keeps working
        foreach (var port in new[] { s.PhonePort }.Where(p => p > 0).Concat(Enumerable.Range(FirstPort, 40)))
        {
            try
            {
                var l = new TcpListener(TestLocal ? IPAddress.Loopback : IPAddress.Any, port);
                l.Start();
                listener = l;
                if (s.PhonePort != port) { s.PhonePort = port; SettingsStore.Save(); }
                Url = $"http://{ip}:{port}/?k={key}";
                break;
            }
            catch (SocketException) { }
        }
        if (listener == null) { Problem = "port"; Log.Note("phone", "no free port"); Changed?.Invoke(); return false; }

        cts = new CancellationTokenSource();
        var token = cts.Token;
        var l2 = listener;
        Task.Run(() => AcceptLoop(l2, token));
        lastUse = DateTime.Now;
        idleTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        idleTimer.Tick -= IdleTick;
        idleTimer.Tick += IdleTick;
        idleTimer.Start();
        Shelf.Items.CollectionChanged -= ShelfChanged;
        Shelf.Items.CollectionChanged += ShelfChanged;
        ShelfChanged(null, null);
        Log.Note("phone", $"on at {ip}:{s.PhonePort}");
        Changed?.Invoke();
        return true;
    }

    public static void Stop()
    {
        if (!On) return;
        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        listener = null;
        Url = null;
        LastSeen = null;
        idleTimer?.Stop();
        Shelf.Items.CollectionChanged -= ShelfChanged;
        lock (gate) thumbs.Clear();
        Log.Note("phone", "off");
        Changed?.Invoke();
    }

    static void IdleTick(object? sender, EventArgs e)
    {
        if (Watched) { lastUse = DateTime.Now; return; }
        if (DateTime.Now - lastUse > IdleOff) Stop();
    }

    /// <summary>A new address for the page: the old QR code and any saved home-screen page stop working.</summary>
    public static void NewCode()
    {
        SettingsStore.Current.PhoneKey = null;
        SettingsStore.Save();
        bool was = On;
        Stop();
        if (was) Start();
    }

    static void ShelfChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e)
    {
        offered = Shelf.Items.Select(i => (IdOf(i.Path), i.Path)).ToArray();
        lastUse = DateTime.Now;
    }

    static string IdOf(string path) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..12].ToLowerInvariant();

    /// <summary>Put a link or some text on the phone's page.</summary>
    public static void SendText(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        lock (gate)
        {
            posts.Add(new Post(nextPost++, text, IsLink(text), DateTime.Now));
            if (posts.Count > 30) posts.RemoveAt(0);
        }
        lastUse = DateTime.Now;
        Log.Note("phone", $"sent {(IsLink(text) ? "a link" : "text")} to the phone");
    }

    public static bool IsLink(string text) =>
        !text.Contains(' ') && !text.Contains('\n') && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

    // ------------------------------------------------------------------ the home network

    /// <summary>This PC's address on the network a phone would be on (Wi-Fi or cable with a router), skipping VPNs and virtual adapters.</summary>
    static (string? ip, int ifIndex) HomeAddress()
    {
        try
        {
            var best = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet &&
                            !System.Text.RegularExpressions.Regex.IsMatch(n.Description + " " + n.Name, "(?i)virtual|hyper-v|vmware|virtualbox|vpn|tap-|tunnel|wsl|loopback|bluetooth|zerotier|tailscale|hamachi"))
                .Select(n =>
                {
                    var p = n.GetIPProperties();
                    var addr = p.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))?.Address;
                    bool gateway = p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    int index = -1;
                    try { index = p.GetIPv4Properties()?.Index ?? -1; } catch { }
                    return (n, addr, gateway, index);
                })
                .Where(x => x.addr != null && IsPrivate(x.addr))
                .OrderByDescending(x => x.gateway)
                .ThenByDescending(x => x.n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .FirstOrDefault();
            return best.addr != null ? (best.addr.ToString(), best.index) : (null, -1);
        }
        catch (Exception ex) { Log.Error("phone address", ex); return (null, -1); }
    }

    /// <summary>Is the network on this adapter marked Public in Windows (where the firewall would block the phone anyway)?</summary>
    static bool IsPublic(int ifIndex)
    {
        try
        {
            using var q = new System.Management.ManagementObjectSearcher(@"root\StandardCimv2", "SELECT InterfaceIndex, NetworkCategory FROM MSFT_NetConnectionProfile");
            foreach (System.Management.ManagementObject o in q.Get())
                if (Convert.ToInt32(o["InterfaceIndex"]) == ifIndex) return Convert.ToInt32(o["NetworkCategory"]) == 0;
        }
        catch (Exception ex) { Log.Error("phone network kind", ex); }
        return false;
    }

    static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    // ------------------------------------------------------------------ serving the page

    static async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => Handle(c, ct));
        }
    }

    sealed class Request
    {
        public string Method = "", Path = "";
        public Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Extra = Array.Empty<byte>(); // body bytes that came in with the headers
        public long Length;
    }

    static async Task Handle(TcpClient c, CancellationToken ct)
    {
        using var _ = c;
        try
        {
            if (c.Client.RemoteEndPoint is not IPEndPoint ep || !IsPrivate(ep.Address)) return; // only the home network
            c.NoDelay = true;
            var s = c.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var req = await ReadHead(s, timeout.Token);
            if (req == null) return;

            if (!Authorised(req))
            {
                await Send(s, 403, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PhonePage.Locked));
                return;
            }
            Seen(req);
            switch (req.Method, req.Path)
            {
                case ("GET", "/"):
                    await Send(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PhonePage.Html(Environment.MachineName)));
                    break;
                case ("GET", "/api/state"):
                    await Send(s, 200, "application/json", State());
                    break;
                case ("GET", "/api/clip"):
                    string clip = "";
                    await Ui.InvokeAsync(() => { try { if (System.Windows.Clipboard.ContainsText()) clip = System.Windows.Clipboard.GetText(); } catch { } });
                    await Send(s, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { text = clip, link = IsLink(clip) }));
                    break;
                case ("POST", "/api/text"):
                    await ReceiveText(s, req);
                    break;
                case ("POST", "/api/up"):
                    await ReceiveFile(s, req, ct);
                    break;
                default:
                    if (req.Method is "GET" or "HEAD" && req.Path.StartsWith("/f/")) await SendFile(s, req, req.Path[3..], ct);
                    else if (req.Method == "GET" && req.Path.StartsWith("/t/")) await SendThumb(s, req.Path[3..]);
                    else await Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("Not here"));
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception ex) { Log.Error("phone request", ex); }
    }

    static bool Authorised(Request r)
    {
        string given = r.Query.TryGetValue("k", out var q) ? q : r.Headers.TryGetValue("x-brim-key", out var h) ? h : "";
        return key.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key));
    }

    static void Seen(Request r)
    {
        var ua = r.Headers.TryGetValue("user-agent", out var u) ? u : "";
        var kind = ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Android") ? "Android phone" : "phone";
        bool fresh = LastSeen == null || DateTime.Now - LastSeen > TimeSpan.FromMinutes(2);
        LastSeen = DateTime.Now;
        lastUse = DateTime.Now;
        PhoneKind = kind;
        if (fresh)
        {
            Log.Note("phone", $"{kind} connected");
            OnUi(() => { Connected?.Invoke(kind); Changed?.Invoke(); });
        }
    }

    static async Task<Request?> ReadHead(Stream s, CancellationToken ct)
    {
        var buf = new byte[16384];
        int n = 0, end = -1;
        while (end < 0)
        {
            if (n == buf.Length) return null; // headers too big
            int got = await s.ReadAsync(buf.AsMemory(n), ct);
            if (got == 0) return null;
            n += got;
            for (int i = Math.Max(0, n - got - 3); i + 3 < n; i++)
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n') { end = i; break; }
        }
        var lines = Encoding.UTF8.GetString(buf, 0, end).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var r = new Request { Method = first[0].ToUpperInvariant() };
        var target = first[1];
        int qm = target.IndexOf('?');
        r.Path = Uri.UnescapeDataString(qm >= 0 ? target[..qm] : target);
        if (qm >= 0)
            foreach (var pair in target[(qm + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                var k = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
                r.Query[k] = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
            }
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) r.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        r.Extra = buf[(end + 4)..n];
        r.Length = r.Headers.TryGetValue("content-length", out var cl) && long.TryParse(cl, out var len) ? len : 0;
        return r;
    }

    static async Task Send(Stream s, int code, string type, byte[] body, string extra = "")
    {
        var head = $"HTTP/1.1 {code} {(code switch { 200 => "OK", 206 => "Partial Content", 403 => "Forbidden", 404 => "Not Found", 413 => "Too Large", 416 => "Range Not Satisfiable", _ => "Error" })}\r\n" +
                   $"Content-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n" +
                   "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n" + extra + "\r\n";
        await s.WriteAsync(Encoding.UTF8.GetBytes(head));
        await s.WriteAsync(body);
    }

    /// <summary>The body, with whatever arrived along with the headers first.</summary>
    static async Task CopyBody(Stream s, Request r, Stream to, CancellationToken ct)
    {
        long left = r.Length;
        if (r.Extra.Length > 0)
        {
            int take = (int)Math.Min(left, r.Extra.Length);
            await to.WriteAsync(r.Extra.AsMemory(0, take), ct);
            left -= take;
        }
        var buf = new byte[81920];
        while (left > 0)
        {
            int got = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct);
            if (got == 0) throw new IOException("the phone stopped sending");
            await to.WriteAsync(buf.AsMemory(0, got), ct);
            left -= got;
        }
    }

    static byte[] State()
    {
        var items = offered.Select(o =>
        {
            bool dir = Directory.Exists(o.path);
            long size = 0;
            try { if (!dir) size = new FileInfo(o.path).Length; } catch { }
            var ext = Path.GetExtension(o.path).ToLowerInvariant();
            return new
            {
                id = o.id,
                name = Path.GetFileName(o.path.TrimEnd('\\')) + (dir ? ".zip" : ""),
                size,
                folder = dir,
                image = !dir && ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp",
                video = !dir && ext is ".mp4" or ".mov" or ".m4v" or ".webm",
                there = dir || File.Exists(o.path),
            };
        }).Where(i => i.there).ToList();
        List<Post> p;
        lock (gate) p = posts.ToList();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            pc = Environment.MachineName,
            items,
            posts = p.OrderByDescending(x => x.Id).Select(x => new { id = x.Id, text = x.Text, link = x.IsLink, at = x.At.ToString("HH:mm") }),
        });
    }

    static async Task SendFile(Stream s, Request r, string id, CancellationToken ct)
    {
        var path = offered.FirstOrDefault(o => o.id == id).path;
        if (path == null) { await Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("That file isn't on the shelf any more")); return; }
        bool inline = r.Query.ContainsKey("view");

        if (Directory.Exists(path))
        {
            // A folder comes down as a zip, made on the way out
            var name = Path.GetFileName(path.TrimEnd('\\')) + ".zip";
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Disposition: attachment; {FileNameHeader(name)}\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.UTF8.GetBytes(head), ct);
            using var zip = new System.IO.Compression.ZipArchive(s, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true);
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var entry = zip.CreateEntry(Path.GetRelativePath(path, f).Replace('\\', '/'), System.IO.Compression.CompressionLevel.Fastest);
                    using var es = entry.Open();
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    await fs.CopyToAsync(es, ct);
                }
                catch (IOException) when (!ct.IsCancellationRequested) { } // a locked file: skip it
            }
            return;
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
        long total = file.Length, from = 0, to = total - 1;
        int code = 200;
        // Ranges: the phone's video player asks for pieces
        if (r.Headers.TryGetValue("range", out var range) && range.StartsWith("bytes=") && !range.Contains(','))
        {
            var parts = range[6..].Split('-');
            if (parts[0].Length > 0 && long.TryParse(parts[0], out var a)) { from = a; if (parts.Length > 1 && long.TryParse(parts[1], out var b)) to = Math.Min(b, total - 1); }
            else if (parts.Length > 1 && long.TryParse(parts[1], out var suffix)) from = Math.Max(0, total - suffix);
            if (from > to || from >= total)
            {
                await Send(s, 416, "text/plain", Array.Empty<byte>(), $"Content-Range: bytes */{total}\r\n");
                return;
            }
            code = 206;
        }
        long length = to - from + 1;
        var fileName = Path.GetFileName(path);
        var headers = $"HTTP/1.1 {(code == 206 ? "206 Partial Content" : "200 OK")}\r\nContent-Type: {MimeOf(path)}\r\nContent-Length: {length}\r\n" +
                      $"Accept-Ranges: bytes\r\nContent-Disposition: {(inline ? "inline" : "attachment")}; {FileNameHeader(fileName)}\r\n" +
                      (code == 206 ? $"Content-Range: bytes {from}-{to}/{total}\r\n" : "") + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.UTF8.GetBytes(headers), ct);
        if (r.Method == "HEAD") return;
        file.Position = from;
        var buf = new byte[81920];
        long left = length;
        while (left > 0)
        {
            int got = await file.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct);
            if (got == 0) break;
            await s.WriteAsync(buf.AsMemory(0, got), ct);
            left -= got;
            lastUse = DateTime.Now;
        }
        if (code == 200) Log.Note("phone", $"phone took {fileName}");
    }

    static string FileNameHeader(string name)
    {
        var ascii = new string(name.Select(ch => ch < 128 && ch != '"' && ch != '\\' ? ch : '_').ToArray());
        return $"filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    /// <summary>A small picture of a photo on the shelf for the phone's list (made once, kept while it's on).</summary>
    static async Task SendThumb(Stream s, string id)
    {
        var path = offered.FirstOrDefault(o => o.id == id).path;
        if (path == null) { await Send(s, 404, "text/plain", Array.Empty<byte>()); return; }
        byte[]? jpg;
        lock (gate) thumbs.TryGetValue(id, out jpg);
        if (jpg == null)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 200;
                bmp.StreamSource = fs;
                bmp.EndInit();
                bmp.Freeze();
                var enc = new JpegBitmapEncoder { QualityLevel = 80 };
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                enc.Save(ms);
                jpg = ms.ToArray();
                lock (gate) { if (thumbs.Count > 60) thumbs.Clear(); thumbs[id] = jpg; }
            }
            catch { await Send(s, 404, "text/plain", Array.Empty<byte>()); return; }
        }
        await Send(s, 200, "image/jpeg", jpg);
    }

    static async Task ReceiveText(Stream s, Request r)
    {
        if (r.Length > 256 * 1024) { await Send(s, 413, "text/plain", Array.Empty<byte>()); return; }
        using var ms = new MemoryStream();
        await CopyBody(s, r, ms, CancellationToken.None);
        var text = Encoding.UTF8.GetString(ms.ToArray()).Trim();
        bool open = r.Query.ContainsKey("open");
        if (text.Length > 0)
        {
            bool link = IsLink(text);
            Log.Note("phone", $"{(link ? "a link" : "text")} from the phone{(open ? ", to open" : "")}");
            OnUi(() => TextReceived?.Invoke(text, link, open && link));
        }
        await Send(s, 200, "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
    }

    static async Task ReceiveFile(Stream s, Request r, CancellationToken ct)
    {
        var name = CleanName(r.Query.TryGetValue("name", out var n) ? n : "");
        var folder = TestSaveTo ?? Downloads.Folder;
        Directory.CreateDirectory(folder);
        var dest = Unique(Path.Combine(folder, name));
        var part = dest + ".brimpart";
        try
        {
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                await CopyBody(s, r, fs, ct);
            dest = Unique(dest); // in case another one with this name finished meanwhile
            File.Move(part, dest);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
        Log.Note("phone", $"got {Path.GetFileName(dest)} ({r.Length / 1048576.0:0.0} MB) from the phone");
        OnUi(() => Received?.Invoke(dest));
        await Send(s, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, name = Path.GetFileName(dest) }));
    }

    static string CleanName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/').Split('/').Last());
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        name = name.Trim().TrimStart('.');
        if (name.Length == 0) name = $"From phone {DateTime.Now:yyyy-MM-dd HHmmss}";
        if (name.Length > 120) name = name[..(120 - Path.GetExtension(name).Length)] + Path.GetExtension(name);
        // Never something Windows would run on a double-click
        if (Path.GetExtension(name).ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".com" or ".scr" or ".ps1" or ".vbs" or ".js" or ".msi" or ".lnk" or ".url" or ".hta" or ".pif" or ".jse" or ".wsf")
            name += ".download";
        return name;
    }

    static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".bmp" => "image/bmp",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };
}
