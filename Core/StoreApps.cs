using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Brim.Core;

/// <summary>
/// Microsoft Store (packaged) apps such as Spotify. Their files live in
/// <c>…\WindowsApps\&lt;Name&gt;_&lt;Version&gt;_&lt;Arch&gt;_&lt;Res&gt;_&lt;Publisher&gt;\</c>: Windows won't start those exes
/// directly ("Access is denied"), and the folder changes with every update. They start by app id
/// (<c>family!app</c>), and the dock knows them by that id, so a pin made from the running window, one picked
/// from the Start list ("shell:AppsFolder\…") and the running window all match, before and after an update.
/// </summary>
public static class StoreApps
{
    public sealed record App(string Family, string AppId, string? Exe)
    {
        public string Aumid => Family + "!" + AppId;
        public string ShellPath => AppsFolder + Aumid;
        /// <summary>The dock's key for this app (the same whichever way it was found).</summary>
        public string Key => ("store:" + Aumid).ToLowerInvariant();
    }

    const string AppsFolder = "shell:AppsFolder\\";

    // Package folder name: Name_Version_Arch_ResourceId_PublisherId (names never contain "_"; ResourceId is usually
    // empty). Windows' own apps live in SystemApps\Name_PublisherId (the family name itself).
    static readonly Regex InPackage =
        new(@"(?i)^(?<dir>.*\\(WindowsApps\\(?<name>[^\\_]+)_[^\\_]+_[^\\_]+_[^\\_]*_(?<pub>[^\\_]+)|SystemApps\\(?<name>[^\\_]+)_(?<pub>[^\\_]+)))\\(?<rel>.+)$");

