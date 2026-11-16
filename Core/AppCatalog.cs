using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Brim.Core;

/// <summary>An app from the Start menu. Desktop apps carry their exe; Store apps launch by their app id.</summary>
public sealed record AppEntry(string Name, string Id, string? Exe)
{
    /// <summary>What a shortcut or dock pin stores: the real exe when there is one (so the dock can match the
    /// running window to the pin), otherwise Windows' app id.</summary>
    public string LaunchPath => Id.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && Id.Contains(":\\")
        ? Id // a desktop or taskbar shortcut: open it as it is (it can carry settings of its own); the dock still matches it to its program
        : Exe ?? "shell:AppsFolder\\" + Id;
}

/// <summary>Every app in the Start menu, including Store apps, from the same list Start uses. Cached for a while.</summary>
public static class AppCatalog
{
    static List<AppEntry>? cache;
    static DateTime cachedAt;
    static readonly object gate = new();
    static readonly List<FileSystemWatcher> watchers = new();

    /// <summary>Forget the list as soon as an app is installed or removed (its Start menu entry appears or goes).</summary>
    static void Watch()
    {
        if (watchers.Count > 0) return;
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),        // this user's Start menu
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),  // everyone's Start menu
        })
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                var w = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                FileSystemEventHandler changed = (_, _) => Invalidate();
                w.Created += changed;
                w.Deleted += changed;
                w.Renamed += (s, e) => changed(s, e);
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch { }
        }
    }

    public static void Invalidate()
    {
        lock (gate) cache = null;
    }

    static bool refreshing;

    /// <summary>
    /// Every app, straight away: a list older than a few seconds is returned as it is and refreshed in the background
    /// (Store apps like Spotify from the Microsoft Store don't add Start menu files, so the list can't be kept long).
    /// </summary>
    public static List<AppEntry> All()
    {
        Watch();
        lock (gate)
        {
            if (cache != null)
            {
                if (DateTime.Now - cachedAt > TimeSpan.FromSeconds(20) && !refreshing)
                {
                    refreshing = true;
                    Task.Run(() => { try { Fresh(); } finally { refreshing = false; } });
                }
                return cache;
            }
        }
        return Fresh();
    }

    /// <summary>Read the list now (for pickers that load in the background anyway, so new apps always show).</summary>
    public static List<AppEntry> Fresh()
    {
        Watch();
        // The shell's app list is a COM object that wants a single-threaded apartment
        List<AppEntry> result = new();
        var t = new Thread(() => result = Load()) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(TimeSpan.FromSeconds(20));
        lock (gate) { cache = result; cachedAt = DateTime.Now; }
        return result;
    }

    public static Task<List<AppEntry>> AllAsync() => Task.Run(Fresh);

    static List<AppEntry> Load()
    {
        var list = new List<AppEntry>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(type!)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                if (Regex.IsMatch(name, @"(?i)\b(uninstall|uninstaller|readme|read me|help|website|documentation|release notes|license)\b")) continue;
                string? exe = null;
                try
                {
                    // Desktop apps: the program the Start menu shortcut points at
                    var target = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
                    if (!string.IsNullOrEmpty(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) exe = target;
                }
                catch { }
                if (exe == null && id.Contains(":\\") && id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(id)) exe = id;
                list.Add(new AppEntry(name, id, exe));
            }
        }
        catch (Exception ex) { Log.Error("app catalog", ex); }
        AddOthers(list);
        return list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(a => a.Exe != null).First())
                   .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Apps that aren't in the Start menu but are clearly yours: shortcuts on the desktop, apps pinned to Windows'
    /// taskbar, and anything running with a window (portable apps like DS4Windows, run from a downloads folder).
    /// </summary>
    static void AddOthers(List<AppEntry> list)
    {
        var known = new HashSet<string>(list.Where(a => a.Exe != null).Select(a => a.Exe!), StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(list.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        void Add(string name, string id, string? exe)
        {
            if (exe != null && !known.Add(exe)) return; // the Start menu (or another place) already has it
            if (!names.Add(name)) return;
            list.Add(new AppEntry(name, id, exe));
        }
        try
        {
            string pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned");
            var folders = new[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), SearchOption.TopDirectoryOnly),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), SearchOption.TopDirectoryOnly),
                (Path.Combine(pinned, "TaskBar"), SearchOption.TopDirectoryOnly),
            };
            foreach (var (folder, depth) in folders)
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.*", depth))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".lnk" or ".exe")) continue;
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (Regex.IsMatch(name, @"(?i)\b(uninstall|uninstaller|readme)\b")) continue;
                    var target = ext == ".lnk" ? IconHelper.ResolveShortcut(file) : file;
                    // Programs only (a shortcut to a folder or a document isn't an app)
                    if (target == null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                    Add(name, file, target);
                }
            }
            // Running apps with a window of their own
            foreach (var w in WindowTracker.TaskbarWindows())
            {
                var exe = w.ExePath;
                if (exe == null || !File.Exists(exe) || StoreApps.Find(exe) != null) continue;
                if (exe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) continue;
                if (w.Pid == (uint)Environment.ProcessId) continue;
                // An app's helper that owns its window (Steam's web helper, a crash reporter): not something to open
                if (Regex.IsMatch(Path.GetFileNameWithoutExtension(exe), @"(?i)helper|crashpad|crashhandler|reporter|updater|launcherhost")) continue;
                Add(IconHelper.NiceName(exe), exe, exe);
            }
        }
        catch (Exception ex) { Log.Error("app catalog extras", ex); }
    }

    public static ImageSource? Icon(AppEntry a, int size = 32) =>
        IconHelper.ForShellItem("shell:AppsFolder\\" + a.Id, size) ?? (a.Exe != null ? IconHelper.ForPath(a.Exe) : null);
}
