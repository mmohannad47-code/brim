using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Brim.Core;

/// <summary>What Brim says about a file or folder: whether it can go, and why.</summary>
public sealed record Advice(Safety Level, string Text, InstalledProgram? Program = null);

/// <summary>
/// Plain advice for what's taking up space, the way storage cleaners explain it: Windows' own files (leave them),
/// installed programs (uninstall rather than delete), caches and build output (they come back by themselves), and
/// your own big or old files (your call).
/// </summary>
public static class StorageAdvice
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string Downloads = Path.Combine(Home, "Downloads");
    static readonly string? OneDrive = Environment.GetEnvironmentVariable("OneDrive");

    static readonly HashSet<string> CacheNames = new(StringComparer.OrdinalIgnoreCase)
    { "cache", "caches", "code cache", "gpucache", "dxcache", "glcache", "shadercache", "grshadercache", "cache2", "__pycache__", ".cache", "temp", "tmp", "htmlcache", "webcache", "crashdumps", "cacheddata" };

    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".m4v", ".webm", ".ts", ".flv" };
    static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".img" };
    static readonly HashSet<string> Setups = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".iso" };

    public static Advice? For(string path, bool dir, long size, DateTime modified, IReadOnlyList<InstalledProgram>? programs)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        var parent = Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
        bool atRoot = parent.Length <= 3;
        var lower = path.ToLowerInvariant();

        // ---- Windows itself
        if (atRoot)
            switch (name.ToLowerInvariant())
            {
                case "windows": return new(Safety.Keep, "Windows itself. Don't delete anything here: Clean up's \"Windows system files\" removes the parts that can go.");
                case "program files" or "program files (x86)": return new(Safety.Keep, "Installed programs. Remove them with Uninstall, not by deleting their folders.");
                case "programdata": return new(Safety.Keep, "Data that apps share between accounts. Uninstall the app it belongs to instead.");
                case "system volume information": return new(Safety.Keep, "Restore points. Choose how much space they get in Windows' System Protection settings.");
                case "$recycle.bin": return new(Safety.Review, "The Recycle Bin. Empty it from Clean up once you've checked it.");
                case "recovery" or "$winreagent" or "boot" or "efi" or "$sysreset": return new(Safety.Keep, "Used by Windows to start or repair itself.");
                case "windows.old" or "$windows.~bt" or "$windows.~ws": return new(Safety.Review, "Your previous Windows. \"Clean system files\" removes it (then you can't go back to that version).");
                case "pagefile.sys" or "swapfile.sys": return new(Safety.Keep, "Windows' extra memory on disk. Windows sizes it itself.");
                case "hiberfil.sys": return new(Safety.Review, "Used for hibernate and fast start-up. Turning hibernation off frees it (powercfg /h off, as administrator).");
                case "users": return null;
            }
        if (lower.Contains(@"\windows\winsxs")) return new(Safety.Keep, "Windows' components. Looks bigger than it is, since most files are shared with the rest of Windows.");
        if (lower.Contains(@":\windows\")) return new(Safety.Keep, "Part of Windows. Leave it to Windows (or Clean up's system files).");
        // (whole folder names: "Microsoft" but not "Microsoft VS Code")
        bool Under(string folder) => lower.EndsWith(folder) || lower.Contains(folder + "\\");
        if (Under(@":\programdata\microsoft")) return new(Safety.Keep, "Windows' shared data (Defender, the Start menu, Windows Update…). Leave it.");
        if (Under(@":\programdata\package cache")) return new(Safety.Keep, "Installers Windows keeps so programs can be repaired or uninstalled. Without them, some programs can't be removed.");
        if (Under(@"\appdata\local\packages")) return new(Safety.Keep, "Microsoft Store apps' data. Remove the app with Uninstall instead.");
        if (Under(@"\appdata\local\microsoft") || Under(@"\appdata\roaming\microsoft"))
            return new(Safety.Keep, "Windows' and Microsoft apps' data for your account (Start menu, sign-ins, Outlook, credentials…). Clean up handles the parts that can go.");
        if (lower.EndsWith(@"\appdata\local\temp") || lower.Contains(@"\appdata\local\temp\.net")) return new(Safety.Review, "Temporary files. Clean up removes the ones that can go, safely.");

        // ---- Installed programs: uninstall rather than delete
        if (programs != null)
        {
            // (Some installers write their folder with forward slashes: C:/Riot Games/…)
            var owner = programs.Select(p => (p, loc: p.Location?.Replace('/', '\\').TrimEnd('\\')))
                                .Where(x => x.loc is { Length: > 3 } loc &&
                                            (string.Equals(loc, path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || path.StartsWith(loc + "\\", StringComparison.OrdinalIgnoreCase)))
                                .OrderByDescending(x => x.loc!.Length).Select(x => x.p).FirstOrDefault();
            if (owner != null) return new(Safety.Review, $"Part of {owner.Name}. If you don't use it, uninstall it rather than deleting files.", owner);
        }
        // A file inside a cache (a shader cache, a browser's cache…): made again when it's needed
        if (!dir && Path.GetDirectoryName(path) is { } folderPath &&
            folderPath.Split('\\').Any(seg => CacheNames.Contains(seg) || seg.Equals("D3DSCache", StringComparison.OrdinalIgnoreCase)))
            return new(Safety.Safe, "Part of a cache. The app that made it makes it again (Clean up removes these).");
        if (lower.Contains(@"\steamapps\common\") || lower.Contains(@"\epic games\") && dir) return new(Safety.Review, "A game. Uninstall it from its launcher (or Brim's Uninstall) to free this.");
        if (lower.Contains(@":\program files")) return new(Safety.Keep, "Part of an installed program. Uninstall the program instead.");

        // ---- Things that come back by themselves
        if (dir)
        {
            switch (name.ToLowerInvariant())
            {
                case "node_modules": return new(Safety.Safe, "Packages for a JavaScript project. npm install brings them back.");
                case ".git": return new(Safety.Keep, "A project's history. Deleting it loses every past version.");
                case ".nuget" or ".gradle" or "npm-cache" or ".m2" or ".cargo" or "go-build" or ".rustup": return new(Safety.Review, "Downloaded developer packages. They come back on your next build (which takes longer once).");
                case "appdata": return new(Safety.Review, "Apps' settings and caches. Clean up handles the usual caches; look inside for big ones.");
                case "obj" or "bin" when HasSibling(parent, "*.csproj", "*.sln", "*.vcxproj"): return new(Safety.Safe, "Build output. Building the project makes it again.");
                case "target" when HasSibling(parent, "Cargo.toml", "pom.xml"): return new(Safety.Safe, "Build output. Building the project makes it again.");
                case "dist" or "build" or ".next" when HasSibling(parent, "package.json"): return new(Safety.Safe, "Build output. Building the project makes it again.");
                case ".venv" or "venv" or "env" when HasSibling(Path.Combine(path, "Scripts"), "python.exe"): return new(Safety.Safe, "A Python environment. It can be made again from the project's requirements.");
            }
            if (CacheNames.Contains(name)) return new(Safety.Safe, "A cache or temporary folder. The app that made it makes it again.");
            if (string.Equals(path, Downloads, StringComparison.OrdinalIgnoreCase)) return new(Safety.Review, "Downloads pile up. Sort by size or date and remove what you've already used.");
            if (OneDrive != null && string.Equals(path, OneDrive, StringComparison.OrdinalIgnoreCase))
                return new(Safety.Review, "Synced with OneDrive. Right-click a folder in Explorer › Free up space to keep it online only.");
        }
        else
        {
            var ext = Path.GetExtension(name);
            if (ext.Equals(".vhdx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".vhd", StringComparison.OrdinalIgnoreCase))
                return new(Safety.Keep, "A virtual disk (WSL, Docker or a virtual machine). Clean up inside it rather than deleting it.");
            if (ext.Equals(".dmp", StringComparison.OrdinalIgnoreCase)) return new(Safety.Safe, "A crash dump. Nothing on your PC uses it.");
            if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) && size > 50L << 20) return new(Safety.Safe, "A log file. Usually safe to delete.");
            if (Setups.Contains(ext) && path.StartsWith(Downloads + "\\", StringComparison.OrdinalIgnoreCase))
                return new(Safety.Review, "An installer you downloaded. Once the app is installed you can delete it (and download it again if you ever need it).");
            if (Path.GetExtension(name) is ".crdownload" or ".part" or ".partial") return new(Safety.Safe, "An unfinished download.");
            if (Videos.Contains(ext) && size > 500L << 20) return new(Safety.Review, "A big video. Move it to an external drive or the cloud if you rarely watch it.");
            if (Archives.Contains(ext) && size > 500L << 20) return new(Safety.Review, "An archive or disk image. Often not needed once it's unpacked or installed.");
        }

        // ---- Your own big, old things
        if (size > 1L << 30 && modified > DateTime.MinValue && (DateTime.Now - modified).TotalDays > 365)
            return new(Safety.Review, $"Nothing in it has changed for {Ago(modified)}. Worth a look.");
        return null;
    }

    static string Ago(DateTime t)
    {
        var days = (DateTime.Now - t).TotalDays;
        return days >= 730 ? $"{(int)(days / 365)} years" : days >= 365 ? "over a year" : $"{(int)(days / 30)} months";
    }

    static bool HasSibling(string dir, params string[] patterns)
    {
        try { return patterns.Any(p => Directory.EnumerateFiles(dir, p).Any()); }
        catch { return false; }
    }
}

/// <summary>A folder an uninstalled app left behind.</summary>
public sealed record Leftover(string Path, long Size, string Where);

/// <summary>
/// After an app is uninstalled: the folders it left in AppData, ProgramData, Documents and Program Files, found by
/// its name (the way Revo and IObit look for leftovers). Never a folder another installed app uses, never a shared
/// folder, and only names distinctive enough not to be someone else's.
/// </summary>
public static class Leftovers
{
    static string Squeeze(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    /// <summary>The program's name without version, edition or bitness ("Foo Bar 2.3 (x64)" → "foobar").</summary>
    static IEnumerable<string> Names(InstalledProgram p)
    {
        var n = Regex.Replace(p.Name, @"\(.*?\)|\bv?\d+(\.\d+)+\b|\b(x64|x86|64-bit|32-bit)\b", " ");
        var names = new List<string> { Squeeze(n), Squeeze(p.Name) };
        var dash = n.Split(new[] { " - ", " – ", ":" }, StringSplitOptions.None)[0];
        names.Add(Squeeze(dash));
        // "Microsoft OneDrive" by Microsoft is also just "OneDrive"
        var firstWord = p.Publisher.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstWord is { Length: >= 3 } && n.TrimStart().StartsWith(firstWord + " ", StringComparison.OrdinalIgnoreCase))
            names.Add(Squeeze(n.TrimStart()[(firstWord.Length + 1)..]));
        if (p.Location is { Length: > 3 } && p.Kind != "StoreApp") names.Add(Squeeze(Path.GetFileName(p.Location.Replace('/', '\\').TrimEnd('\\'))));
        return names.Where(x => x.Length >= 4).Distinct();
    }

    public static List<Leftover> Find(InstalledProgram removed, IReadOnlyList<InstalledProgram> stillInstalled)
    {
        var names = Names(removed).ToHashSet();
        var result = new List<Leftover>();
        if (names.Count == 0) return result;
        // Names of everything that's still installed: a folder that matches one of those isn't a leftover
        var taken = stillInstalled.Where(p => p.Key != removed.Key).SelectMany(Names).ToHashSet();
        names.ExceptWith(taken);
        if (names.Count == 0) return result;
        var publisher = Squeeze(Regex.Replace(removed.Publisher, @"(?i)\b(inc|ltd|llc|gmbh|corp|corporation|co|limited)\b\.?", ""));

        string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new (string dir, string where)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppData (Roaming)"),
            (Local, "AppData (Local)"),
            (Path.Combine(Local, "Programs"), "AppData (Local)"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow"), "AppData (LocalLow)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"), "Documents › My Games"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)"),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Consider(string dir, string where)
        {
            dir = dir.Replace('/', '\\').TrimEnd('\\');
            if (!seen.Add(dir) || !Directory.Exists(dir)) return;
            // Store apps' folders belong to Windows (removing the app removes them)
            if (dir.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)) return;
            if (stillInstalled.Any(p => p.Key != removed.Key && p.Location?.Replace('/', '\\').TrimEnd('\\') is { Length: > 3 } loc &&
                                        (loc.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase) || string.Equals(loc, dir, StringComparison.OrdinalIgnoreCase))))
                return;
            if (!Uninstaller.SafeToRecycle(dir, new List<string>())) return;
            result.Add(new Leftover(dir, FolderSize(dir), where));
        }
        foreach (var (dir, where) in roots)
        {
            if (!Directory.Exists(dir)) continue;
            string[] kids;
            try { kids = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var k in kids)
            {
                var s = Squeeze(Path.GetFileName(k));
                if (names.Contains(s)) Consider(k, where);
                // Publisher\Product (e.g. AppData\Roaming\Ubisoft\Some Game)
                else if (publisher.Length >= 3 && s == publisher)
                {
                    try
                    {
                        foreach (var inner in Directory.GetDirectories(k))
                            if (names.Contains(Squeeze(Path.GetFileName(inner)))) Consider(inner, where + " › " + Path.GetFileName(k));
                    }
                    catch { }
                }
            }
        }
        // Its own install folder, if the uninstaller left it behind
        if (removed.Kind == "Program" && removed.Location is { Length: > 3 } own) Consider(own, "its install folder");
        return result.OrderByDescending(r => r.Size).ToList();
    }

    static long FolderSize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                                         .Sum(f => f.Length);
        }
        catch { return 0; }
    }
}
