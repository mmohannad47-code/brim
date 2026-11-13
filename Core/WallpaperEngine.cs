using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Brim.Core;

public sealed record WeItem(string Id, string Title, string Type, string Folder, string? File, string? Preview, string ProjectJson)
{
    /// <summary>Video wallpapers are plain video files (they're still shown through Wallpaper Engine, which plays them better).</summary>
    public bool PlaysInBrim => Type == "video" && File != null && System.IO.File.Exists(File);
}

/// <summary>
/// Finds the wallpapers you have in Wallpaper Engine (Steam Workshop subscriptions and your own projects),
/// and drives Wallpaper Engine through its documented command line for the ones only it can render.
/// </summary>
public static class WallpaperEngine
{
    const string AppId = "431960";

    static IEnumerable<string> SteamLibraries()
    {
        var libs = new List<string>();
        var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (string.IsNullOrEmpty(steam)) return libs;
        libs.Add(Path.GetFullPath(steam));
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (System.IO.File.Exists(vdf))
        {
            foreach (Match m in Regex.Matches(System.IO.File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            {
                try { libs.Add(Path.GetFullPath(m.Groups[1].Value.Replace(@"\\", @"\"))); } catch { }
            }
        }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static string? InstallFolder() =>
        SteamLibraries().Select(l => Path.Combine(l, "steamapps", "common", "wallpaper_engine")).FirstOrDefault(Directory.Exists);

    static string? exeCache;
    static DateTime exeCachedAt;

    /// <summary>Remembered for a while, so play/pause commands go out without re-reading Steam's library list.</summary>
    static string? Exe()
    {
        if (exeCache != null && DateTime.Now - exeCachedAt < TimeSpan.FromMinutes(10) && System.IO.File.Exists(exeCache)) return exeCache;
        exeCache = FindExe();
        exeCachedAt = DateTime.Now;
        return exeCache;
    }

    static string? FindExe()
    {
        var dir = InstallFolder();
        if (dir == null) return null;
        foreach (var name in new[] { "wallpaper64.exe", "wallpaper32.exe" })
        {
            var p = Path.Combine(dir, name);
            if (System.IO.File.Exists(p)) return p;
        }
        return null;
    }

    public static bool Installed => Exe() != null;

    public static List<WeItem> Items()
    {
        var folders = new List<string>();
        foreach (var lib in SteamLibraries())
        {
            var ws = Path.Combine(lib, "steamapps", "workshop", "content", AppId);
            if (Directory.Exists(ws)) folders.AddRange(Directory.GetDirectories(ws));
        }
        var install = InstallFolder();
        if (install != null)
            foreach (var sub in new[] { "myprojects", "defaultprojects" })
            {
                var p = Path.Combine(install, "projects", sub);
                if (Directory.Exists(p)) folders.AddRange(Directory.GetDirectories(p));
            }

        var items = new List<WeItem>();
        foreach (var dir in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pj = Path.Combine(dir, "project.json");
            if (!System.IO.File.Exists(pj)) continue;
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(pj));
                var root = doc.RootElement;
                string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var file = Str("file");
                var preview = Str("preview");
                items.Add(new WeItem(
                    Path.GetFileName(dir),
                    Str("title") is { Length: > 0 } t ? t : Path.GetFileName(dir),
                    Str("type").ToLowerInvariant(),
                    dir,
                    file.Length > 0 ? Path.Combine(dir, file) : null,
                    preview.Length > 0 && System.IO.File.Exists(Path.Combine(dir, preview)) ? Path.Combine(dir, preview) : null,
                    pj));
            }
            catch (Exception ex) { Log.Error("we project " + dir, ex); }
        }
        return items.OrderByDescending(i => i.PlaysInBrim).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool Running
    {
        get
        {
            int n = 0;
            foreach (var name in new[] { "wallpaper64", "wallpaper32" })
            {
                var ps = Process.GetProcessesByName(name);
                n += ps.Length;
                foreach (var p in ps) p.Dispose();
            }
            return n > 0;
        }
    }

    /// <summary>Send a command to the running Wallpaper Engine (play, pause…) without waiting for it.</summary>
    public static void Control(string command)
    {
        var exe = Exe();
        if (exe == null) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "-control " + command)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                });
            }
            catch (Exception ex) { Log.Error("we control", ex); }
        });
    }

