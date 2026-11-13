using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Brim.Core;

/// <summary>
/// Which apps are downloading something right now, for the dock's download badge. Windows' taskbar learns about
/// download progress through a private channel no other program can read, so this watches the Downloads folder for
/// the files browsers and apps write while downloading (Chrome / Edge / Teams ".crdownload", Firefox ".part"…) and
/// asks Windows which program has each one open. Works for any app that downloads that way, on any PC.
/// </summary>
public static class Downloads
{
    static readonly string[] TempExtensions = { ".crdownload", ".part", ".partial", ".download", ".opdownload", ".!ut", ".aria2" };
    static FileSystemWatcher? watcher;
    static string? folder;
    /// <summary>In-progress file → the programs that have it open (and their parents, e.g. Teams for its WebView)</summary>
    static readonly Dictionary<string, HashSet<uint>> active = new(StringComparer.OrdinalIgnoreCase);
    static readonly object gate = new();

    public static event Action? Changed;

    static bool IsTemp(string path) => TempExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static void Start()
    {
        if (watcher != null) return;
        try
        {
            folder = KnownDownloads();
            if (folder == null || !Directory.Exists(folder)) return;
            watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            watcher.Created += (_, e) => Seen(e.FullPath);
            watcher.Changed += (_, e) => Seen(e.FullPath);
            watcher.Renamed += (_, e) => { Gone(e.OldFullPath); Seen(e.FullPath); };
            watcher.Deleted += (_, e) => Gone(e.FullPath);
            watcher.EnableRaisingEvents = true;
            foreach (var f in Directory.EnumerateFiles(folder)) Seen(f); // downloads already running
        }
        catch (Exception ex) { Log.Error("downloads watch", ex); }
    }

    static void Seen(string path)
    {
        if (!IsTemp(path)) return;
        lock (gate) if (active.TryGetValue(path, out var known) && known.Count > 0) return;
        // Asking Windows who has the file open takes a moment: do it off the UI thread
        System.Threading.Tasks.Task.Run(() =>
        {
            var owners = Owners(path);
            bool changed;
            lock (gate)
            {
                changed = !active.ContainsKey(path) || active[path].Count != owners.Count;
                if (File.Exists(path)) active[path] = owners; else active.Remove(path);
            }
            if (changed)
            {
                Log.Note("downloads", $"{Path.GetFileName(path)}: being downloaded by " +
                    string.Join(", ", owners.Select(p => Path.GetFileNameWithoutExtension(Native.ProcessPath(p) ?? "?")).Distinct()));
                Notify();
            }
        });
    }

    static void Gone(string path)
    {
        bool removed;
        lock (gate) removed = active.Remove(path);
        if (removed) Notify();
    }

    static void Notify() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));

    /// <summary>Is this app (its exe, or one of its windows' processes) downloading?</summary>
    public static bool ActiveFor(string? exePath, IEnumerable<uint> pids)
    {
        lock (gate)
        {
            // Forget files that finished or were cancelled without us hearing about it
            foreach (var dead in active.Keys.Where(p => !File.Exists(p)).ToList()) active.Remove(dead);
            if (active.Count == 0) return false;
            var mine = new HashSet<uint>(pids);
            foreach (var owners in active.Values)
            {
                if (owners.Overlaps(mine)) return true;
                if (exePath != null)
                    foreach (var pid in owners)
                        if (string.Equals(Native.ProcessPath(pid), exePath, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ who has the file open (Restart Manager)

    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint handle, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint handle, uint nFiles, string[] files, uint nApps, IntPtr apps, uint nServices, string[]? services);
    [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? info, ref uint reasons);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint handle);

    /// <summary>The programs that have the file open, plus their parent programs (an app's built-in browser downloads for it).</summary>
    static HashSet<uint> Owners(string path)
    {
        var result = new HashSet<uint>();
        if (RmStartSession(out uint h, 0, new StringBuilder(64)) != 0) return result;
        try
        {
            if (RmRegisterResources(h, 1, new[] { path }, 0, IntPtr.Zero, 0, null) != 0) return result;
            uint count = 0, reasons = 0;
            int rc = RmGetList(h, out uint needed, ref count, null, ref reasons);
            if (rc == 234 /* ERROR_MORE_DATA */ && needed > 0)
            {
                var info = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(h, out needed, ref count, info, ref reasons) == 0)
                    for (int i = 0; i < count; i++) result.Add((uint)info[i].Process.dwProcessId);
            }
        }
        catch { }
        finally { RmEndSession(h); }
        foreach (var pid in result.ToList())
            foreach (var parent in Parents(pid)) result.Add(parent);
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    /// <summary>Up to four parent processes (Teams → its WebView, Chrome → its helper…), stopping at system ones.</summary>
    static IEnumerable<uint> Parents(uint pid)
    {
        var parents = new Dictionary<uint, (uint parent, string exe)>();
        var snap = CreateToolhelp32Snapshot(0x2 /* PROCESSES */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) yield break;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32FirstW(snap, ref e))
                do parents[e.th32ProcessID] = (e.th32ParentProcessID, e.szExeFile);
                while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        var cur = pid;
        for (int i = 0; i < 4 && parents.TryGetValue(cur, out var p); i++)
        {
            if (!parents.TryGetValue(p.parent, out var up)) break;
            if (System.Text.RegularExpressions.Regex.IsMatch(up.exe, "(?i)^(explorer|svchost|services|wininit|winlogon|sihost|runtimebroker)\\.exe$")) break;
            yield return p.parent;
            cur = p.parent;
        }
    }

    // ------------------------------------------------------------------ where downloads go on this PC

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static string? KnownDownloads()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                return s;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
