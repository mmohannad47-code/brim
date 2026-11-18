using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace Brim.Core;

/// <summary>How sure Brim is that something can go.</summary>
public enum Safety { Safe, Review, Keep }

/// <summary>Something Brim can clean, with what it is, how much it frees, and whether to tick it for you.</summary>
public sealed class CleanItem
{
    public string Id = "", Title = "", Advice = "";
    public Safety Safety;
    /// <summary>Needs administrator rights (Windows' own folders): cleaned by an elevated Brim, one prompt for all.</summary>
    public bool Admin;
    /// <summary>Your own files (old installers): they go to the Recycle Bin, so you can change your mind.</summary>
    public bool Recycle;
    /// <summary>-1 while measuring; -2 when Brim can't see inside without admin.</summary>
    public long Size = -1;
    public int Count;
    public bool Picked;
    public string? Note;
    /// <summary>Folders whose contents go (the folder itself stays), or single files.</summary>
    internal Func<IEnumerable<string>> Targets = () => Array.Empty<string>();
    /// <summary>Only files older than this (temp files still in use are usually new).</summary>
    internal TimeSpan MinAge = TimeSpan.Zero;
    internal Func<string, bool>? Only;
    internal Func<long>? Measure;
    internal Func<(long freed, string? note)>? Clean;
    /// <summary>A reason not to clean it right now ("Close Chrome to clean this"), checked again just before cleaning.</summary>
    internal Func<string?>? Blocked;
    /// <summary>The files themselves, for items that are your own files (so you see what would go).</summary>
    public List<string>? Names;
    public string? BlockedNow => Blocked?.Invoke();
}

/// <summary>
/// Junk Windows and apps leave behind, measured and cleaned the way CCleaner, BleachBit and Windows' own Disk
/// Cleanup do: temp files, caches that refill by themselves, crash dumps, old installers, the Recycle Bin. Things you
/// made never go without asking, and anything that isn't just a cache goes to the Recycle Bin.
/// </summary>
public static class Cleanup
{
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static string SysDrive => Path.GetPathRoot(Win) ?? @"C:\";

    static readonly string[] Installers = { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".iso" };

