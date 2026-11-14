using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Brim.Core;

public sealed class MessageItem
{
    public uint Id { get; init; }
    public string App { get; init; } = "";
    public string? AppId { get; init; }
    /// <summary>The notification's first line: who wrote (or the channel / subject, depending on the app)</summary>
    public string Sender { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTime Time { get; set; }
    public ImageSource? Icon { get; set; }
    public string Ago => MessageCenter.Ago(Time);
    /// <summary>For messages seen from the app's window (it flashed for attention, or its title shows unread):
    /// the window to bring up. Zero for notifications.</summary>
    public IntPtr Window { get; init; }
    public uint Pid { get; init; }
    /// <summary>For messages seen from a tray badge: the icon (clicking it opens the app, as on the taskbar)</summary>
    public TrayIconItem? Tray { get; init; }
    public bool FromWindow => Window != IntPtr.Zero || Tray != null;
}

/// <summary>
/// Catches messages from apps that don't send Windows a notification (Discord while its window is open, apps set to
/// "no pop-ups"…): chat apps flash for attention when a message arrives, and many put an unread count in their window
/// title ("(3) WhatsApp"). Windows reports both to a "shell hook" window, with or without Windows' taskbar showing.
/// </summary>
sealed class AttentionWatcher : IDisposable
{
    readonly System.Windows.Interop.HwndSource src;
    readonly uint shellMsg;
    readonly Action<IntPtr, bool> seen; // window, true = flashed / false = title changed

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr h);

    public AttentionWatcher(Action<IntPtr, bool> seen)
    {
        this.seen = seen;
        // A hidden top-level window (shell hooks don't reach message-only windows)
        var p = new System.Windows.Interop.HwndSourceParameters("BrimAttention")
        {
            Width = 0, Height = 0, PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = (int)Native.WS_EX_TOOLWINDOW,
        };
        src = new System.Windows.Interop.HwndSource(p);
        shellMsg = Native.RegisterWindowMessage("SHELLHOOK");
        src.AddHook(WndProc);
        RegisterShellHookWindow(src.Handle);
    }

    public static int Signals;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if ((uint)msg == shellMsg)
        {
            Signals++;
            long code = w.ToInt64() & 0xFFFF;
            if (code == 0x8006) seen(l, true);        // HSHELL_FLASH: a window wants attention
            else if (code == 0x0006) seen(l, false);  // HSHELL_REDRAW: a window's title changed
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        DeregisterShellHookWindow(src.Handle);
        src.Dispose();
    }
}

