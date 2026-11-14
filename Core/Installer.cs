using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Brim.Core;

/// <summary>
/// Installs Brim for the current user (no admin needed): copies it to %LOCALAPPDATA%\Programs\Brim, adds a Start menu
/// shortcut and an entry in Settings › Apps › Installed apps, whose Uninstall puts Windows back the way it was.
/// </summary>
public static class Installer
{
    public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Brim");
    static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Brim.lnk");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Brim";

    public static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "0";
    // Where Brim.exe itself is. (Not AppContext.BaseDirectory: the single-file release unpacks itself to a temp
    // folder and reports that, which made the installer copy the unpacked files and leave out Brim.exe.)
    static string CurrentDir => Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!.TrimEnd('\\');

    /// <summary>This Brim is one self-contained exe (the download), not a folder of files.</summary>
    static bool IsSingleFile => !File.Exists(Path.Combine(CurrentDir, "Brim.dll"));

    public static bool IsInstalled => File.Exists(Path.Combine(InstallDir, "Brim.exe"));
    public static bool RunningInstalled => string.Equals(CurrentDir, InstallDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Running from the build folder while developing (no install offer then).</summary>
    public static bool IsDevCopy => File.Exists(Path.Combine(CurrentDir, "..", "Brim.csproj"));

    /// <summary>Close the running Brim (installed copy) so its files can be replaced; it restores the taskbar as it quits.</summary>
    public static void CloseRunning()
    {
        foreach (var p in Process.GetProcessesByName("Brim"))
        {
            if (p.Id == Environment.ProcessId) continue;
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
                if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(2000); }
            }
            catch { }
        }
    }

    /// <summary>
    /// Start the installed copy and let this one quit: the installed Brim waits until this one has closed
    /// (and put the taskbar back) before it starts, so there's never two at once.
    /// </summary>
    public static void SwitchToInstalled()
    {
        var exe = Path.Combine(InstallDir, "Brim.exe");
        Process.Start(new ProcessStartInfo(exe, $"--wait-for {Environment.ProcessId}") { UseShellExecute = true, WorkingDirectory = InstallDir });
    }

    /// <summary>Copy this copy of Brim into place and register it. Returns the installed exe.</summary>
    public static string Install()
    {
        Directory.CreateDirectory(InstallDir);
        if (!RunningInstalled)
        {
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find Brim's own file");
            var deps = Path.Combine(CurrentDir, "Brim.deps.json");
            // Start clean: files from an older or half-finished install could get in the way of this one
            ClearInstallDir();
            if (!IsSingleFile && File.Exists(deps))
            {
                // Brim as a folder of files: copy only Brim's own files (never the rest of the folder it sits in)
                foreach (var rel in OwnFiles(deps))
                {
                    var src = Path.Combine(CurrentDir, rel);
                    if (!File.Exists(src)) continue;
                    var dest = Path.Combine(InstallDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(src, dest, true);
                }
            }
            else
            {
                // Brim as one exe (the download): copy just that, named Brim.exe
                File.Copy(self, Path.Combine(InstallDir, "Brim.exe"), true);
            }
        }
        var exe = Path.Combine(InstallDir, "Brim.exe");
        if (!File.Exists(exe)) throw new IOException($"Brim.exe didn't arrive in {InstallDir}");

        CreateShortcut(StartMenuLink, exe);
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Brim");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Brim");
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024), RegistryValueKind.DWord);
        }
        // Keep "start with Windows" pointing at the installed copy
        if (SettingsStore.Current.StartWithWindows) Startup.Set(true, exe);
        return exe;
    }

    /// <summary>Empty Brim's program folder (only ever %LOCALAPPDATA%\Programs\Brim; settings live elsewhere).</summary>
    static void ClearInstallDir()
    {
        if (!Directory.Exists(InstallDir) || RunningInstalled) return;
        foreach (var f in Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch (Exception ex) { Log.Error("install clear " + Path.GetFileName(f), ex); }
        foreach (var d in Directory.GetDirectories(InstallDir))
            try { Directory.Delete(d, true); } catch { }
    }

    /// <summary>Put Windows back (taskbar, desktop icons, start-up), remove shortcuts and the program files.</summary>
    public static void Uninstall(bool keepSettings)
    {
        // Other Brim processes (the running app, the live wallpaper player) must go first
        foreach (var p in Process.GetProcessesByName("Brim"))
        {
            if (p.Id == Environment.ProcessId) continue;
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        }
        try { TaskbarManager.RestoreAll(); } catch (Exception ex) { Log.Error("uninstall taskbar", ex); }
        try { DesktopIcons.Set(true); } catch { }
        try { SettingsStore.Load(); WindowsBanners.RestoreAll(); } catch { } // the running Brim was closed abruptly above
        try { Startup.Set(false); } catch { }
        try { WallpaperEngine.Control("play"); } catch { }
        try { if (File.Exists(StartMenuLink)) File.Delete(StartMenuLink); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

        // Files in use can't delete themselves; a short-lived cmd does it once Brim has quit
        var targets = $"rmdir /s /q \"{InstallDir}\"";
        if (!keepSettings) targets += $" & rmdir /s /q \"{SettingsStore.Folder}\" & rmdir /s /q \"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim")}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & {targets}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>Brim's own files, as listed in its .deps.json (plus the exe, the dll and its config files).</summary>
    static System.Collections.Generic.IEnumerable<string> OwnFiles(string depsFile)
    {
        var files = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Brim.exe", "Brim.dll", "Brim.deps.json", "Brim.runtimeconfig.json" };
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(depsFile));
        if (doc.RootElement.TryGetProperty("targets", out var targets))
            foreach (var target in targets.EnumerateObject())
                foreach (var lib in target.Value.EnumerateObject())
                    foreach (var kind in new[] { "runtime", "native", "runtimeTargets", "resources" })
                        if (lib.Value.TryGetProperty(kind, out var assets))
                            foreach (var a in assets.EnumerateObject())
                            {
                                var rel = a.Name.Replace('/', '\\');
                                // Plain package assets are published flat; runtimes\… keep their folders
                                files.Add(rel.StartsWith("runtimes\\", StringComparison.OrdinalIgnoreCase) ? rel : Path.GetFileName(rel));
                            }
        return files;
    }

    static void CreateShortcut(string link, string target)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(t!)!;
            dynamic sc = shell.CreateShortcut(link);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.IconLocation = target + ",0";
            sc.Description = "Brim: notch, dock and widgets for Windows";
            sc.Save();
        }
        catch (Exception ex) { Log.Error("shortcut", ex); }
    }

    // ---------- settings backup ----------

    /// <summary>Save all of Brim's settings (layout, widgets, dock, games…) to one file.</summary>
    public static void Export(string path)
    {
        SettingsStore.Save();
        File.Copy(Path.Combine(SettingsStore.Folder, "settings.json"), path, true);
    }

    /// <summary>Load settings from a backup. The current settings are kept as settings.before-import.json.</summary>
    public static bool Import(string path)
    {
        var text = File.ReadAllText(path);
        // Make sure it really is a Brim settings file before replacing anything
        var probe = System.Text.Json.JsonDocument.Parse(text);
        if (!probe.RootElement.TryGetProperty("Theme", out _) && !probe.RootElement.TryGetProperty("Widgets", out _)) return false;
        var file = Path.Combine(SettingsStore.Folder, "settings.json");
        if (File.Exists(file)) File.Copy(file, Path.Combine(SettingsStore.Folder, "settings.before-import.json"), true);
        File.WriteAllText(file, text);
        SettingsStore.Load();
        return true;
    }
}