    /// <summary>The wallpaper Wallpaper Engine is showing on the first screen (from its config), for matching colours.</summary>
    public static string? CurrentFile()
    {
        try
        {
            var dir = InstallFolder();
            if (dir == null) return null;
            var cfg = Path.Combine(dir, "config.json");
            if (!System.IO.File.Exists(cfg)) return null;
            using var fs = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            foreach (var user in doc.RootElement.EnumerateObject())
            {
                if (user.Value.ValueKind != JsonValueKind.Object) continue;
                if (!user.Value.TryGetProperty("general", out var g) || !g.TryGetProperty("wallpaperconfig", out var wc) ||
                    !wc.TryGetProperty("selectedwallpapers", out var sel) || sel.ValueKind != JsonValueKind.Object) continue;
                foreach (var mon in sel.EnumerateObject())
                    if (mon.Value.TryGetProperty("file", out var f) && f.GetString() is { Length: > 0 } file)
                        return file.Replace('/', '\\');
            }
        }
        catch (Exception ex) { Log.Error("we current", ex); }
        return null;
    }

    /// <summary>The preview picture of the wallpaper Wallpaper Engine is showing (scenes render on the GPU, so this stands in for them).</summary>
    public static string? CurrentPreview()
    {
        var file = CurrentFile();
        if (file == null) return null;
        var folder = Path.GetDirectoryName(file);
        if (folder == null || !Directory.Exists(folder)) return null;
        try
        {
            var pj = Path.Combine(folder, "project.json");
            if (System.IO.File.Exists(pj))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(pj));
                if (doc.RootElement.TryGetProperty("preview", out var p) && p.GetString() is { Length: > 0 } preview &&
                    System.IO.File.Exists(Path.Combine(folder, preview)))
                    return Path.Combine(folder, preview);
            }
        }
        catch { }
        foreach (var name in new[] { "preview.jpg", "preview.png", "preview.gif" })
            if (System.IO.File.Exists(Path.Combine(folder, name))) return Path.Combine(folder, name);
        return null;
    }

    /// <summary>Show a scene/web wallpaper through Wallpaper Engine (starts it if needed).</summary>
    public static bool Open(WeItem item) => Run($"-control openWallpaper -file \"{item.ProjectJson}\"");

    /// <summary>
    /// Take Wallpaper Engine's wallpaper down so Brim's can show. Wallpaper Engine puts back the Windows
    /// wallpaper it found when it started as it closes, so this waits until it's done; otherwise that would
    /// land on top of the picture Brim just set.
    /// </summary>
    public static async System.Threading.Tasks.Task CloseIfRunningAsync()
    {
        if (!Running) return;
        var exe = Exe();
        if (exe == null) return;
        await System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "-control closeWallpaper")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                });
                p?.WaitForExit(5000);
            }
            catch (Exception ex) { Log.Error("we close", ex); }
        });
        await System.Threading.Tasks.Task.Delay(900);
    }

    /// <summary>
    /// Is Wallpaper Engine drawing a wallpaper right now? Its picture lives in a WPEDesktop… window inside the
    /// desktop's WorkerW (a child of Progman on Windows 11 24H2, a top-level window before that). Cheap: no process scan.
    /// </summary>
    public static bool ShowingWallpaper()
    {
        bool found = false;
        void Check(IntPtr parent)
        {
            for (var c = Native.FindWindowEx(parent, IntPtr.Zero, null, null); c != IntPtr.Zero && !found; c = Native.FindWindowEx(parent, c, null, null))
                if (Native.ClassOf(c).StartsWith("WPEDesktop", StringComparison.Ordinal) && Native.IsWindowVisible(c)) found = true;
        }
        var progman = Native.FindWindow("Progman", null);
        for (var w = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null); w != IntPtr.Zero && !found; w = Native.FindWindowEx(progman, w, "WorkerW", null))
            Check(w);
        for (var w = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "WorkerW", null); w != IntPtr.Zero && !found; w = Native.FindWindowEx(IntPtr.Zero, w, "WorkerW", null))
            Check(w);
        return found;
    }

    static string NormFile(string? f) => (f ?? "").Replace('/', '\\').Trim().ToLowerInvariant();

    /// <summary>
    /// Brim is taking the wallpaper over. Wallpaper Engine keeps its wallpaper in its own settings and puts it back
    /// whenever the desktop changes (which starting Brim's live wallpaper does), or when it restarts with Windows; so
    /// remember which one it was, and <see cref="Guard"/> closes it again whenever it comes back.
    /// </summary>
    public static async System.Threading.Tasks.Task TakeOverAsync()
    {
        var s = SettingsStore.Current;
        s.WallpaperOwner = "Brim";
        if (CurrentFile() is { } file) s.WeSuppressedFile = file;
        SettingsStore.Save();
        await CloseIfRunningAsync();
    }

    /// <summary>You picked a Wallpaper Engine wallpaper through Brim: it's in charge again.</summary>
    public static void HandBack()
    {
        var s = SettingsStore.Current;
        s.WallpaperOwner = "WallpaperEngine";
        s.WeSuppressedFile = null;
        SettingsStore.Save();
    }

    static bool closing;
    static DateTime lastGuard;

    /// <summary>
    /// Called often (twice a second, and whenever windows change). While Brim owns the wallpaper and Wallpaper Engine
    /// shows one anyway: the one Brim took over came back → close it again; a different one → you picked it in
    /// Wallpaper Engine yourself, so Brim steps aside (returns true so the caller stops Brim's live wallpaper).
    /// </summary>
    public static bool Guard()
    {
        var s = SettingsStore.Current;
        if (s.WallpaperOwner != "Brim" || closing || DateTime.Now - lastGuard < TimeSpan.FromSeconds(1)) return false;
        lastGuard = DateTime.Now;
        if (!ShowingWallpaper()) return false;
        var current = CurrentFile();
        if (current != null && s.WeSuppressedFile != null && NormFile(current) != NormFile(s.WeSuppressedFile))
        {
            Log.Note("wallpaper", $"Wallpaper Engine shows a different wallpaper ({System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(current))}): you picked it there, so Brim steps aside");
            HandBack();
            return true;
        }
        Log.Note("wallpaper", "Wallpaper Engine brought its wallpaper back over Brim's: closing it again");
        closing = true;
        _ = CloseAgain();
        return false;
    }

    static async System.Threading.Tasks.Task CloseAgain()
    {
        try
        {
            await CloseIfRunningAsync();
            // Closing can put back the picture Wallpaper Engine found when it started: put Brim's back
            var s = SettingsStore.Current;
            if (s.LiveWallpaper == null && s.BrimPicture != null && System.IO.File.Exists(s.BrimPicture) &&
                !string.Equals(Wallpapers.Current(), s.BrimPicture, StringComparison.OrdinalIgnoreCase))
                Wallpapers.ApplyPicture(s.BrimPicture);
        }
        finally { closing = false; lastGuard = DateTime.Now; }
    }

    /// <summary>Folders to watch so the list updates when you subscribe to or remove wallpapers.</summary>
    public static IEnumerable<string> WatchFolders()
    {
        foreach (var lib in SteamLibraries())
        {
            var ws = Path.Combine(lib, "steamapps", "workshop", "content", AppId);
            if (Directory.Exists(ws)) yield return ws;
        }
        var install = InstallFolder();
        if (install != null)
        {
            var mine = Path.Combine(install, "projects", "myprojects");
            if (Directory.Exists(mine)) yield return mine;
        }
    }

    static bool Run(string args)
    {
        var exe = Exe();
        if (exe == null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return true;
        }
        catch (Exception ex) { Log.Error("wallpaper engine", ex); return false; }
    }

    public const string WorkshopVideos = "steam://openurl/https://steamcommunity.com/workshop/browse/?appid=431960&requiredtags%5B%5D=Video";
}