    static readonly ConcurrentDictionary<string, App?> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Could this be a Store app (cheap check, no disk access)?</summary>
    public static bool Looks(string? path) =>
        !string.IsNullOrEmpty(path) &&
        (path.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase) && path.Contains('!') || InPackage.IsMatch(path));

    /// <summary>The Store app behind a path: a file inside a package folder, or "shell:AppsFolder\family!app".</summary>
    public static App? Find(string? path, bool fresh = false)
    {
        if (!Looks(path)) return null;
        if (fresh) { cache.TryRemove(path!, out _); manifests.Clear(); }
        else if (cache.TryGetValue(path!, out var hit)) return hit;
        App? app = null;
        try { app = Resolve(path!); }
        catch (Exception ex) { Log.Error("store app " + path, ex); }
        if (cache.Count > 500) cache.Clear();
        cache[path!] = app;
        return app;
    }

    /// <summary>Forget what's known (a Store app was installed, updated or removed).</summary>
    public static void Forget() { cache.Clear(); manifests.Clear(); identities.Clear(); }

    static App? Resolve(string path)
    {
        if (path.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase))
        {
            var id = path.Substring(AppsFolder.Length);
            int bang = id.IndexOf('!');
            if (bang <= 0) return null;
            var family = id[..bang];
            var appId = id[(bang + 1)..];
            var dir = InstallDir(family, null);
            var entry = dir == null ? null : Manifest(dir).FirstOrDefault(a => a.Id.Equals(appId, StringComparison.OrdinalIgnoreCase));
            return new App(family, entry?.Id ?? appId, dir != null && entry?.Exe != null ? Path.Combine(dir, entry.Exe) : null);
        }

        var m = InPackage.Match(path);
        if (!m.Success) return null;
        var dirName = m.Groups["dir"].Value;
        var rel = m.Groups["rel"].Value;
        bool here = File.Exists(Path.Combine(dirName, "AppxManifest.xml"));
        // The family is the package's name (from its manifest: SystemApps folders can be named differently, e.g.
        // ShellExperienceHost_… for Microsoft.Windows.ShellExperienceHost) plus the publisher id from the folder
        var fam = ((here ? IdentityName(dirName) : null) ?? m.Groups["name"].Value) + "_" + m.Groups["pub"].Value;
        // The folder named in the path, or (the app updated since) wherever it lives now
        var current = here ? dirName : InstallDir(fam, rel);
        if (current == null) return null; // uninstalled
        var apps = Manifest(current);
        // The app whose program this is (the one shown in Start when several share it, like Teams); a helper exe
        // (Spotify.exe, which Start opens through SpotifyMigrator.exe) or a hidden entry (SpotifyLauncher) counts
        // as the package's main app: the first one listed in Start
        var pick = apps.Where(a => a.Exe != null && a.Exe.Equals(rel, StringComparison.OrdinalIgnoreCase)).OrderByDescending(a => a.Listed).FirstOrDefault();
        if (pick?.Listed != true) pick = apps.FirstOrDefault(a => a.Listed) ?? pick ?? apps.FirstOrDefault();
        if (pick == null) return null;
        var exe = Path.Combine(current, rel);
        return new App(fam, pick.Id, File.Exists(exe) ? exe : pick.Exe != null ? Path.Combine(current, pick.Exe) : null);
    }

    sealed record Entry(string Id, string? Exe, bool Listed);

    static readonly ConcurrentDictionary<string, string?> identities = new(StringComparer.OrdinalIgnoreCase);

    static string? IdentityName(string dir) => identities.GetOrAdd(dir, d =>
    {
        try
        {
            var doc = XDocument.Load(Path.Combine(d, "AppxManifest.xml"));
            var name = (string?)doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name");
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    });

    static readonly ConcurrentDictionary<string, List<Entry>> manifests = new(StringComparer.OrdinalIgnoreCase);

    static List<Entry> Manifest(string dir) => manifests.GetOrAdd(dir, d =>
    {
        try
        {
            var doc = XDocument.Load(Path.Combine(d, "AppxManifest.xml"));
            return doc.Descendants().Where(e => e.Name.LocalName == "Application")
                .Select(e =>
                {
                    var exe = (string?)e.Attribute("Executable");
                    var visual = e.Descendants().FirstOrDefault(v => v.Name.LocalName == "VisualElements");
                    bool listed = !string.Equals((string?)visual?.Attribute("AppListEntry"), "none", StringComparison.OrdinalIgnoreCase);
                    return new Entry((string?)e.Attribute("Id") ?? "", exe?.Replace('/', '\\'), listed);
                })
                .Where(a => a.Id.Length > 0).ToList();
        }
        catch { return new List<Entry>(); }
    });

    // ------------------------------------------------------------------ where a package is installed now

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint bufferLength, IntPtr buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackagePathByFullName(string fullName, ref uint length, StringBuilder? path);

    /// <summary>
    /// This user's install folder for a package family (the main package, not a resource pack of the same family):
    /// the one that has <paramref name="rel"/>, else the one that declares apps.
    /// </summary>
    static string? InstallDir(string family, string? rel)
    {
        uint count = 0, len = 0;
        if (GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref len, IntPtr.Zero) != 122 /* insufficient buffer */ || count == 0) return null;
        IntPtr names = Marshal.AllocHGlobal((int)count * IntPtr.Size), buffer = Marshal.AllocHGlobal((int)len * 2);
        try
        {
            if (GetPackagesByPackageFamily(family, ref count, names, ref len, buffer) != 0) return null;
            var dirs = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var full = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size));
                if (full == null) continue;
                uint plen = 0;
                GetPackagePathByFullName(full, ref plen, null);
                if (plen == 0) continue;
                var sb = new StringBuilder((int)plen);
                if (GetPackagePathByFullName(full, ref plen, sb) == 0) dirs.Add(sb.ToString());
            }
            return (rel != null ? dirs.FirstOrDefault(d => File.Exists(Path.Combine(d, rel))) : null)
                   ?? dirs.FirstOrDefault(d => Manifest(d).Count > 0);
        }
        finally { Marshal.FreeHGlobal(names); Marshal.FreeHGlobal(buffer); }
    }

    // ------------------------------------------------------------------ app id of a running process / window

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? id);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    /// <summary>The app id of a running packaged process (null for ordinary programs).</summary>
    public static string? AumidOfProcess(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* query limited */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint len = 0;
            if (GetApplicationUserModelId(h, ref len, null) != 122 || len == 0) return null;
            var sb = new StringBuilder((int)len);
            return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    /// <summary>
    /// The app id a window carries (how the taskbar groups it). Store apps hosted by ApplicationFrameHost
    /// (Calculator, Photos…) only say who they are this way.
    /// </summary>
    public static string? AumidOfWindow(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return null;
            try
            {
                var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                if (store.GetValue(ref key, out var v) != 0) return null;
                try { return v.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(v.p) : null; }
                finally { PropVariantClear(ref v); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ starting one

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] class ApplicationActivationManager { }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string aumid, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint pid);
    }

    /// <summary>Start a Store app by its id, passing arguments (e.g. files dropped on its dock icon).</summary>
    public static bool Launch(string aumid, string? args)
    {
        try
        {
            var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
            try
            {
                int hr = mgr.ActivateApplication(aumid, string.IsNullOrWhiteSpace(args) ? null : args, 0, out _);
                if (hr == 0) return true;
                Log.Note("launch", $"activating {aumid} failed (0x{hr:X8}); trying Explorer");
            }
            finally { Marshal.ReleaseComObject(mgr); }
        }
        catch (Exception ex) { Log.Error("activate " + aumid, ex); }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", AppsFolder + aumid) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { Log.Error("launch " + aumid, ex); return false; }
    }

    /// <summary>Dev check (no windows opened): how every running packaged app and Start's Store apps resolve.</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        int fails = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            string? path = null;
            try { path = Native.ProcessPath((uint)p.Id); } catch { }
            if (path == null || !InPackage.IsMatch(path) || !seen.Add(path)) continue;
            var app = Find(path);
            var real = AumidOfProcess((uint)p.Id);
            // A helper may run under its own app id; only the package has to agree
            bool ok = app != null && (real == null || real.StartsWith(app.Family + "!", StringComparison.OrdinalIgnoreCase));
            if (!ok) fails++;
            sb.AppendLine($"{(ok ? "ok  " : "FAIL")} running {Path.GetFileName(path)} → {app?.Aumid ?? "?"} (process says {real ?? "-"})");
        }
        foreach (var a in AppCatalog.Fresh().Where(a => a.Exe == null && a.Id.Contains('!')))
        {
            var app = Find(AppsFolder + a.Id);
            bool ok = app != null && app.Exe != null && Find(app.Exe)?.Key == app.Key;
            if (!ok && app != null && (app.Exe == null || !Looks(app.Exe)))
            {
                // Outside a package folder (Settings): the dock matches its window by the id the window carries
                sb.AppendLine($"--   start {a.Name} → {app.Aumid} (program outside a package folder; matched by window id)");
                continue;
            }
            if (!ok) fails++;
            sb.AppendLine($"{(ok ? "ok  " : "FAIL")} start {a.Name} → {app?.Exe ?? "?"} (key {app?.Key}, exe key {(app?.Exe != null ? Find(app.Exe)?.Key : null)})");
        }
        // A pin made before the app updated: its old version folder is gone, it must still find the app
        foreach (var a in AppCatalog.All().Where(a => a.Exe == null && a.Id.Contains('!')).Select(a => Find(AppsFolder + a.Id)).Where(a => a?.Exe != null && a.Exe.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)).Take(5))
        {
            var old = Regex.Replace(a!.Exe!, @"(?i)(\\WindowsApps\\[^\\_]+_)[^\\_]+(_)", "${1}0.0.0.1$2");
            var found = Find(old);
            // (the program the manifest names may be a stub that isn't on disk, like Copilot's proxy: compare paths)
            bool ok = found?.Key == a.Key && string.Equals(found.Exe, a.Exe, StringComparison.OrdinalIgnoreCase);
            if (!ok) fails++;
            sb.AppendLine($"{(ok ? "ok  " : "FAIL")} after update {Path.GetFileName(old)} (old folder) → {found?.Exe ?? "?"}" +
                          (ok ? "" : $" (key {found?.Key}, want {a.Key}, file there: {(found?.Exe != null && File.Exists(found.Exe))})"));
        }
        sb.Insert(0, fails == 0 ? "ALL PASSED\n" : $"{fails} FAILED\n");
        return sb.ToString();
    }
}