    /// <summary>Everything Brim knows how to clean on this PC (only what's actually here).</summary>
    public static List<CleanItem> Items()
    {
        var list = new List<CleanItem>
        {
            new()
            {
                Id = "temp", Title = "Temporary files", Safety = Safety.Safe, MinAge = TimeSpan.FromDays(7),
                Advice = "Left behind by installers and apps. Only files untouched for a week go (as with Windows' Disk Cleanup), and files in use are skipped.",
                Targets = () => new[] { Path.GetTempPath() },
                // Unpacked apps are handled whole (below): taking single files out of one would break it
                Only = f => !f.StartsWith(NetDir + "\\", StringComparison.OrdinalIgnoreCase),
            },
            new()
            {
                Id = "unpacked", Title = "Old unpacked app copies", Safety = Safety.Safe,
                Advice = "Apps that come as one file (Brim too) unpack themselves here to run, and every update leaves the old copy behind. Copies in use are kept; an app unpacks itself again if it needs to.",
                Measure = () => OldUnpacked(null).Sum(FolderSize),
                Clean = () =>
                {
                    long freed = 0; int kept = 0;
                    foreach (var d in OldUnpacked(null).ToList())
                    {
                        long s = FolderSize(d);
                        if (RemoveWhole(d)) freed += s; else kept++;
                    }
                    return (freed, kept > 0 ? $"{kept} in use were kept" : null);
                },
            },
            new()
            {
                Id = "recycle", Title = "Recycle Bin", Safety = Safety.Review,
                Advice = "Files you deleted. Check there's nothing you still want: emptying it can't be undone.",
                Measure = () => RecycleBinSize(), Clean = () => { long s = RecycleBinSize(); EmptyRecycleBin(); return (s, null); },
            },
        };

        // Browsers: only the cache (pages and pictures they saved to load faster). History, passwords, logins stay.
        foreach (var (name, data) in new[]
                 {
                     ("Chrome", Path.Combine(Local, @"Google\Chrome\User Data")), ("Edge", Path.Combine(Local, @"Microsoft\Edge\User Data")),
                     ("Brave", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data")), ("Vivaldi", Path.Combine(Local, @"Vivaldi\User Data")),
                     ("Opera", Path.Combine(Local, @"Opera Software\Opera Stable")), ("Opera GX", Path.Combine(Local, @"Opera Software\Opera GX Stable")),
                 })
        {
            if (!Directory.Exists(data)) continue;
            var d = data;
            list.Add(new CleanItem
            {
                Id = "browser:" + name, Title = name + " cache", Safety = Safety.Safe,
                Advice = $"Saved copies of web pages and pictures. History, passwords, logins and sites' offline data stay; pages load a little slower the first time.",
                Targets = () => ChromiumCaches(d),
                Blocked = WhileRunning(name, BrowserProcess(name)),
            });
        }
        var firefox = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(firefox))
            list.Add(new CleanItem
            {
                Id = "browser:Firefox", Title = "Firefox cache", Safety = Safety.Safe,
                Advice = "Saved copies of web pages and pictures. History, passwords and logins stay.",
                Targets = () => SafeDirs(firefox).SelectMany(p => new[] { Path.Combine(p, "cache2"), Path.Combine(p, "startupCache"), Path.Combine(p, "thumbnails") }),
                Blocked = WhileRunning("Firefox", "firefox"),
            });

        // Apps built on a browser keep the same kind of cache
        // (Only the folders each app clears itself when you ask it to, or that its makers say can go)
        foreach (var (name, short_, dir, parts, process) in new (string, string, string, string[], string)[]
                 {
                     ("Discord", "Discord", Path.Combine(Roaming, "discord"), new[] { "Cache", "Code Cache", "GPUCache" }, "Discord"),
                     ("Spotify", "Spotify", Path.Combine(Local, "Spotify"), new[] { "Data", "Browser\\Cache" }, "Spotify"),
                     ("Microsoft Teams (classic)", "Teams", Path.Combine(Roaming, @"Microsoft\Teams"), new[] { "Cache", "Code Cache", "GPUCache" }, "Teams"),
                     ("Slack", "Slack", Path.Combine(Roaming, "Slack"), new[] { "Cache", "Code Cache", "GPUCache" }, "slack"),
                     ("Visual Studio Code", "VS Code", Path.Combine(Roaming, "Code"), new[] { "Cache", "CachedData", "Code Cache", "GPUCache", "CachedExtensionVSIXs" }, "Code"),
                     ("Steam", "Steam", Path.Combine(Local, "Steam"), new[] { "htmlcache" }, "steam"),
                     ("Epic Games Launcher", "the Epic Games Launcher", Path.Combine(Local, @"EpicGamesLauncher\Saved"), new[] { "webcache", "webcache_4147", "webcache_4430" }, "EpicGamesLauncher"),
                 })
        {
            if (!Directory.Exists(dir)) continue;
            var (d, p) = (dir, parts);
            list.Add(new CleanItem
            {
                Id = "app:" + name, Title = name + " cache", Safety = Safety.Safe,
                Advice = $"{name} downloads these again when it needs them. Your messages, songs, downloads and settings aren't touched.",
                Targets = () => p.Select(x => Path.Combine(d, x)),
                Blocked = WhileRunning(short_, process),
            });
        }

        var shaders = new[]
        {
            Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"), Path.Combine(Local, @"NVIDIA\OptixCache"),
            Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"), Path.Combine(Local, @"AMD\VkCache"), Path.Combine(Local, @"AMD\GLCache"),
            Path.Combine(Local, "D3DSCache"), Path.Combine(Local, @"Intel\ShaderCache"),
        };
        if (shaders.Any(Directory.Exists))
            list.Add(new CleanItem
            {
                Id = "shaders", Title = "Graphics shader caches", Safety = Safety.Review,
                Advice = "Games and your graphics driver build these again. The first few minutes of a game can stutter while they do, so only worth it when they're big.",
                Targets = () => shaders,
            });

        list.Add(new CleanItem
        {
            Id = "crash", Title = "Crash dumps and error reports", Safety = Safety.Safe,
            Advice = "Saved when an app crashed, for its makers to look at. Nothing on your PC uses them.",
            Targets = () => new[]
            {
                Path.Combine(Local, "CrashDumps"), Path.Combine(Local, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Combine(Local, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(Local, @"Microsoft\Windows\WER\Temp"),
            },
        });

        var downloads = Path.Combine(Home, "Downloads");
        if (Directory.Exists(downloads))
            list.Add(new CleanItem
            {
                Id = "installers", Title = "Old installers in Downloads", Safety = Safety.Review, Recycle = true, MinAge = TimeSpan.FromDays(30),
                Advice = "Setup files and disk images you downloaded over a month ago. Check the list: a program you run straight from Downloads (one with no installer) would be in it too. They go to the Recycle Bin.",
                Targets = () => SafeFiles(downloads).Where(f => Installers.Contains(Path.GetExtension(f).ToLowerInvariant())),
            });
        if (Directory.Exists(downloads))
            list.Add(new CleanItem
            {
                Id = "partial", Title = "Unfinished downloads", Safety = Safety.Review, Recycle = true, MinAge = TimeSpan.FromDays(7),
                Advice = "Downloads that stopped part-way over a week ago. One you paused on purpose (JDownloader, Free Download Manager and the like) would have to start again. They go to the Recycle Bin.",
                Targets = () => SafeFiles(downloads).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".crdownload" or ".part" or ".partial" or ".download" or ".opdownload"),
            });

        var dev = new[]
        {
            Path.Combine(Local, "npm-cache"), Path.Combine(Local, @"pip\cache"), Path.Combine(Local, @"Yarn\Cache"), Path.Combine(Local, @"pnpm\store"),
            Path.Combine(Home, @".nuget\packages"), Path.Combine(Home, @".gradle\caches"), Path.Combine(Home, @".cargo\registry\cache"), Path.Combine(Local, @"go-build"),
        };
        if (dev.Any(Directory.Exists))
            list.Add(new CleanItem
            {
                Id = "dev", Title = "Developer package caches", Safety = Safety.Review,
                Advice = "Packages downloaded by npm, pip, NuGet, Gradle and the like. Your projects download them again on their next build, which takes longer once.",
                Targets = () => dev,
            });

        // ---- Windows' own folders: one administrator prompt cleans all of these
        list.Add(new CleanItem
        {
            Id = "sys:update", Title = "Windows Update downloads", Safety = Safety.Safe, Admin = true,
            Advice = "Updates Windows has already installed. Windows downloads them again if it ever needs one. Never while an update is waiting for a restart.",
            Targets = () => new[] { Path.Combine(Win, @"SoftwareDistribution\Download") },
            Blocked = () => UpdatePending() ? "Windows is waiting to restart for an update: restart first, then clean this" : null,
        });
        list.Add(new CleanItem
        {
            Id = "sys:temp", Title = "Windows temporary files", Safety = Safety.Safe, Admin = true, MinAge = TimeSpan.FromDays(7),
            Advice = "Temporary files of Windows itself and its services, untouched for a week.",
            Targets = () => new[] { Path.Combine(Win, "Temp") },
        });
        list.Add(new CleanItem
        {
            Id = "sys:delivery", Title = "Delivery Optimization files", Safety = Safety.Safe, Admin = true,
            Advice = "Pieces of updates Windows keeps to share with other PCs. Removed with Windows' own command for it.",
            Targets = () => new[] { Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") },
        });
        list.Add(new CleanItem
        {
            Id = "sys:dumps", Title = "System crash dumps", Safety = Safety.Safe, Admin = true,
            Advice = "Memory dumps from blue screens and driver hiccups, and Windows' error reports. Can be several GB.",
            Targets = () => new[]
            {
                Path.Combine(Win, "LiveKernelReports"), Path.Combine(Win, "Minidump"), Path.Combine(Win, "MEMORY.DMP"),
                @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive", @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue", @"C:\ProgramData\Microsoft\Windows\WER\Temp",
            },
        });
        // Only Windows.old: $Windows.~BT and ~WS can belong to an upgrade that is still going on
        if (Directory.Exists(Path.Combine(SysDrive, "Windows.old")))
            list.Add(new CleanItem
            {
                Id = "sys:old", Title = "Previous Windows installation", Safety = Safety.Review, Admin = true,
                Advice = "Kept so you can go back to the Windows you had before the last big update. Once removed, going back isn't possible. Often 10–30 GB.",
                Targets = () => new[] { Path.Combine(SysDrive, "Windows.old") },
            });
        return list;
    }

    // ---------------------------------------------------------------- measuring

    /// <summary>Fill in each item's size (off the UI thread). Admin items Brim can't see into show as unknown.</summary>
    public static Task MeasureAll(IEnumerable<CleanItem> items, Action<CleanItem> measured) => Task.Run(() =>
    {
        foreach (var i in items)
        {
            try
            {
                if (i.Measure != null) { i.Size = i.Measure(); i.Count = 0; }
                else
                {
                    long size = 0; int count = 0; bool blind = false;
                    var files = Files(i, ref blind).ToList();
                    foreach (var f in files) { size += f.Length; count++; }
                    i.Size = size == 0 && blind ? -2 : size;
                    i.Count = count;
                    if (i.Recycle) i.Names = files.OrderByDescending(f => f.Length).Select(f => f.Name).ToList();
                }
            }
            catch { i.Size = -2; }
            // Tick what's safe and worth it; Windows' own folders Brim can't see into are ticked too (they're cleaned
            // only when you press "Clean system files")
            if (i.Safety == Safety.Safe && (i.Size > 0 || i.Admin && i.Size == -2) && i.BlockedNow == null) i.Picked = true;
            measured(i);
        }
    });

    /// <summary>The files an item would remove (respecting its age and type rules).</summary>
    static IEnumerable<FileInfo> Files(CleanItem item, ref bool blind)
    {
        var result = new List<FileInfo>();
        var cutoff = DateTime.Now - item.MinAge;
        foreach (var t in item.Targets())
        {
            try
            {
                if (File.Exists(t)) { var fi = new FileInfo(t); if (fi.LastWriteTime <= cutoff) result.Add(fi); continue; }
                if (!Directory.Exists(t)) continue;
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                bool any = false;
                foreach (var fi in new DirectoryInfo(t).EnumerateFiles("*", opts))
                {
                    any = true;
                    if (fi.LastWriteTime <= cutoff && (item.Only?.Invoke(fi.FullName) ?? true)) result.Add(fi);
                }
                if (!any && item.Admin) blind = true;
            }
            catch (UnauthorizedAccessException) { blind = true; }
            catch { }
        }
        return result;
    }

    // ---------------------------------------------------------------- cleaning

    /// <summary>Clean the picked items Brim can clean itself (not the admin ones). Returns what was freed.</summary>
    public static Task<long> Clean(IEnumerable<CleanItem> items, Action<CleanItem> done) => Task.Run(() =>
    {
        long total = 0;
        foreach (var i in items.Where(i => i.Picked && !i.Admin))
        {
            // Checked again now: the app may have been opened since
            if (i.BlockedNow is { } why) { i.Note = why; Log.Note("cleanup", $"{i.Title}: skipped ({why})"); done(i); continue; }
            var (freed, note) = CleanOne(i);
            total += freed;
            i.Note = note;
            Log.Note("cleanup", $"{i.Title}: {StorageScan.Size(freed)}{(note != null ? " (" + note + ")" : "")}");
            done(i);
        }
        return total;
    });

    static (long freed, string? note) CleanOne(CleanItem i)
    {
        if (i.Clean != null) return i.Clean();
        bool blind = false;
        var files = Files(i, ref blind).ToList();
        if (i.Recycle)
        {
            long size = files.Sum(f => f.Length);
            return RecycleBin.Send(files.Select(f => f.FullName)) ? (size, "moved to the Recycle Bin") : (0, "not moved");
        }
        long freed = 0; int busy = 0;
        foreach (var f in files)
        {
            try
            {
                long len = f.Length;
                if (f.IsReadOnly) f.IsReadOnly = false;
                f.Delete();
                freed += len;
            }
            catch { busy++; }
        }
        // Then the folders that are now empty (never the target folder itself, and not ones made recently: an app
        // may have just made one to put something in)
        var oldEnough = DateTime.Now - (i.MinAge > TimeSpan.FromDays(1) ? i.MinAge : TimeSpan.FromDays(1));
        foreach (var t in i.Targets().Where(Directory.Exists))
            try
            {
                foreach (var d in Directory.EnumerateDirectories(t, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                                           .OrderByDescending(d => d.Length))
                    try { if (Directory.GetLastWriteTime(d) < oldEnough && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
            catch { }
        return (freed, busy > 0 ? $"{busy} file{(busy == 1 ? "" : "s")} in use were kept" : null);
    }

    /// <summary>
    /// Clean Windows' own folders: Brim starts itself as administrator for this (Windows asks once), cleans the
    /// picked ones and reports back through a small file.
    /// </summary>
    public static async Task<Dictionary<string, (long freed, string? note)>?> CleanSystem(IEnumerable<CleanItem> items)
    {
        var ids = items.Where(i => i.Picked && i.Admin).Select(i => i.Id).ToList();
        if (ids.Count == 0) return new();
        var result = Path.Combine(Path.GetTempPath(), $"brim-clean-{Guid.NewGuid():N}.json");
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--clean-system \"{result}\" {string.Join(" ", ids.Select(i => "\"" + i + "\""))}")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            if (p == null) return null;
            await p.WaitForExitAsync();
            if (!File.Exists(result)) return null;
            var map = JsonSerializer.Deserialize<Dictionary<string, Outcome>>(await File.ReadAllTextAsync(result)) ?? new();
            return map.ToDictionary(kv => kv.Key, kv => (kv.Value.Freed, kv.Value.Note));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return null; } // you said no
        finally { try { File.Delete(result); } catch { } }
    }

    public sealed class Outcome { public long Freed { get; set; } public string? Note { get; set; } }

    /// <summary>In the elevated Brim (--clean-system): clean these and write what happened.</summary>
    public static void RunElevated(string resultFile, IEnumerable<string> ids)
    {
        var all = Items().ToDictionary(i => i.Id);
        var outcome = new Dictionary<string, Outcome>();
        foreach (var id in ids)
        {
            if (!all.TryGetValue(id, out var item) || !item.Admin) continue;
            try
            {
                (long freed, string? note) r;
                if (item.BlockedNow is { } why) r = (0, why);
                else if (id == "sys:old") r = RemovePreviousWindows(item);
                else if (id == "sys:delivery") r = DeliveryOptimization(item);
                else
                {
                    // Windows Update keeps its downloads open while it runs
                    if (id == "sys:update") Service("wuauserv", false);
                    try { r = CleanOne(item); }
                    finally { if (id == "sys:update") Service("wuauserv", true); }
                }
                outcome[id] = new Outcome { Freed = r.freed, Note = r.note };
                Log.Note("cleanup", $"{item.Title} (admin): {StorageScan.Size(r.freed)}");
            }
            catch (Exception ex) { outcome[id] = new Outcome { Note = ex.Message }; Log.Error("cleanup " + id, ex); }
        }
        File.WriteAllText(resultFile, JsonSerializer.Serialize(outcome));
    }

    static void Service(string name, bool start)
    {
        try
        {
            // (net waits until the service has really stopped or started; sc does not)
            using var p = Process.Start(new ProcessStartInfo("net.exe", $"{(start ? "start" : "stop")} {name}") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(60000);
        }
        catch { }
    }

    /// <summary>Windows.old belongs to TrustedInstaller: take it over first, then remove it (as Disk Cleanup does).</summary>
    static (long, string?) RemovePreviousWindows(CleanItem item)
    {
        long before = 0;
        bool blind = false;
        try { before = Files(item, ref blind).Sum(f => f.Length); } catch { }
        foreach (var dir in item.Targets().Where(Directory.Exists))
        {
            Run("takeown.exe", $"/F \"{dir}\" /R /A /D Y");
            Run("icacls.exe", $"\"{dir}\" /grant *S-1-5-32-544:F /T /C /Q");
            Run("cmd.exe", $"/c rd /s /q \"{dir}\"");
        }
        bool left = item.Targets().Any(Directory.Exists);
        return (before, left ? "some of it couldn't be removed" : null);
    }

    static void Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return;
            p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            p.WaitForExit(30 * 60 * 1000);
        }
        catch (Exception ex) { Log.Error("cleanup " + exe, ex); }
    }

    // ---------------------------------------------------------------- unpacked single-file apps

    /// <summary>Where single-file .NET apps unpack themselves (one folder per app, one copy per version).</summary>
    static string NetDir => Path.Combine(Path.GetTempPath(), ".net");

    /// <summary>The copy this Brim runs from (never removed).</summary>
    static string Running => AppContext.BaseDirectory.TrimEnd('\\');

    /// <summary>Unpacked copies other than the one running, not touched in the last hour (one may be unpacking).</summary>
    static IEnumerable<string> OldUnpacked(Func<string, bool>? app) =>
        SafeDirs(NetDir).Where(a => app?.Invoke(Path.GetFileName(a)) ?? true)
                        .SelectMany(SafeDirs)
                        .Where(d => !string.Equals(d.TrimEnd('\\'), Running, StringComparison.OrdinalIgnoreCase))
                        .Where(d => { try { return (DateTime.Now - Directory.GetCreationTime(d)).TotalHours > 1; } catch { return false; } });

    /// <summary>
    /// Remove a whole folder or nothing: it's renamed first, which Windows refuses while any file in it is in use,
    /// so a running app never loses half of its files.
    /// </summary>
    static bool RemoveWhole(string dir)
    {
        var aside = dir.TrimEnd('\\') + ".brim-old";
        if (!dir.EndsWith(".brim-old", StringComparison.OrdinalIgnoreCase))
        {
            try { Directory.Move(dir, aside); }
            catch { return false; }
        }
        else aside = dir;
        try { Directory.Delete(aside, true); return true; }
        catch { return !Directory.Exists(aside); }
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

    /// <summary>
    /// Every Brim update (and every Setup run) unpacks a new copy of Brim to the temp folder, about 250 MB each, and
    /// .NET never removes the old ones. Brim removes its own old copies a little after it starts.
    /// </summary>
    public static void TidyOwnCopies()
    {
        try
        {
            long freed = 0; int removed = 0;
            foreach (var d in OldUnpacked(name => name.StartsWith("Brim", StringComparison.OrdinalIgnoreCase) || name.Equals("Setup", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                // Only Brim's: its own files are in there
                if (!File.Exists(Path.Combine(d, "Brim.dll")) && !d.EndsWith(".brim-old", StringComparison.OrdinalIgnoreCase)) continue;
                long s = FolderSize(d);
                if (RemoveWhole(d)) { freed += s; removed++; }
            }
            // App folders left empty
            foreach (var a in SafeDirs(NetDir).Where(a => Path.GetFileName(a).StartsWith("Brim", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(a) == "Setup"))
                try { if (!Directory.EnumerateFileSystemEntries(a).Any()) Directory.Delete(a); } catch { }
            if (removed > 0) Log.Note("cleanup", $"removed {removed} old unpacked copies of Brim ({StorageScan.Size(freed)})");
        }
        catch (Exception ex) { Log.Error("tidy own copies", ex); }
    }

    /// <summary>Delivery Optimization keeps a record of its files: let Windows remove them with its own command.</summary>
    static (long, string?) DeliveryOptimization(CleanItem item)
    {
        bool blind = false;
        long before = Files(item, ref blind).Sum(f => f.Length);
        Run("powershell.exe", "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force\"");
        long after = Files(item, ref blind).Sum(f => f.Length);
        return (Math.Max(0, before - after), null);
    }

    /// <summary>An update is installed and Windows is waiting to restart to finish it.</summary>
    static bool UpdatePending()
    {
        try
        {
            using var a = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var b = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return a != null || b != null;
        }
        catch { return false; }
    }

    static string BrowserProcess(string name) => name switch
    {
        "Chrome" => "chrome", "Edge" => "msedge", "Brave" => "brave", "Vivaldi" => "vivaldi", _ => "opera",
    };

    /// <summary>Cleaning an app's cache while it runs can upset it: wait until it is closed.</summary>
    static Func<string?> WhileRunning(string app, string process) => () =>
    {
        try
        {
            var ps = Process.GetProcessesByName(process);
            bool running = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            return running ? $"Close {app} to clean this" : null;
        }
        catch { return null; }
    };

    // ---------------------------------------------------------------- helpers

    static IEnumerable<string> ChromiumCaches(string userData)
    {
        var caches = new List<string> { Path.Combine(userData, "ShaderCache"), Path.Combine(userData, "GrShaderCache"), Path.Combine(userData, "GraphiteDawnCache") };
        // Opera keeps one profile right in its folder; the others have Default, Profile 1, …
        var profiles = SafeDirs(userData).Where(p => Path.GetFileName(p) is "Default" or "Guest Profile" || Path.GetFileName(p).StartsWith("Profile ")).ToList();
        if (File.Exists(Path.Combine(userData, "Preferences"))) profiles.Add(userData);
        // Only what the browser's own "Cached images and files" clears (not Service Worker storage: sites keep offline data there)
        foreach (var p in profiles)
            caches.AddRange(new[] { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache" }
                                .Select(x => Path.Combine(p, x)));
        // Opera's cache lives under Local, its profile under Roaming
        if (userData.Contains("Opera Software")) caches.Add(Path.Combine(userData, "Cache"));
        return caches;
    }

    static IEnumerable<string> SafeDirs(string dir) { try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); } }
    static IEnumerable<string> SafeFiles(string dir) { try { return Directory.GetFiles(dir); } catch { return Array.Empty<string>(); } }

    [StructLayout(LayoutKind.Sequential)] // 64-bit Windows: natural alignment (24 bytes)
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public static long RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    static void EmptyRecycleBin() => SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); // no confirm, no progress, no sound

    /// <summary>From search: Windows asks "Are you sure?" first, as the Recycle Bin's own Empty does.</summary>
    public static void EmptyRecycleBinAsk()
    {
        try { SHEmptyRecycleBin(IntPtr.Zero, null, 0); } catch (Exception ex) { Log.Error("empty recycle bin", ex); }
    }
}

/// <summary>Send files and folders to the Recycle Bin (Windows' own way, so they can be put back).</summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>
    /// No questions (Brim asked already), except one: Windows still asks before deleting anything too big for the
    /// Recycle Bin outright (and for administrator permission for protected folders).
    /// </summary>
    public static bool Send(IEnumerable<string> paths, IntPtr owner = default)
    {
        var list = paths.Select(p => p.TrimEnd('\\')).Where(p => p.Length > 3).ToList();
        if (list.Count == 0) return true;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = 0x3,                       // FO_DELETE
            pFrom = string.Join("\0", list) + "\0\0",
            // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING: without the last one, anything too big for the
            // Recycle Bin would be deleted outright without a word; with it Windows asks first
            fFlags = 0x40 | 0x10 | 0x4000,
        };
        return SHFileOperation(ref op) == 0 && op.fAnyOperationsAborted == 0;
    }
}
