using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace Brim.Core;

public sealed class TrayIconItem : Observable
{
    public uint HWnd { get; set; }
    public uint UID { get; set; }
    public Guid Guid { get; set; }
    public uint Callback { get; set; }
    public uint Version { get; set; }
    public uint Pid { get; set; }
    /// <summary>Brim already asked the app to register this icon again (to learn what it's missing)</summary>
    public bool AskedAgain { get; set; }

    ImageSource? icon;
    public ImageSource? Icon { get => icon; set => Set(ref icon, value); }

    string tip = "";
    public string Tip { get => tip; set { Set(ref tip, value); RaiseChanged(nameof(Label)); } }

    string? appName;

    /// <summary>
    /// What to call it when you point at it: the app's own tooltip, or, when it never set one (most apps, or it set
    /// it before Brim was listening), the app's name from its program ("Steam", "NVIDIA App"…).
    /// </summary>
    public string Label => !string.IsNullOrWhiteSpace(tip) ? tip : appName ??= NameOfApp();

    string NameOfApp()
    {
        uint pid = Pid;
        if (pid == 0) Native.GetWindowThreadProcessId(new IntPtr(HWnd), out pid);
        var path = pid != 0 ? Native.ProcessPath(pid) : null;
        return path != null ? IconHelper.NiceName(path) : "";
    }

    bool hidden;
    public bool Hidden { get => hidden; set => Set(ref hidden, value); }

    /// <summary>
    /// An icon only Windows' own tray has (the app added it before Brim was listening and never adds it again): known
    /// from Windows' record of tray icons, by its program. Clicking it brings the app up rather than passing the click on.
    /// </summary>
    public string? ExePath { get; init; }
    public bool FromWindowsList => ExePath != null && HWnd == 0;
    /// <summary>Icon numbers to try as well, when Windows keeps the icon under an id instead (only the right one reacts).</summary>
    public uint[]? OtherUids { get; init; }

    public bool Matches(uint hwnd, uint uid, Guid guid) =>
        guid != Guid.Empty && Guid == guid || guid == Guid.Empty && HWnd == hwnd && UID == uid;
}

/// <summary>
/// Collects notification-area icons so the dock can show them itself.
/// Apps find the tray with FindWindow("Shell_TrayWnd"), so Brim registers a hidden, topmost window of that class,
/// keeps a copy of every icon message, and passes each one on to Explorer so its taskbar stays complete.
/// Only runs while the dock replaces the taskbar.
/// </summary>
public sealed class TrayService : IDisposable
{
    public static TrayService? Instance { get; private set; }

    /// <summary>A tray icon's picture changed (or appeared): the icon, and a fingerprint of its pixels.</summary>
    public static event Action<TrayIconItem, ulong>? IconChanged;

    public static int IconMessages;
    static readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    readonly System.Collections.Generic.Dictionary<TrayIconItem, IntPtr> pendingIcons = new();
    readonly System.Collections.Generic.Dictionary<(uint, uint, Guid), uint> pendingVersions = new();
    System.Windows.Threading.DispatcherTimer? iconTimer;

