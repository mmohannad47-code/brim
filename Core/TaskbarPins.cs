using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Brim.Core;

/// <summary>
/// The apps pinned to Windows' taskbar, in the same order, so the dock starts out the way you're used to. Copied
/// once, the first time the dock is switched on; after that the dock's pins are yours to change.
/// </summary>
public static class TaskbarPins
{
    static string PinnedFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned");
    static string CopiesFolder => Path.Combine(SettingsStore.Folder, "Pins");

    /// <summary>Windows' list of taskbar pins (it holds the order, and Store apps that have no shortcut file).</summary>
    static byte[]? Favorites()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            return k?.GetValue("Favorites") as byte[];
        }
        catch { return null; }
    }

    /// <summary>Where a piece of text first appears in the pin list (it's stored as UTF-16, at either byte alignment).</summary>
    static int PositionIn(byte[] blob, string text)
    {
        var needle = Encoding.Unicode.GetBytes(text);
        for (int i = 0; i + needle.Length <= blob.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && char.ToLowerInvariant((char)blob[i + j]) == char.ToLowerInvariant((char)needle[j])) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    /// <summary>Windows' taskbar pins, in order: programs (as their shortcuts) and Store apps (by their app id).</summary>
    public static List<(string name, string path, int at)> Read()
    {
        var found = new List<(string name, string path, int at)>();
        var blob = Favorites();
        try
        {
            if (Directory.Exists(PinnedFolder))
                foreach (var lnk in Directory.EnumerateFiles(PinnedFolder, "*.lnk", SearchOption.AllDirectories))
                {
                    var file = Path.GetFileName(lnk);
                    int at = blob != null ? PositionIn(blob, file) : -1;
                    // The TaskBar folder holds the pins themselves; Windows also keeps shortcuts elsewhere in here for apps
                    // that aren't pinned, so those only count when the pin list names them
                    bool pinnedFolder = string.Equals(Path.GetFileName(Path.GetDirectoryName(lnk)), "TaskBar", StringComparison.OrdinalIgnoreCase);
                    if (at < 0 && !pinnedFolder) continue;
                    found.Add((Path.GetFileNameWithoutExtension(lnk), lnk, at < 0 ? int.MaxValue : at));
                }
            // Store apps pinned without a shortcut file: their app id ("Publisher.App_8wekyb3d8bbwe!App") is in the list
            if (blob != null)
                foreach (int offset in new[] { 0, 1 })
                {
                    var text = Encoding.Unicode.GetString(blob, offset, blob.Length - offset);
                    foreach (Match m in Regex.Matches(text, @"[A-Za-z0-9][A-Za-z0-9.\-]+_[a-z0-9]{13}![A-Za-z0-9.\-_]+"))
                    {
                        var aumid = m.Value;
                        if (found.Any(f => f.path.EndsWith(aumid, StringComparison.OrdinalIgnoreCase))) continue;
                        var name = AppCatalog.All().FirstOrDefault(a => string.Equals(a.Id, aumid, StringComparison.OrdinalIgnoreCase))?.Name;
                        if (name == null) continue; // not installed (any more)
                        found.Add((name, "shell:AppsFolder\\" + aumid, m.Index * 2 + offset));
                    }
                }
        }
        catch (Exception ex) { Log.Error("taskbar pins", ex); }
        return found.OrderBy(f => f.at).ThenBy(f => f.name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The first time the dock is switched on: start it with Windows' taskbar pins. Only ever once, and never over
    /// pins you already have.
    /// </summary>
    public static void ImportOnce()
    {
        var s = SettingsStore.Current;
        if (s.DockPinsImported) return;
        s.DockPinsImported = true;
        if (s.DockPins.Count == 0)
        {
            var added = new List<string>();
            foreach (var (name, path, _) in Read())
            {
                var keep = path;
                // A copy of the shortcut, so the dock keeps it even if it's later unpinned from Windows' taskbar
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    try
                    {
                        Directory.CreateDirectory(CopiesFolder);
                        keep = Path.Combine(CopiesFolder, Path.GetFileName(path));
                        File.Copy(path, keep, true);
                    }
                    catch { keep = path; }
                s.DockPins.Add(new ShortcutItem { Name = name, Path = keep });
                added.Add(name);
            }
            Log.Note("dock", added.Count > 0 ? "started with Windows' taskbar pins: " + string.Join(", ", added) : "Windows' taskbar had no pins to start with");
        }
        SettingsStore.Save();
    }
}