/// <summary>
/// Reads the notifications waiting in Windows' notification centre (Teams, Discord, Outlook…) for the notch's
/// Messages tab, and filters them per app: everything, only some people, or nothing.
/// Windows only tells packaged apps when notifications change, so this looks every few seconds; it's one quick call.
/// </summary>
public static class MessageCenter
{
    static AppSettings S => SettingsStore.Current;
    static readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(4) };
    static UserNotificationListener? listener;
    static bool started, busy, primed;
    static readonly Dictionary<string, ImageSource?> icons = new();
    static readonly HashSet<uint> seen = new();
    static List<MessageItem> all = new();

    public static int AllCount => all.Count;

    [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    static string? lastHeld = "";

    /// <summary>
    /// Why Windows is telling apps not to show notifications right now (Discord and Chrome ask before showing one),
    /// or null when it isn't. A fullscreen video or game left open behind other windows is the usual reason.
    /// </summary>
    public static string? HeldBecause()
    {
        string? why = null;
        try
        {
            if (SHQueryUserNotificationState(out int s) == 0)
                why = s switch
                {
                    1 => "the screen is locked or the screensaver is on",
                    2 => "a fullscreen app or video is open (even one behind other windows)",
                    3 => "a game is running fullscreen",
                    4 => "presentation mode is on",
                    6 => "Do not disturb or quiet hours are on",
                    7 => "a fullscreen Store app is open",
                    _ => null,
                };
        }
        catch { }
        if (why != lastHeld) { lastHeld = why; Log.Note("messages", why == null ? "Windows lets apps notify again" : "Windows is holding app notifications: " + why); }
        return why;
    }

    /// <summary>For `--messages-test`: what Brim can open for each waiting notification, and the badges it sees.</summary>
    public static IEnumerable<string> Diagnose()
    {
        foreach (var m in all)
        {
            var l = NotificationStore.LaunchFor(m.Id);
            yield return $"  {m.App} #{m.Id}: " + (l == null ? "not in Windows' records" : $"opens via {l.Activation}, {(l.Args.Length > 0 ? "has a link to the message" : "no link")}");
        }
        foreach (var (id, n) in badges ?? new()) yield return $"  badge {NameForAppId(id) ?? id}: {n}";
        foreach (var a in attention) yield return $"  from window/tray/badge: {a.App} · {a.Text}";
    }

    /// <summary>Messages that pass your filters, newest first.</summary>
    public static List<MessageItem> Visible { get; private set; } = new();

    /// <summary>Raised on the UI thread after the list changes.</summary>
    public static event Action? Changed;
    /// <summary>A new message you'd see in the tab arrived (not raised for the ones already there when Brim started).</summary>
    public static event Action<MessageItem>? Arrived;

    /// <summary>Why Brim can't read notifications, or null when it can.</summary>
    public static string? Status { get; private set; }

    static readonly Regex Chatty = new(@"(?i)teams|discord|outlook|mail|whatsapp|telegram|slack|signal|messenger|instagram|phone link|skype|zoom|thunderbird|gmail|element|viber|\bline\b|wechat|kakao|guilded|revolt|beeper|facebook");

    static bool Wanted => S.NotchTabs.Contains("Messages") || S.HomeItems.Contains("Message") || S.MessagePopups;

    static AttentionWatcher? watcher;
    static readonly List<MessageItem> attention = new();
    static readonly Dictionary<IntPtr, int> unread = new();
    static uint nextAttentionId = 0x80000000;

    /// <summary>Start or stop looking, depending on whether anything shows messages.</summary>
    public static void Sync()
    {
        if (!Wanted)
        {
            poll.Stop();
            watcher?.Dispose();
            watcher = null;
            return;
        }
        if (!started)
        {
            started = true;
            poll.Tick += async (_, _) => { ClearSeenAttention(); CheckBadges(); HeldBecause(); await Refresh(); };
        }
        if (watcher == null)
        {
            watcher = new AttentionWatcher(OnWindowSignal);
            // Tray icons: only a red unread badge appearing counts (Discord changes its icon for all sorts of reasons,
            // several times a second in a call, and reacting to every change made the notch pop up for nothing)
            TrayService.IconChanged += OnTrayIcon;
            WatchDatabase();
        }
        if (!poll.IsEnabled) { poll.Start(); _ = Refresh(); }
    }

    static FileSystemWatcher? dbWatcher;
    static readonly DispatcherTimer soon = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>
    /// React the moment Windows records a notification (it writes to its notification database), instead of waiting
    /// for the next check a few seconds later.
    /// </summary>
    static void WatchDatabase()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");
            if (!Directory.Exists(dir)) return;
            soon.Tick += async (_, _) => { soon.Stop(); CheckBadges(); await Refresh(); };
            dbWatcher = new FileSystemWatcher(dir, "wpndatabase.db*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
            dbWatcher.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => { soon.Stop(); soon.Start(); }));
            dbWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Error("messages watch", ex); }
    }

    // ---------- tray badges (Discord's red dot and the like). Brim sees every tray icon while its dock runs.

    static readonly Dictionary<string, int> trayBaseline = new();
    static readonly Dictionary<string, bool> trayBadged = new();

    /// <summary>
    /// How much "unread badge red" sits in the icon's right-hand corners (where Discord, Teams, Slack and Telegram
    /// put their red dot or count). Other changes (Discord's speaking indicator in a call, a status colour) don't
    /// add strong red in a corner, so they don't count.
    /// </summary>
    /// <summary>`Brim.exe --badge-test`: the badge check on drawn icons (Discord's colours), no apps needed.</summary>
    public static string BadgeSelfTest()
    {
        System.Windows.Media.Imaging.BitmapSource Icon(Action<DrawingContext> draw)
        {
            var v = new DrawingVisual();
            using (var dc = v.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2)), null, new System.Windows.Rect(0, 0, 32, 32), 8, 8);
                dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(16, 16), 8, 6);
                draw(dc);
            }
            var r = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            r.Render(v);
            return r;
        }
        var red = new SolidColorBrush(Color.FromRgb(0xED, 0x42, 0x45));
        var normal = Icon(_ => { });
        var cases = new (string name, System.Windows.Media.Imaging.BitmapSource img, bool want)[]
        {
            ("red dot, bottom right (Discord unread)", Icon(dc => dc.DrawEllipse(red, null, new System.Windows.Point(25, 25), 6, 6)), true),
            ("red dot, top right (Teams / Slack style)", Icon(dc => dc.DrawEllipse(red, null, new System.Windows.Point(25, 7), 6, 6)), true),
            ("green speaking ring (in a call)", Icon(dc => dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x23, 0xA5, 0x5A)), 3), new System.Windows.Point(16, 16), 14, 14)), false),
            ("grey status dot", Icon(dc => dc.DrawEllipse(Brushes.Gray, null, new System.Windows.Point(25, 25), 6, 6)), false),
            ("unchanged", Icon(_ => { }), false),
        };
        int baseline = CornerRed(normal), fails = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var (name, img, want) in cases)
        {
            int red2 = CornerRed(img);
            bool got = red2 - baseline >= Math.Max(4, 32 * 32 / 60);
            if (got != want) fails++;
            sb.AppendLine($"{(got == want ? "ok  " : "FAIL")} {name}: {(got ? "badge" : "no badge")} ({red2} red vs normal {baseline})");
        }
        return (fails == 0 ? "ALL PASSED\n" : $"{fails} FAILED\n") + sb;
    }

    static int CornerRed(System.Windows.Media.Imaging.BitmapSource b)
    {
        try
        {
            var src = b.Format == PixelFormats.Bgra32 ? b : new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            src.CopyPixels(px, stride, 0);
            int red = 0;
            for (int y = 0; y < h; y++)
            {
                bool cornerRow = y < h * 0.45 || y >= h * 0.55;
                if (!cornerRow) continue;
                for (int x = (int)(w * 0.55); x < w; x++)
                {
                    int i = y * stride + x * 4;
                    byte bl = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                    if (a > 180 && r > 190 && g < 110 && bl < 110 && r - g > 110) red++;
                }
            }
            return red;
        }
        catch { return 0; }
    }

    /// <summary>
    /// A tray icon's picture changed. A red badge appearing in a corner (compared with how the icon looks while
    /// you're using the app) means something unread arrived; it going away means it was read.
    /// </summary>
    static void OnTrayIcon(TrayIconItem icon, ulong fingerprint)
    {
        try
        {
            if (icon.Pid == 0 || icon.Pid == (uint)Environment.ProcessId || icon.Icon is not System.Windows.Media.Imaging.BitmapSource bmp) return;
            var app = AppName(icon.Pid);
            if (app == null) return;
            if (Rule(app).Mode == "Off" && !Chatty.IsMatch(app)) return; // only apps you get messages from
            var key = $"{app}|{icon.UID}|{icon.Guid}";
            int red = CornerRed(bmp);
            int area = Math.Max(1, bmp.PixelWidth * bmp.PixelHeight);
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint fgPid);
            bool inApp = fgPid == icon.Pid || AppName(fgPid) == app;

            // The normal amount of corner red: what the icon shows while you're in the app (nothing unread then),
            // otherwise the least seen so far
            int baseline;
            if (inApp || !trayBaseline.TryGetValue(key, out baseline)) baseline = red;
            else baseline = Math.Min(baseline, red);
            trayBaseline[key] = baseline;
            bool badged = red - baseline >= Math.Max(4, area / 60); // a dot of at least ~1.6% of the icon
            trayBadged.TryGetValue(key, out bool was);
            if (badged == was) return;
            trayBadged[key] = badged;
            Log.Note("messages", $"{app}: tray icon {(badged ? "got" : "lost")} a red badge ({red} red pixels, normal {baseline})");
            if (!badged || inApp) { ClearAttention(app, looked: inApp); return; }

            if (!S.MessageApps.ContainsKey(app)) { S.MessageApps[app] = DefaultRule(app); SettingsStore.Save(); }
            var window = WindowTracker.TaskbarWindows().FirstOrDefault(w => w.Pid == icon.Pid)?.Hwnd ?? IntPtr.Zero;
            var who = CleanTitle(icon.Tip ?? "", app);
            AddAttention(app, icon.Pid, window, who.Length > 0 ? who : app, "New message (unread badge)", icon, "tray badge");
        }
        catch (Exception ex) { Log.Error("tray badge", ex); }
    }

    /// <summary>When each app last made news (so an icon flickering between badged and not isn't news every time).</summary>
    static readonly Dictionary<string, DateTime> lastNews = new();

    static void ClearAttention(string app, bool looked)
    {
        if (looked) lastNews.Remove(app); // you've been to the app: the next message is news again
        int n = attention.RemoveAll(a => a.App == app);
        if (n > 0)
        {
            Log.Note("messages", $"{app}: cleared {n} ({(looked ? "you opened it" : "its badge went away")})");
            Refilter();
        }
    }

    // ---------- badges: the unread count Store apps (Teams, WhatsApp, Outlook…) put on their icon

    static Dictionary<string, int>? badges;

    static void CheckBadges()
    {
        var now = NotificationStore.Badges();
        if (now == null) return; // nothing changed in Windows' notification records
        bool first = badges == null;
        var before = badges ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        badges = now;
        foreach (var (id, count) in now)
        {
            before.TryGetValue(id, out int was);
            if (count <= was) continue;
            var app = NameForAppId(id);
            if (app == null) continue;
            if (!S.MessageApps.ContainsKey(app)) { var r = DefaultRule(app); r.AppId = id; S.MessageApps[app] = r; SettingsStore.Save(); }
            AddAttention(app, 0, IntPtr.Zero, app, count == 1 ? "1 unread" : $"{count} unread", null, $"badge {count}", appId: id, quiet: first);
        }
        foreach (var id in before.Keys.Where(k => !now.ContainsKey(k)))
            if (NameForAppId(id) is { } gone) ClearAttention(gone, looked: true); // read (here or on another device)
    }

    static readonly Dictionary<string, string?> idNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Microsoft Teams" for "MSTeams_8wekyb3d8bbwe!MSTeams": your Messages settings, else the installed package's name.</summary>
    static string? NameForAppId(string id)
    {
        if (idNames.TryGetValue(id, out var n)) return n;
        n = S.MessageApps.FirstOrDefault(kv => string.Equals(kv.Value.AppId, id, StringComparison.OrdinalIgnoreCase)).Key;
        if (n == null && id.Contains('!'))
        {
            try { n = new Windows.Management.Deployment.PackageManager().FindPackagesForUser("", id[..id.IndexOf('!')]).FirstOrDefault()?.DisplayName; }
            catch { }
        }
        if (string.IsNullOrWhiteSpace(n) || n!.StartsWith("ms-resource:")) n = null;
        idNames[id] = n;
        return n;
    }

    /// <summary>Add (or refresh) a "new message" seen from an app's window, tray icon or badge, and pop out if it's news.</summary>
    static void AddAttention(string app, uint pid, IntPtr window, string sender, string text, TrayIconItem? tray, string how, string? appId = null, bool quiet = false)
    {
        var item = attention.FirstOrDefault(a => a.App == app && (a.Window == window || window == IntPtr.Zero || a.Window == IntPtr.Zero));
        bool fresh = !quiet && (!lastNews.TryGetValue(app, out var last) || DateTime.Now - last > TimeSpan.FromSeconds(45));
        if (fresh) lastNews[app] = DateTime.Now;
        if (item != null) attention.Remove(item);
        item = new MessageItem
        {
            Id = item?.Id ?? nextAttentionId++,
            App = app,
            Sender = sender,
            Text = text,
            Time = DateTime.Now,
            Window = window,
            Pid = pid,
            Tray = tray,
            AppId = appId,
            Icon = icons.TryGetValue(app, out var ic) ? ic : window != IntPtr.Zero ? WindowIcon(window, app) : tray?.Icon,
        };
        attention.Add(item);
        Refilter();
        bool passes = Passes(item);
        if (fresh) Log.Note("messages", $"{app}: {how} → {(passes ? "in the tab, pop-up asked" : $"hidden by your settings ({Rule(app).Mode})")} · tab shows {Visible.Count}");
        if (fresh && passes) Arrived?.Invoke(item);
    }

    static readonly Regex UnreadInTitle = new(@"^\s*[\(\[](\d{1,4})\+?[\)\]]|[\(\[](\d{1,4})\+?[\)\]]\s*$");

    /// <summary>An app window flashed for attention, or its title changed (maybe to an unread count).</summary>
    static void OnWindowSignal(IntPtr hwnd, bool flashed)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            var top = Native.GetAncestor(hwnd, 3 /* GA_ROOTOWNER */);
            if (top != IntPtr.Zero) hwnd = top;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return;
            if (flashed && Native.GetForegroundWindow() != hwnd) flashing[hwnd] = DateTime.Now; // for the dock's badge
            var title = Native.TitleOf(hwnd);
            int count = 0;
            if (!flashed)
            {
                // Only a rising unread count in the title counts as news
                var m = UnreadInTitle.Match(title);
                count = m.Success ? int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : 0;
                unread.TryGetValue(hwnd, out int before);
                unread[hwnd] = count;
                if (count <= before) return;
            }
            // Already looking at it: not news
            if (Native.GetForegroundWindow() == hwnd) return;

            var app = AppName(pid);
            if (app == null) return;
            if (!S.MessageApps.ContainsKey(app)) { S.MessageApps[app] = DefaultRule(app); SettingsStore.Save(); }

            var where = CleanTitle(title, app);
            AddAttention(app, pid, hwnd, where.Length > 0 ? where : app, count > 0 ? $"{count} unread" : "New message", null,
                flashed ? "asked for attention" : $"shows {count} unread");
        }
        catch (Exception ex) { Log.Error("attention", ex); }
    }

    /// <summary>"The Table | #general - Discord" → "The Table | #general" (the app's own name adds nothing).</summary>
    static string CleanTitle(string title, string app)
    {
        var t = UnreadInTitle.Replace(title, "").Trim();
        foreach (var sep in new[] { " - ", " | ", " — " })
        {
            if (t.EndsWith(sep + app, StringComparison.OrdinalIgnoreCase)) t = t[..^(sep.Length + app.Length)];
            if (t.StartsWith(app + sep, StringComparison.OrdinalIgnoreCase)) t = t[(app.Length + sep.Length)..];
        }
        return string.Equals(t, app, StringComparison.OrdinalIgnoreCase) ? "" : t.Trim();
    }

    static readonly Dictionary<uint, string?> appNames = new();
    static readonly Dictionary<string, string?> pathNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the app calls itself (its exe's product name: "Discord", "Microsoft Teams"), for any app.</summary>
    static string? AppName(uint pid)
    {
        if (appNames.TryGetValue(pid, out var n)) return n;
        var path = Native.ProcessPath(pid);
        n = path == null ? null : AppNameForPath(path);
        if (appNames.Count > 200) appNames.Clear();
        appNames[pid] = n;
        return n;
    }

    public static string? AppNameForPath(string path)
    {
        if (pathNames.TryGetValue(path, out var n)) return n;
        n = null;
        try
        {
            var exe = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!Regex.IsMatch(exe, "(?i)^(explorer|searchhost|shellexperiencehost|startmenuexperiencehost|applicationframehost|textinputhost|lockapp)$"))
            {
                var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                n = new[] { v.ProductName, v.FileDescription, exe }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s!.Length < 40)?.Trim();
                // A known name from the notification centre wins (keeps one entry per app in Settings)
                var known = S.MessageApps.Keys.FirstOrDefault(k => n != null && SameApp(k, n));
                if (known != null) n = known;
            }
        }
        catch { }
        if (pathNames.Count > 300) pathNames.Clear();
        pathNames[path] = n;
        return n;
    }

    static bool SameApp(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + " ", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + " ", StringComparison.OrdinalIgnoreCase);

    // ---------- the dock's "something to see" badge, for any app (like Windows' taskbar does)

    static readonly Dictionary<IntPtr, DateTime> flashing = new();

    /// <summary>
    /// What to show on an app's dock icon: a count (notifications waiting in Windows, or the app's own unread badge),
    /// "" for a plain dot (it flashed for attention), or null for nothing. Independent of your Messages filters.
    /// </summary>
    public static string? IndicatorFor(string? exePath, IReadOnlyCollection<AppWindow> windows)
    {
        var fg = Native.GetForegroundWindow();
        // Looking at it now: whatever it wanted, you've seen it
        foreach (var w in windows)
            if (w.Hwnd == fg) flashing.Remove(w.Hwnd);
        foreach (var dead in flashing.Where(f => DateTime.Now - f.Value > TimeSpan.FromMinutes(30) || !Native.IsWindow(f.Key)).Select(f => f.Key).ToList()) flashing.Remove(dead);

        var name = exePath != null ? AppNameForPath(exePath) : null;
        int count = 0;
        if (name != null)
        {
            count += all.Count(m => SameApp(m.App, name));
            if (badges != null)
                foreach (var (id, n) in badges)
                    if (NameForAppId(id) is { } bn && SameApp(bn, name)) count = Math.Max(count, n);
            count = Math.Max(count, attention.Where(a => SameApp(a.App, name) && a.Text.EndsWith("unread")).Select(a => int.TryParse(a.Text.Split(' ')[0], out var c) ? c : 1).DefaultIfEmpty(0).Max());
        }
        if (count > 0) return count > 9 ? "9+" : count.ToString();
        return windows.Any(w => flashing.ContainsKey(w.Hwnd)) ? "" : null;
    }

    static ImageSource? WindowIcon(IntPtr hwnd, string app)
    {
        var ic = IconHelper.ForWindow(hwnd);
        if (ic != null) icons[app] = ic;
        return ic;
    }

    /// <summary>You went to the app (its window is in front): its "new message" items are read.</summary>
    static void ClearSeenAttention()
    {
        if (attention.Count == 0) return;
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out uint fgPid);
        foreach (var a in attention.Where(a => a.Pid == fgPid)) lastNews.Remove(a.App); // you're in the app now
        var gone = attention.Where(a => a.Pid == fgPid || DateTime.Now - a.Time > TimeSpan.FromHours(2) ||
                                        (a.Window != IntPtr.Zero && !Native.IsWindow(a.Window))).ToList();
        foreach (var a in gone) attention.Remove(a);
        if (gone.Count > 0)
        {
            Log.Note("messages", "cleared " + string.Join(", ", gone.Select(a => $"{a.App} ({(a.Pid == fgPid ? "you're in it" : "old or closed")})")));
            Refilter();
        }
    }

    static async Task<bool> EnsureAccess()
    {
        try
        {
            listener ??= UserNotificationListener.Current;
            var status = listener.GetAccessStatus();
            if (status == UserNotificationListenerAccessStatus.Unspecified) status = await listener.RequestAccessAsync();
            Status = status switch
            {
                UserNotificationListenerAccessStatus.Allowed => null,
                UserNotificationListenerAccessStatus.Denied => "Turn on Settings › Privacy & security › Notifications › \"Let apps access your notifications\".",
                _ => "Windows hasn't said yet whether Brim may read notifications.",
            };
            return status == UserNotificationListenerAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Status = "This version of Windows doesn't let Brim read notifications.";
            Log.Error("messages access", ex);
            return false;
        }
    }

    public static async Task Refresh()
    {
        if (busy) return;
        busy = true;
        try
        {
            if (!await EnsureAccess()) { if (all.Count > 0) { all = new(); Refilter(); } return; }
            var list = await listener!.GetNotificationsAsync(NotificationKinds.Toast);
            var ids = list.Select(n => n.Id).ToHashSet();
            // Nothing new or gone: skip the work (this runs every few seconds)
            if (ids.SetEquals(all.Select(m => m.Id))) { primed = true; return; }

            var fresh = new List<MessageItem>();
            foreach (var n in list)
            {
                var old = all.FirstOrDefault(m => m.Id == n.Id);
                if (old != null) { fresh.Add(old); continue; }
                var item = Read(n);
                if (item != null) fresh.Add(item);
            }
            all = fresh.OrderByDescending(m => m.Time).ToList();

            Log.Note("messages", $"{all.Count} in Windows' notification centre from {string.Join(", ", all.Select(m => m.App).Distinct())}");
            bool settingsChanged = false;
            foreach (var m in all.GroupBy(m => m.App).Select(g => g.First()))
            {
                if (!S.MessageApps.TryGetValue(m.App, out var rule)) { rule = DefaultRule(m.App); S.MessageApps[m.App] = rule; settingsChanged = true; }
                if (rule.AppId == null && m.AppId != null) { rule.AppId = m.AppId; settingsChanged = true; }
            }
            if (settingsChanged) SettingsStore.Save();

            Refilter();

            foreach (var m in all.OrderBy(m => m.Time))
            {
                if (!seen.Add(m.Id) || !primed) continue;
                if (Passes(m) && DateTime.Now - m.Time < TimeSpan.FromMinutes(2)) Arrived?.Invoke(m);
            }
            primed = true;
            _ = LoadIcons();
        }
        catch (Exception ex) { Log.Error("messages", ex); }
        finally { busy = false; }
    }

    static MessageItem? Read(UserNotification n)
    {
        try
        {
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var lines = binding?.GetTextElements().Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList() ?? new();
            string app = "", appId = "";
            try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; appId = n.AppInfo?.AppUserModelId ?? ""; } catch { }
            if (app.Length == 0) app = "Other";
            return new MessageItem
            {
                Id = n.Id,
                App = app,
                AppId = appId.Length > 0 ? appId : null,
                Sender = lines.Count > 0 ? lines[0] : app,
                Text = string.Join("  ", lines.Skip(1)),
                Time = n.CreationTime.LocalDateTime,
            };
        }
        catch (Exception ex) { Log.Error("message read", ex); return null; }
    }

    static async Task LoadIcons()
    {
        try
        {
            var list = await listener!.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var n in list)
            {
                string app;
                try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? "Other"; } catch { continue; }
                if (icons.ContainsKey(app)) continue;
                icons[app] = null;
                try
                {
                    var logo = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(32, 32));
                    using var ras = await logo.OpenReadAsync();
                    using var stream = ras.AsStreamForRead();
                    var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    ms.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 32;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    icons[app] = bmp;
                }
                catch { }
            }
            bool any = false;
            foreach (var m in all)
                if (m.Icon == null && icons.TryGetValue(m.App, out var ic) && ic != null) { m.Icon = ic; any = true; }
            if (any) Changed?.Invoke();
        }
        catch { }
    }

    // ------------------------------------------------------------------ filters

    static MessageRule DefaultRule(string app) => new() { Mode = S.MessagesAllApps || Chatty.IsMatch(app) ? "All" : "Off" };

    /// <summary>"Show every app's notifications" on or off: every app follows (chat and mail apps always stay on).</summary>
    public static void SetAllApps(bool on)
    {
        S.MessagesAllApps = on;
        foreach (var (app, rule) in S.MessageApps)
            if (!Chatty.IsMatch(app) && rule.Mode != "People") rule.Mode = on ? "All" : "Off";
        SettingsStore.Save();
        Refilter();
    }

    public static MessageRule Rule(string app) => S.MessageApps.TryGetValue(app, out var r) ? r : DefaultRule(app);

    static bool Matches(string sender, string name) => sender.Contains(name, StringComparison.OrdinalIgnoreCase);

    public static bool Passes(MessageItem m)
    {
        var r = Rule(m.App);
        if (r.Mode == "Off") return false;
        if (r.Muted.Any(n => Matches(m.Sender, n))) return false;
        return r.Mode != "People" || r.People.Any(n => Matches(m.Sender, n));
    }

    public static void Refilter()
    {
        // Notifications plus what apps' windows told us, newest first; an app's window item is dropped when a
        // notification from the same app arrived around the same time (same message, seen twice)
        var fromWindows = attention.Where(a => !all.Any(n => n.App == a.App && Math.Abs((n.Time - a.Time).TotalSeconds) < 20));
        Visible = all.Concat(fromWindows).Where(Passes).OrderByDescending(m => m.Time).ToList();
        Changed?.Invoke();
    }

    /// <summary>
    /// Every app on this PC that has sent Windows a notification in the last few months (from Windows' own list),
    /// named the way Start names it, so Settings can offer it before anything is waiting in the notification centre.
    /// </summary>
    public static void Discover()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings");
            if (root == null) return;
            var apps = AppCatalog.All();
            bool changed = false;
            foreach (var id in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(id);
                if (k?.GetValue("LastNotificationAddedTime") is not long ft || DateTime.Now - DateTime.FromFileTime(ft) > TimeSpan.FromDays(90)) continue;
                var name = apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))?.Name;
                // Chat and mail apps up front; anything else appears once it has something waiting
                if (name == null || S.MessageApps.ContainsKey(name) || !Chatty.IsMatch(name)) continue;
                var rule = DefaultRule(name);
                rule.AppId = id;
                S.MessageApps[name] = rule;
                changed = true;
            }
            if (changed) SettingsStore.Save();
        }
        catch (Exception ex) { Log.Error("messages discover", ex); }
    }

    /// <summary>Apps to list in Settings: the ones that sent a notification, chat apps first.</summary>
    public static List<string> KnownApps() =>
        S.MessageApps.Keys.Concat(all.Select(m => m.App)).Concat(attention.Select(m => m.App)).Distinct()
            .OrderByDescending(a => Chatty.IsMatch(a)).ThenBy(a => a, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static string Describe(string app)
    {
        int n = all.Count(m => m.App == app);
        var r = Rule(app);
        string what = r.Mode switch
        {
            "Off" => "Not shown",
            "People" => r.People.Count == 0 ? "Only people you add (none yet)" : "Only " + string.Join(", ", r.People),
            _ => "Everything",
        };
        var parts = new List<string> { what };
        if (n > 0) parts.Add($"{n} waiting in Windows' notification centre");
        if (WindowsSays(app) is { } w) parts.Add(w);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// What Windows' own notification settings say about the app: turned off, not kept in the notification centre
    /// (so Brim never sees them), or when it last sent one. Read live, so it's right on any PC.
    /// </summary>
    public static string? WindowsSays(string app)
    {
        var id = Rule(app).AppId ?? all.FirstOrDefault(m => m.App == app)?.AppId;
        if (id == null) return null;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + id);
            if (k == null) return null;
            if (k.GetValue("Enabled") is int en && en == 0) return "its notifications are turned off in Windows Settings";
            if (k.GetValue("ShowInActionCenter") is int sc && sc == 0) return "Windows doesn't keep its notifications, so Brim can't see them";
            if (k.GetValue("LastNotificationAddedTime") is long ft && ft > 0)
            {
                var t = DateTime.FromFileTime(ft);
                var d = DateTime.Now - t;
                return "last sent Windows one " + (d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
                       : t.Date == DateTime.Today ? $"today at {t:HH:mm}" : t.Date == DateTime.Today.AddDays(-1) ? $"yesterday at {t:HH:mm}" : $"on {t:d MMM}");
            }
        }
        catch { }
        return null;
    }

    public static IEnumerable<string> RecentSenders(string app) =>
        all.Where(m => m.App == app).Select(m => m.Sender).Where(s => s.Length is > 0 and < 40).Distinct(StringComparer.OrdinalIgnoreCase);

    public static void Follow(MessageItem m)
    {
        var r = Rule(m.App);
        r.Mode = "People";
        if (!r.People.Contains(m.Sender, StringComparer.OrdinalIgnoreCase)) r.People.Add(m.Sender);
        r.Muted.RemoveAll(n => string.Equals(n, m.Sender, StringComparison.OrdinalIgnoreCase));
        Save(m.App, r);
    }

    public static void Mute(MessageItem m)
    {
        var r = Rule(m.App);
        if (!r.Muted.Contains(m.Sender, StringComparer.OrdinalIgnoreCase)) r.Muted.Add(m.Sender);
        r.People.RemoveAll(n => string.Equals(n, m.Sender, StringComparison.OrdinalIgnoreCase));
        Save(m.App, r);
    }

    public static void SetMode(string app, string mode)
    {
        var r = Rule(app);
        r.Mode = mode;
        Save(app, r);
    }

    static void Save(string app, MessageRule r)
    {
        S.MessageApps[app] = r;
        SettingsStore.Save();
        Refilter();
        WindowsBanners.Sync(); // an app turned off in Brim gets its Windows pop-up back
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Open the app the message came from.</summary>
    public static void Open(MessageItem m)
    {
        if (m.Window != IntPtr.Zero && Native.IsWindow(m.Window))
        {
            if (Native.IsIconic(m.Window)) Native.ShowWindow(m.Window, 9 /* SW_RESTORE */);
            Native.ForceForeground(m.Window);
            attention.Remove(m);
            Refilter();
            return;
        }
        if (m.Tray != null)
        {
            TrayService.Click(m.Tray, right: false); // the app opens itself, like clicking its tray icon
            attention.Remove(m);
            Refilter();
            return;
        }
        if (attention.Contains(m))
        {
            // From a badge: Windows has no link to the message itself, so open the app (it shows what's unread)
            if (m.AppId != null) Launcher.Open("shell:AppsFolder\\" + m.AppId);
            attention.Remove(m);
            Refilter();
            return;
        }
        // A notification: do what clicking it in Windows does (open that chat / email), else at least open the app
        bool opened = false;
        try { opened = NotificationStore.Open(m.Id); } catch (Exception ex) { Log.Error("open message", ex); }
        Log.Note("messages", $"opened a {m.App} message {(opened ? "where it points" : "(no link to it: opened the app)")}");
        if (!opened && m.AppId != null) Launcher.Open("shell:AppsFolder\\" + m.AppId);
        if (opened) Dismiss(m); // as in Windows: a notification you've opened leaves the notification centre
    }

    public static void Dismiss(MessageItem m)
    {
        if (m.FromWindow) attention.Remove(m);
        else
        {
            try { listener?.RemoveNotification(m.Id); } catch (Exception ex) { Log.Error("message dismiss", ex); }
            all.RemoveAll(x => x.Id == m.Id);
        }
        Refilter();
    }

    public static void DismissApp(string app)
    {
        foreach (var m in all.Where(x => x.App == app).ToList())
            try { listener?.RemoveNotification(m.Id); } catch { }
        all.RemoveAll(x => x.App == app);
        attention.RemoveAll(x => x.App == app);
        Refilter();
    }

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        return t.ToString("d MMM");
    }
}