    System.Windows.Threading.DispatcherTimer MakeIconTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            foreach (var (item, h) in pendingIcons)
            {
                item.Icon = IconHelper.FromHIcon(h, true) ?? item.Icon; // destroys the copy
                if (item.Icon is System.Windows.Media.Imaging.BitmapSource bmp && IconChanged != null) IconChanged(item, Fingerprint(bmp));
            }
            pendingIcons.Clear();
        };
        return t;
    }

    static ulong Fingerprint(System.Windows.Media.Imaging.BitmapSource b)
    {
        try
        {
            var conv = b.Format == PixelFormats.Bgra32 ? b : new System.Windows.Media.Imaging.FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
            int stride = conv.PixelWidth * 4;
            var px = new byte[stride * conv.PixelHeight];
            conv.CopyPixels(px, stride, 0);
            ulong h = 1469598103934665603UL;
            foreach (var x in px) { h ^= x; h *= 1099511628211UL; }
            return h;
        }
        catch { return 0; }
    }
    public ObservableCollection<TrayIconItem> Icons { get; } = new();

    readonly Native.WndProcDelegate proc;
    readonly IntPtr hInstance = Native.GetModuleHandle(null);
    IntPtr hwnd, notifyHwnd;
    const string TrayClass = "Shell_TrayWnd";
    const string NotifyClass = "TrayNotifyWnd";
    const int Signature = 0x34753423;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA32
    {
        public int cbSize;
        public uint hWnd, uID, uFlags, uCallbackMessage, hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public uint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLTRAYDATA
    {
        public int dwSignature;
        public uint dwMessage;
        public NOTIFYICONDATA32 nid;
    }

    TrayService()
    {
        proc = WndProc;
    }

    public static void Start()
    {
        if (Instance != null) return;
        var t = new TrayService();
        if (t.Create()) Instance = t;
    }

    public static void Stop()
    {
        Instance?.Dispose();
        Instance = null;
    }

    bool Create()
    {
        try
        {
            foreach (var cls in new[] { TrayClass, NotifyClass })
            {
                var wc = new Native.WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
                    hInstance = hInstance,
                    lpszClassName = cls,
                };
                Native.RegisterClassEx(ref wc);
            }
            hwnd = Native.CreateWindowEx((uint)(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW), TrayClass, "", Native.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return false;
            notifyHwnd = Native.CreateWindowEx(0, NotifyClass, "", Native.WS_CHILD, 0, 0, 0, 0, hwnd, IntPtr.Zero, hInstance, IntPtr.Zero);
            KeepInFront();
            Rescan();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("tray service", ex);
            return false;
        }
    }

    /// <summary>Must stay ahead of Explorer's window so FindWindow returns ours.</summary>
    public void KeepInFront()
    {
        Log.Trace("tray stand-in: back on top");
        if (hwnd != IntPtr.Zero)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Ask every app to re-register its icon (the same thing Explorer does after it restarts).</summary>
    public void Rescan()
    {
        uint msg = Native.RegisterWindowMessage("TaskbarCreated");
        Native.SendNotifyMessage(new IntPtr(0xFFFF), msg, IntPtr.Zero, IntPtr.Zero);
    }

    public void Prune()
    {
        foreach (var dead in Icons.Where(i => !Native.IsWindow(new IntPtr(i.HWnd))).ToList()) Icons.Remove(dead);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClipboardFormatName(uint format, System.Text.StringBuilder name, int size);

    IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        // While tracing: every message Windows and apps send "the taskbar" (Brim's stand-in gets them first)
        if (Log.Tracing && h == hwnd && msg != Native.WM_COPYDATA)
        {
            var name = new System.Text.StringBuilder(64);
            if (msg >= 0xC000) GetClipboardFormatName(msg, name, 64); // a registered message: its name
            uint how = Native.InSendMessageEx(IntPtr.Zero);
            Log.Trace($"tray stand-in got 0x{msg:X4} {name} w=0x{w.ToInt64():X} l=0x{l.ToInt64():X} {((how & 1) != 0 ? "sent" : "posted")}");
        }
        try
        {
            if (h == hwnd && msg == Native.WM_COPYDATA)
            {
                var cds = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(l);
                if (cds.dwData.ToInt64() == 1)
                {
                    IconMessages++;
                    // A tray icon update. Take a copy, let the app carry on straight away, then pass the copy to
                    // Explorer. (Holding the app until Explorer answered could deadlock: Explorer sometimes needs
                    // something from that same app first. Discord, updating its icon several times a second, hung Brim.)
                    var bytes = new byte[Math.Max(0, cds.cbData)];
                    if (bytes.Length > 0) Marshal.Copy(cds.lpData, bytes, 0, bytes.Length);
                    Native.ReplyMessage(new IntPtr(1));
                    ForwardCopy(w, 1, bytes);
                    HandleIconMessage(bytes);
                    return new IntPtr(1);
                }
                return Forward(msg, w, l);
            }
            if (h == hwnd && msg == Native.WM_COMMAND) return Forward(msg, w, l);
            // Everything else apps ask "the taskbar" (private messages and registered ones, e.g. Windows asking
            // whether a fullscreen app is running, which Discord and Chrome check before showing a notification)
            // goes to Explorer's real taskbar, and its answer comes back. Unanswered, Discord stayed silent.
            // Only direct questions (sent and waiting for an answer); broadcasts every window gets, Explorer gets itself.
            if (h == hwnd && msg >= 0x0400 /* WM_USER and up, including registered messages */ && msg != taskbarCreated)
            {
                uint how = Native.InSendMessageEx(IntPtr.Zero);
                if ((how & 0x1 /* ISMEX_SEND */) != 0 && (how & 0x2 /* ISMEX_NOTIFY */) == 0) return Forward(msg, w, l);
                // Posted ones too, the taskbar's own private messages (not the registered ones, which can be broadcasts
                // Explorer gets itself). Windows' three-finger "show desktop" talks to the taskbar with both kinds;
                // keeping the posted half back broke the gesture whenever Brim was running.
                if ((how & 0x1) == 0 && msg < 0x8000 && Native.ExplorerTray() is var real && real != IntPtr.Zero)
                {
                    Native.PostMessage(real, (int)msg, w, l);
                    return IntPtr.Zero;
                }
            }
        }
        catch (Exception ex) { Log.Error("tray wndproc", ex); }
        return Native.DefWindowProc(h, msg, w, l);
    }

    static IntPtr Forward(uint msg, IntPtr w, IntPtr l)
    {
        var real = Native.ExplorerTray();
        if (real == IntPtr.Zero) return IntPtr.Zero;
        Native.SendMessageTimeout(real, (int)msg, w, l, 2 /* ABORTIFHUNG */, 1500, out IntPtr res);
        return res;
    }

    /// <summary>Send our own copy of a WM_COPYDATA message to Explorer's taskbar (it gets every icon too).</summary>
    static void ForwardCopy(IntPtr sender, long dwData, byte[] data)
    {
        var real = Native.ExplorerTray();
        if (real == IntPtr.Zero) return;
        IntPtr buf = Marshal.AllocHGlobal(Math.Max(1, data.Length));
        IntPtr cdsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.COPYDATASTRUCT>());
        try
        {
            if (data.Length > 0) Marshal.Copy(data, 0, buf, data.Length);
            Marshal.StructureToPtr(new Native.COPYDATASTRUCT { dwData = new IntPtr(dwData), cbData = data.Length, lpData = buf }, cdsPtr, false);
            Native.SendMessageTimeout(real, (int)Native.WM_COPYDATA, sender, cdsPtr, 2 /* ABORTIFHUNG */, 1000, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(cdsPtr);
            Marshal.FreeHGlobal(buf);
        }
    }

    void HandleIconMessage(byte[] received)
    {
        int size = Marshal.SizeOf<SHELLTRAYDATA>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            // Copy only what the sender gave us into a zeroed buffer: older apps send shorter structures
            var bytes = new byte[size];
            Array.Copy(received, bytes, Math.Min(size, received.Length));
            Marshal.Copy(bytes, 0, buf, size);
            var data = Marshal.PtrToStructure<SHELLTRAYDATA>(buf);
            if (data.dwSignature != Signature) return;
            var n = data.nid;
            Guid guid = (n.uFlags & 0x20) != 0 ? n.guidItem : Guid.Empty;
            var existing = Icons.FirstOrDefault(i => i.Matches(n.hWnd, n.uID, guid));

            switch (data.dwMessage)
            {
                case 0: // NIM_ADD
                case 1: // NIM_MODIFY
                    if (existing == null)
                    {
                        if (n.hWnd == 0) return;
                        Native.GetWindowThreadProcessId(new IntPtr(n.hWnd), out uint pid);
                        existing = new TrayIconItem { HWnd = n.hWnd, UID = n.uID, Guid = guid, Pid = pid };
                        if (pendingVersions.Remove((n.hWnd, n.uID, guid), out var v)) existing.Version = v;
                        Icons.Add(existing);
                    }
                    if ((n.uFlags & 0x1) != 0) { existing.Callback = n.uCallbackMessage; Remember(existing); }
                    if ((n.uFlags & 0x2) != 0 && n.hIcon != 0)
                    {
                        // Some apps swap their icon several times a second (Discord while you talk in a call). Keep a
                        // cheap copy of the newest one and turn it into a picture at most twice a second.
                        if (existing.Icon == null)
                        {
                            existing.Icon = IconHelper.FromHIcon(new IntPtr(n.hIcon), false);
                            if (existing.Icon is System.Windows.Media.Imaging.BitmapSource first && IconChanged != null)
                            {
                                var item = existing;
                                var fp = Fingerprint(first);
                                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => IconChanged?.Invoke(item, fp)),
                                    System.Windows.Threading.DispatcherPriority.Background);
                            }
                        }
                        else
                        {
                            var copy = Native.CopyIcon(new IntPtr(n.hIcon));
                            if (copy != IntPtr.Zero)
                            {
                                if (pendingIcons.TryGetValue(existing, out var older)) Native.DestroyIcon(older);
                                pendingIcons[existing] = copy;
                                iconTimer ??= MakeIconTimer();
                                if (!iconTimer.IsEnabled) iconTimer.Start();
                            }
                        }
                    }
                    if ((n.uFlags & 0x4) != 0) existing.Tip = n.szTip ?? "";
                    if ((n.uFlags & 0x8) != 0 && (n.dwStateMask & 1) != 0) existing.Hidden = (n.dwState & 1) != 0;
                    // An icon Brim only knows from later updates (it was registered with Windows before Brim took over
                    // the tray) has no click message yet, so clicks on it would go nowhere: ask the app to register again
                    if (existing.Callback == 0 && !existing.AskedAgain) { existing.AskedAgain = true; AskToRegisterAgain(existing); }
                    break;
                case 2: // NIM_DELETE
                    if (existing != null)
                    {
                        if (pendingIcons.Remove(existing, out var waiting)) Native.DestroyIcon(waiting);
                        Icons.Remove(existing);
                    }
                    break;
                case 4: // NIM_SETVERSION
                    if (existing != null) { existing.Version = n.uVersion; Remember(existing); }
                    else
                    {
                        // Announced before the icon itself arrived: keep it for when it does
                        pendingVersions[(n.hWnd, n.uID, guid)] = n.uVersion;
                    }
                    break;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The same "the taskbar was just created, add your icons" Windows sends after Explorer restarts, to one app.</summary>
    static void AskToRegisterAgain(TrayIconItem icon)
    {
        Native.SendNotifyMessage(new IntPtr(icon.HWnd), taskbarCreated, IntPtr.Zero, IntPtr.Zero);
        Native.PostMessage(new IntPtr(icon.HWnd), (int)taskbarCreated, IntPtr.Zero, IntPtr.Zero); // (some only read their queue)
        // Hidden message-only windows of the app (where some apps listen)
        for (IntPtr h = IntPtr.Zero; (h = Native.FindWindowEx(new IntPtr(-3) /* HWND_MESSAGE */, h, null, null)) != IntPtr.Zero;)
        {
            Native.GetWindowThreadProcessId(h, out uint mp);
            if (mp == icon.Pid) Native.SendNotifyMessage(h, taskbarCreated, IntPtr.Zero, IntPtr.Zero);
        }
        // Some apps listen on their main window rather than the icon's: the other top-level windows of that process too
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint p);
            if (p == icon.Pid && h != new IntPtr(icon.HWnd)) Native.SendNotifyMessage(h, taskbarCreated, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Windows writes program paths here starting with a known folder's id ("{6D809377…}\Steam\steam.exe").</summary>
    static string ExpandKnownFolder(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(path, @"^\{([0-9A-Fa-f\-]{36})\}(.*)$");
        if (!m.Success) return Environment.ExpandEnvironmentVariables(path);
        var id = Guid.Parse(m.Groups[1].Value);
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return path;
        try { return Marshal.PtrToStringUni(p) + m.Groups[2].Value; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>
    /// Running apps that have an icon in Windows' own tray which Brim never got (apps add their icon once, and some
    /// did before Brim was listening and never do it again): from Windows' record of tray icons, with its picture.
    /// </summary>
    public static System.Collections.Generic.List<TrayIconItem> WindowsOnly(System.Collections.Generic.IEnumerable<TrayIconItem> have)
    {
        var list = new System.Collections.Generic.List<TrayIconItem>();
        try
        {
            var running = new System.Collections.Generic.Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in System.Diagnostics.Process.GetProcesses())
                if (Native.ProcessPath((uint)p.Id) is { } path) running.TryAdd(path, (uint)p.Id);
            // Programs Brim already shows an icon for
            var shown = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in have)
            {
                uint pid = i.Pid;
                if (pid == 0) Native.GetWindowThreadProcessId(new IntPtr(i.HWnd), out pid);
                if (Native.ProcessPath(pid) is { } path) shown.Add(path);
            }
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return list;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("ExecutablePath") is not string raw) continue;
                var exe = ExpandKnownFolder(raw);
                if (!running.TryGetValue(exe, out uint pid) || shown.Contains(exe)) continue;
                var file = System.IO.Path.GetFileName(exe).ToLowerInvariant();
                if (file is "explorer.exe" or "brim.exe") continue; // Windows' own icons (network, sound…) are on the dock already
                shown.Add(exe);
                System.Windows.Media.ImageSource? picture = null;
                if (k.GetValue("IconSnapshot") is byte[] png && png.Length > 8)
                    try
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = new System.IO.MemoryStream(png);
                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        picture = bmp;
                    }
                    catch { }
                var tip = k.GetValue("InitialTooltip") as string;
                if (file == "monotificationux.exe") tip = "Windows Update"; // (its program is called "Notifications host process")
                var bare = new TrayIconItem { ExePath = exe, Pid = pid, Icon = picture ?? IconHelper.ForPath(exe), Tip = string.IsNullOrWhiteSpace(tip) ? "" : tip };
                list.Add(file == "monotificationux.exe" ? bare : Wire(bare, pid, file));
            }
        }
        catch (Exception ex) { Log.Error("tray icons from windows", ex); }
        return list;
    }

    /// <summary>Remember how this app's icon is clicked, for a later session where the app starts before Brim.</summary>
    static void Remember(TrayIconItem i)
    {
        if (i.Callback == 0 || i.HWnd == 0) return;
        var exe = ExeOf(i);
        if (exe.Length == 0 || exe is "explorer.exe" or "brim.exe") return;
        var cls = Native.ClassOf(new IntPtr(i.HWnd));
        var map = SettingsStore.Current.TrayWiring;
        if (map.TryGetValue(exe, out var w) && w.Class == cls && w.Callback == i.Callback && w.Version == i.Version) return;
        map[exe] = new TrayWiring { Class = cls, Uid = i.UID, Callback = i.Callback, Version = i.Version };
        SettingsStore.Save();
    }

    [StructLayout(LayoutKind.Sequential)] struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out Native.RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    /// <summary>Every window of a process, message-only ones included (apps often keep their tray icon on one).</summary>
    static System.Collections.Generic.List<IntPtr> WindowsOf(uint pid)
    {
        var list = new System.Collections.Generic.List<IntPtr>();
        Native.EnumWindows((h, _) => { Native.GetWindowThreadProcessId(h, out uint p); if (p == pid) list.Add(h); return true; }, IntPtr.Zero);
        for (IntPtr h = FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, null); h != IntPtr.Zero; h = FindWindowEx(new IntPtr(-3), h, null, null))
        {
            Native.GetWindowThreadProcessId(h, out uint p);
            if (p == pid) list.Add(h);
        }
        return list;
    }

    /// <summary>The icon number Windows has for this window's icon (null when it keeps it under an id instead).</summary>
    static uint? IconNumber(IntPtr hwnd)
    {
        for (uint uid = 0; uid <= 64; uid++)
        {
            var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = hwnd, uID = uid };
            if (Shell_NotifyIconGetRect(ref id, out _) == 0) return uid;
        }
        return null;
    }

    static readonly System.Collections.Generic.Dictionary<uint, DateTime> askedAgainAt = new();

    /// <summary>
    /// Wire up an icon only Windows' tray has, so a click (a right-click above all) reaches the app the way Windows
    /// would send it: from how Brim saw this app's icon before, or Chrome's and Electron's standard way (Discord, Copilot…).
    /// Meanwhile the app is asked once more to give Brim its icon (Vanguard and Wallpaper Engine do).
    /// </summary>
    static TrayIconItem Wire(TrayIconItem bare, uint pid, string file)
    {
        if (!askedAgainAt.TryGetValue(pid, out var at) || DateTime.Now - at > TimeSpan.FromMinutes(2))
        {
            askedAgainAt[pid] = DateTime.Now;
            foreach (var h in WindowsOf(pid)) Native.SendNotifyMessage(h, taskbarCreated, IntPtr.Zero, IntPtr.Zero);
        }
        var windows = WindowsOf(pid);
        IntPtr target = IntPtr.Zero;
        uint uid = 0, callback = 0, version = 0;
        uint[]? others = null;
        if (SettingsStore.Current.TrayWiring.TryGetValue(file, out var w) &&
            windows.FirstOrDefault(h => Native.ClassOf(h) == w.Class) is var known && known != IntPtr.Zero)
        {
            target = known; uid = IconNumber(known) ?? w.Uid; callback = w.Callback; version = w.Version;
            if (IconNumber(known) == null) others = Enumerable.Range(1, 10).Select(n => (uint)n).Where(n => n != uid).ToArray();
        }
        else if (windows.FirstOrDefault(h => Native.ClassOf(h) is "Electron_NotifyIconHostWindow" or "Chrome_StatusTrayWindow") is var chrome && chrome != IntPtr.Zero)
        {
            // Chrome and Electron: their icons listen for WM_APP + 1 and number from 1
            target = chrome; callback = 0x8001; version = 0;
            if (IconNumber(chrome) is uint n) uid = n;
            else { uid = 1; others = Enumerable.Range(2, 9).Select(x => (uint)x).ToArray(); }
        }
        if (target == IntPtr.Zero) return bare;
        return new TrayIconItem
        {
            HWnd = (uint)target.ToInt64(), UID = uid, Callback = callback, Version = version, OtherUids = others,
            Pid = pid, Icon = bare.Icon, Tip = bare.Tip, ExePath = bare.ExePath,
        };
    }

    /// <summary>A click on an icon only Windows' tray has: bring the app's window up, or open the app (which for a
    /// tray app usually shows its window).</summary>
    public static void OpenApp(TrayIconItem icon)
    {
        if (KnownAction(icon)) return;
        if (OwnWindow(icon)) return;
        if (icon.ExePath != null) { Log.Note("tray", $"opening {System.IO.Path.GetFileName(icon.ExePath)} (its icon is only in Windows' own tray)"); Launcher.Open(icon.ExePath); }
    }

    static string ExeOf(TrayIconItem icon)
    {
        uint pid = icon.Pid;
        if (pid == 0) Native.GetWindowThreadProcessId(new IntPtr(icon.HWnd), out pid);
        return System.IO.Path.GetFileName(Native.ProcessPath(pid) ?? "").ToLowerInvariant();
    }

    /// <summary>
    /// Icons of Windows' own that only ever talk to Windows' tray: what clicking them does, done directly.
    /// </summary>
    static bool KnownAction(TrayIconItem icon)
    {
        string? open = ExeOf(icon) switch
        {
            "securityhealthsystray.exe" => "windowsdefender:", // Windows Security
            "monotificationux.exe" => "ms-settings:windowsupdate", // Windows Update (an update waiting, or a restart)
            _ => null,
        };
        if (open == null) return false;
        Brim.Views.Flyout.CloseOpen();
        Log.Note("tray", $"'{icon.Tip.Split('\n')[0]}': opening {open} (it only talks to Windows' own tray)");
        Launcher.Open(open);
        return true;
    }

    /// <summary>The app behind the icon has a window: bring it up.</summary>
    static bool OwnWindow(TrayIconItem icon)
    {
        uint pid = icon.Pid;
        if (pid == 0) Native.GetWindowThreadProcessId(new IntPtr(icon.HWnd), out pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        IntPtr main = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint p);
            if (p != pid || Native.TitleOf(h).Length == 0 || Native.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
            // A visible window first; a hidden main window (an app tucked into the tray) as a last resort is not shown,
            // because showing a window an app hid can leave it half-drawn
            if (!Native.IsWindowVisible(h)) return true;
            main = h;
            return false;
        }, IntPtr.Zero);
        if (main == IntPtr.Zero) return false;
        Brim.Views.Flyout.CloseOpen();
        Log.Note("tray", $"'{icon.Tip.Split('\n')[0]}': bringing up its window '{Native.TitleOf(main)}'");
        Native.ForceForeground(main);
        return true;
    }

    /// <summary>Pass a click on to the app that owns the icon, the way Explorer would.</summary>
    public static async void Click(TrayIconItem icon, bool right, bool doubleClick = false)
    {
        var owner = new IntPtr(icon.HWnd);
        if (!Native.IsWindow(owner)) { Instance?.Icons.Remove(icon); return; }
        if (icon.Callback == 0)
        {
            // Apps Brim knows the answer for (Windows Security never registers again): straight there
            if (KnownAction(icon)) return;
            // Still don't know which message this app wants: ask it to register again and wait a moment for it
            AskToRegisterAgain(icon);
            for (int i = 0; i < 12 && icon.Callback == 0; i++)
            {
                await System.Threading.Tasks.Task.Delay(50);
                if (Instance?.Icons.FirstOrDefault(x => x.Matches(icon.HWnd, icon.UID, icon.Guid)) is { } fresh && fresh != icon) icon = fresh;
            }
            Log.Note("tray", icon.Callback == 0 ? $"'{icon.Tip}' never said which message it wants: showing Windows' taskbar, where it can be clicked" : $"'{icon.Tip}' registered again: message 0x{icon.Callback:X}");
            if (icon.Callback == 0)
            {
                // An app with a window of its own: bring that up (what clicking its icon nearly always does)
                if (OwnWindow(icon)) return;
                // This app only talks to Windows' own tray: bring that up for a moment so the icon can be clicked there
                // (the dock steps aside meanwhile, or it would sit on top of it)
                Brim.Views.Flyout.CloseOpen();
                TaskbarManager.Peek();
                return;
            }
        }
        bool allowed = Native.AllowSetForegroundWindow(icon.Pid);
        // Give the app's own (hidden) tray window focus first, like Windows' taskbar does. Apps show their menu
        // with TrackPopupMenu, which closes the menu straight away if their window isn't the focused one.
        Native.ForceForeground(owner);
        bool front = Native.GetForegroundWindow() == owner;
        Native.GetCursorPos(out var pt);
        Native.GetWindowThreadProcessId(owner, out uint ownerPid);
        Log.Note("tray", $"{(right ? "right" : doubleClick ? "double" : "left")}-click on '{icon.Tip.Split('\n')[0]}' " +
            $"({System.IO.Path.GetFileName(Native.ProcessPath(ownerPid) ?? "?")}, format v{icon.Version}, id {icon.UID}{(icon.Guid != Guid.Empty ? ", by guid" : "")}, " +
            $"window {Native.ClassOf(owner)}, message 0x{icon.Callback:X}, allowed to come forward {allowed}, its window in front {front}, at {pt.X},{pt.Y})");

        void Send(int mouseMsg)
        {
            foreach (var uid in (icon.OtherUids ?? Array.Empty<uint>()).Prepend(icon.UID))
            {
                IntPtr wp, lp;
                if (icon.Version >= 4)
                {
                    wp = Native.MakeLParam(pt.X, pt.Y);
                    lp = Native.MakeLParam(mouseMsg, (int)uid);
                }
                else
                {
                    wp = new IntPtr(uid);
                    lp = new IntPtr(mouseMsg);
                }
                Native.SendNotifyMessage(owner, icon.Callback, wp, lp);
            }
        }

        if (right)
        {
            Send(Native.WM_RBUTTONDOWN);
            Send(Native.WM_RBUTTONUP);
            if (icon.Version >= 4) Send(Native.WM_CONTEXTMENU);
        }
        else if (doubleClick)
        {
            Send(Native.WM_LBUTTONDBLCLK);
            Send(Native.WM_LBUTTONUP);
        }
        else
        {
            Send(Native.WM_LBUTTONDOWN);
            Send(Native.WM_LBUTTONUP);
            if (icon.Version >= 4) Send(Native.WM_USER); // NIN_SELECT
        }
    }

    public void Dispose()
    {
        if (notifyHwnd != IntPtr.Zero) Native.DestroyWindow(notifyHwnd);
        if (hwnd != IntPtr.Zero) Native.DestroyWindow(hwnd);
        hwnd = notifyHwnd = IntPtr.Zero;
        Native.UnregisterClass(NotifyClass, hInstance);
        Native.UnregisterClass(TrayClass, hInstance);
        Icons.Clear();
    }
}
