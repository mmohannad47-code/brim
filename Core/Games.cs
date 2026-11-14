using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Brim.Core;

public sealed class GameInfo
{
    public string Key { get; init; } = "";
    public string Name { get; set; } = "";
    public string Source { get; init; } = "";
    public string? Exe { get; set; }
    public string? Args { get; set; }
    public string? WorkDir { get; set; }
    public string? LaunchUri { get; set; }
    public string? Folder { get; init; }
    public string? SteamAppId { get; init; }
    public uint? ShortcutAppId { get; init; }
    public string? GridFolder { get; init; }
    public List<string> Candidates { get; set; } = new();
    public DateTime LastPlayed { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Exe to take the icon from when the game starts through a launcher (e.g. League via the Riot Client).</summary>
    public string? IconExe { get; init; }
}

/// <summary>
/// Counts how long you play each game: every 30 seconds, if the focused window belongs to a game's folder, that game
/// gets the time. Works for games started anywhere (Steam, a desktop shortcut…), not only from Brim.
/// </summary>
public static class PlayTime
{
    static readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    static uint lastPid;
    static GameInfo? lastGame;
    static int unsaved;
    static AppSettings S => SettingsStore.Current;

    /// <summary>The game being played right now, if any.</summary>
    public static GameInfo? Playing { get; private set; }
    public static event Action? Changed;

    public static void Start()
    {
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    static void Tick()
    {
        try
        {
            var fg = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid != lastPid)
            {
                lastPid = pid;
                var path = pid == 0 ? null : Native.ProcessPath(pid);
                lastGame = path == null ? null : Match(path);
            }
            var was = Playing;
            Playing = lastGame;
            if (lastGame == null) { if (was != null) { Save(); Changed?.Invoke(); } return; }
            S.GamePlaySeconds[lastGame.Key] = Seconds(lastGame) + timer.Interval.TotalSeconds;
            lastGame.LastPlayed = DateTime.Now;
            S.GameLaunches[lastGame.Key] = DateTime.Now;
            if (was != lastGame) Changed?.Invoke();
            if (++unsaved >= 10) Save(); // every five minutes of play
        }
        catch (Exception ex) { Log.Error("play time", ex); }
    }

    static void Save()
    {
        if (unsaved == 0) return;
        unsaved = 0;
        SettingsStore.Save();
    }

    static GameInfo? Match(string path)
    {
        foreach (var g in GameLibrary.Current)
        {
            if (g.Exe != null && string.Equals(g.Exe, path, StringComparison.OrdinalIgnoreCase)) return g;
            var folder = g.Folder?.TrimEnd('\\');
            if (folder is { Length: > 3 } && path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)) return g;
        }
        return null;
    }

    public static double Seconds(GameInfo g) => S.GamePlaySeconds.TryGetValue(g.Key, out var s) ? s : 0;

    /// <summary>"12 min", "3.5 h", or "" when never played.</summary>
    public static string Format(GameInfo g)
    {
        var s = Seconds(g);
        if (s < 60) return "";
        if (s < 3600) return $"{Math.Round(s / 60)} min";
        var h = s / 3600;
        return h < 10 ? $"{h:0.#} h" : $"{Math.Round(h)} h";
    }

    public static string Ago(DateTime when)
    {
        if (when == default) return "";
        var d = DateTime.Now - when;
        if (d.TotalMinutes < 2) return "Playing now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "Yesterday";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} days ago";
        return when.ToString("d MMM");
    }
}

/// <summary>
/// Finds installed games: Steam, non-Steam games you added to Steam, Epic, GOG, game folders (like D:\Games)
/// and ones you add yourself. Watches all of those places so new games show up on their own.
/// </summary>
public static class GameLibrary
{
    public static event Action? Changed;
    public static List<GameInfo> Current { get; private set; } = new();
    public static bool Scanned { get; private set; }

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly Dictionary<string, ImageSource?> covers = new();
    static readonly List<FileSystemWatcher> watchers = new();
    static System.Windows.Threading.DispatcherTimer? debounce;
    static bool scanning;
    public static readonly string CoverFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim", "Covers");

    static AppSettings S => SettingsStore.Current;

    static GameLibrary() => http.DefaultRequestHeaders.UserAgent.ParseAdd("Brim/0.3");

    // Wherever Windows keeps them on this PC (not always C:)
    static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>Epic's install list: where the launcher says (registry), else its default under ProgramData.</summary>
    static string EpicManifests
    {
        get
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher") ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Epic Games\EpicGamesLauncher");
                if (k?.GetValue("AppDataPath") is string p && p.Length > 3) return Path.Combine(p, "Manifests");
            }
            catch { }
            return Path.Combine(ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        }
    }

    // ------------------------------------------------------------------ scanning

    public static async Task Refresh()
    {
        if (scanning) return;
        scanning = true;
        try
        {
            var list = await Task.Run(ScanAll);
            Current = list;
            Scanned = true;
            Watch();
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Error("games scan", ex); }
        finally { scanning = false; }
    }

    static List<GameInfo> ScanAll()
    {
        var found = new List<GameInfo>();
        try { found.AddRange(Steam()); } catch (Exception ex) { Log.Error("steam games", ex); }
        try { found.AddRange(SteamShortcuts()); } catch (Exception ex) { Log.Error("steam shortcuts", ex); }
        try { found.AddRange(Epic()); } catch (Exception ex) { Log.Error("epic games", ex); }
        try { found.AddRange(Gog()); } catch (Exception ex) { Log.Error("gog games", ex); }
        try { found.AddRange(Riot()); } catch (Exception ex) { Log.Error("riot games", ex); }
        try { found.AddRange(Ubisoft()); } catch (Exception ex) { Log.Error("ubisoft games", ex); }
        try { found.AddRange(PublisherInstalls()); } catch (Exception ex) { Log.Error("ea/blizzard games", ex); }
        try { found.AddRange(Xbox()); } catch (Exception ex) { Log.Error("xbox games", ex); }
        foreach (var m in S.ManualGames.Where(m => !Path.IsPathFullyQualified(m.Path) || File.Exists(m.Path)))
            found.Add(new GameInfo { Key = "manual:" + m.Path.ToLowerInvariant(), Name = m.Name, Source = "Added by you", Exe = m.Path, Args = m.Args, Folder = Path.GetDirectoryName(m.Path) });

        // Game folders last, skipping anything already found another way
        var knownExes = new HashSet<string>(found.Where(g => g.Exe != null).Select(g => Norm(g.Exe!)), StringComparer.OrdinalIgnoreCase);
        var knownDirs = found.Where(g => g.Exe != null).Select(g => Norm(Path.GetDirectoryName(g.Exe!) ?? ""))
            .Concat(found.Where(g => g.Folder != null).Select(g => Norm(g.Folder!)))
            .Where(d => d.Length > 3).ToList();
        bool Known(GameInfo g) =>
            (g.Exe != null && knownExes.Contains(Norm(g.Exe))) ||
            knownDirs.Any(d => d.StartsWith(Norm(g.Folder!), StringComparison.OrdinalIgnoreCase) ||
                               Norm(g.Folder!).StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(d, Norm(g.Folder!), StringComparison.OrdinalIgnoreCase));
        try
        {
            foreach (var g in FolderGames())
            {
                if (Known(g)) continue;
                found.Add(g);
                knownDirs.Add(Norm(g.Folder!));
            }
        }
        catch (Exception ex) { Log.Error("folder games", ex); }
        if (S.GameFingerprints)
        {
            try
            {
                foreach (var g in FingerprintGames())
                {
                    if (Known(g)) continue;
                    found.Add(g);
                    knownDirs.Add(Norm(g.Folder!));
                }
            }
            catch (Exception ex) { Log.Error("fingerprint games", ex); }
        }

        // Every game, whatever found it, gets the other ways to start it (DirectX 11 / Vulkan exes, launchers…)
        foreach (var g in found)
        {
            if (g.Candidates.Count > 0 || g.Folder == null) continue;
            var dir = g.Folder.TrimEnd('\\');
            // Never walk a whole drive or a shared folder like Program Files (a game added by hand from there)
            if (dir.Length <= 3 || NotGameFolder.IsMatch(Path.GetFileName(dir)) || VendorFolder.IsMatch(Path.GetFileName(dir)) ||
                Regex.IsMatch(Path.GetFileName(dir), @"(?i)^program files")) continue;
            try { if (Directory.Exists(dir)) g.Candidates = ExeCandidates(dir); } catch { }
        }

        // Your changes win
        foreach (var g in found)
        {
            var o = S.GameOverrides.FirstOrDefault(x => x.Key == g.Key);
            if (o != null)
            {
                if (!string.IsNullOrWhiteSpace(o.Name)) g.Name = o.Name!;
                if (!string.IsNullOrWhiteSpace(o.Exe)) g.Exe = o.Exe;
                if (o.Args != null) g.Args = o.Args;
                g.Hidden = o.Hidden;
            }
            if (S.GameLaunches.TryGetValue(g.Key, out var t) && t > g.LastPlayed) g.LastPlayed = t;
        }
        return Ordered(found.GroupBy(g => g.Key).Select(x => x.First()));
    }

    /// <summary>The folder exists and has something in it (a deleted game can leave an empty folder behind).</summary>
    static bool HasFiles(string? dir)
    {
        try { return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }

    /// <summary>Your own order first (if you've dragged games around), then everything else, most recently played first.</summary>
    static List<GameInfo> Ordered(IEnumerable<GameInfo> games)
    {
        var order = S.GameOrder;
        return games
            .OrderBy(g => { int i = order.IndexOf(g.Key); return i < 0 ? int.MaxValue : i; })
            .ThenByDescending(g => g.LastPlayed)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Save a new order (from dragging in the Games tab) and show it straight away.</summary>
    public static void SetOrder(List<string> keys)
    {
        S.GameOrder = keys;
        SettingsStore.Save();
        Current = Ordered(Current);
        Changed?.Invoke();
    }

    public static void ResetOrder()
    {
        S.GameOrder = new();
        SettingsStore.Save();
        Current = Ordered(Current);
        Changed?.Invoke();
    }

    /// <summary>Use a picture you chose as a game's cover.</summary>
    public static void SetCustomCover(GameInfo g, string? imagePath)
    {
        var o = Override(g.Key);
        if (imagePath == null) o.Cover = null;
        else
        {
            Directory.CreateDirectory(CoverFolder);
            var dest = Path.Combine(CoverFolder, "custom-" + SafeName(g.Key) + Path.GetExtension(imagePath));
            File.Copy(imagePath, dest, true);
            o.Cover = dest;
        }
        SettingsStore.Save();
        covers.Remove(g.Key);
        missing.Remove(g.Key);
        Changed?.Invoke();
    }

    static string Norm(string p)
    {
        try { return Path.GetFullPath(p.Trim().Trim('"')).TrimEnd('\\'); } catch { return p; }
    }

    public static IEnumerable<string> SteamLibraries()
    {
        var libs = new List<string>();
        var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (string.IsNullOrEmpty(steam)) return libs;
        libs.Add(Norm(steam));
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(Norm(m.Groups[1].Value.Replace(@"\\", @"\")));
        return libs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    static string? SteamRoot()
    {
        var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        return string.IsNullOrEmpty(steam) ? null : Norm(steam);
    }

    static readonly Regex NotAGame = new(@"(?i)redistributable|steamworks|\bsdk\b|proton|steam linux runtime|dedicated server|wallpaper engine|soundtrack|benchmark|steamvr");

    static IEnumerable<GameInfo> Steam()
    {
        foreach (var lib in SteamLibraries())
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf); } catch { continue; }
                string Field(string k) => Regex.Match(text, $"\"{k}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                var id = Field("appid");
                var name = Field("name");
                if (id.Length == 0 || NotAGame.IsMatch(name) || id is "228980" or "431960") continue;
                long.TryParse(Field("LastPlayed"), out long last);
                var dir = Path.Combine(apps, "common", Field("installdir"));
                // Steam can leave a game's record behind after its folder is gone (deleted by hand, old drive): skip those
                if (!HasFiles(dir)) continue;
                yield return new GameInfo
                {
                    Key = "steam:" + id,
                    Name = name,
                    Source = "Steam",
                    SteamAppId = id,
                    LaunchUri = "steam://rungameid/" + id,
                    Folder = Directory.Exists(dir) ? dir : null,
                    Candidates = Directory.Exists(dir) ? ExeCandidates(dir) : new List<string>(),
                    LastPlayed = last > 0 ? DateTimeOffset.FromUnixTimeSeconds(last).LocalDateTime : DateTime.MinValue,
                };
            }
        }
    }

    // Steam's shortcuts.vdf (binary key/values) holds the non-Steam games you added to Steam
    static IEnumerable<GameInfo> SteamShortcuts()
    {
        var root = SteamRoot();
        if (root == null) yield break;
        var userdata = Path.Combine(root, "userdata");
        if (!Directory.Exists(userdata)) yield break;
        foreach (var user in Directory.GetDirectories(userdata))
        {
            var file = Path.Combine(user, "config", "shortcuts.vdf");
            if (!File.Exists(file)) continue;
            Dictionary<string, object> data;
            try
            {
                using var r = new BinaryReader(File.OpenRead(file));
                data = ReadVdfMap(r);
            }
            catch (Exception ex) { Log.Error("shortcuts.vdf", ex); continue; }
            if (data.Values.FirstOrDefault() is not Dictionary<string, object> list) continue;
            foreach (var entry in list.Values.OfType<Dictionary<string, object>>())
            {
                string Get(string k) => entry.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value as string ?? "";
                int GetInt(string k) => entry.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value is int i ? i : 0;
                var exe = Get("Exe").Trim().Trim('"');
                if (exe.Length == 0) continue;
                // A program that has since been removed (only for real file paths; links and commands stay)
                if (Path.IsPathFullyQualified(exe) && !File.Exists(exe)) continue;
                var name = Get("AppName");
                var start = Get("StartDir").Trim().Trim('"');
                int last = GetInt("LastPlayTime");
                yield return new GameInfo
                {
                    Key = "shortcut:" + exe.ToLowerInvariant(),
                    Name = name.Length > 0 ? name : Path.GetFileNameWithoutExtension(exe),
                    Source = "Steam (non-Steam game)",
                    Exe = exe,
                    Args = Get("LaunchOptions"),
                    WorkDir = start.Length > 0 ? start : null,
                    Folder = Path.GetDirectoryName(exe),
                    ShortcutAppId = unchecked((uint)GetInt("appid")),
                    GridFolder = Path.Combine(user, "config", "grid"),
                    LastPlayed = last > 0 ? DateTimeOffset.FromUnixTimeSeconds(last).LocalDateTime : DateTime.MinValue,
                };
            }
        }
    }

    static Dictionary<string, object> ReadVdfMap(BinaryReader r)
    {
        var map = new Dictionary<string, object>();
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            byte type = r.ReadByte();
            if (type == 0x08) break;
            string key = ReadCString(r);
            switch (type)
            {
                case 0x00: map[key] = ReadVdfMap(r); break;
                case 0x01: map[key] = ReadCString(r); break;
                case 0x02: map[key] = r.ReadInt32(); break;
                case 0x07: map[key] = r.ReadInt64(); break;
                default: return map;
            }
        }
        return map;
    }

    static string ReadCString(BinaryReader r)
    {
        var bytes = new List<byte>();
        byte b;
        while (r.BaseStream.Position < r.BaseStream.Length && (b = r.ReadByte()) != 0) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    static IEnumerable<GameInfo> Epic()
    {
        var dir = EpicManifests;
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.GetFiles(dir, "*.item"))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(File.ReadAllText(f)).RootElement; } catch { continue; }
            string Str(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
            var app = Str("AppName");
            var loc = Str("InstallLocation");
            if (!HasFiles(loc)) continue; // uninstalled (or the folder was deleted) but Epic kept the record
            yield return new GameInfo
            {
                Key = "epic:" + app,
                Name = Str("DisplayName"),
                Source = "Epic Games",
                LaunchUri = $"com.epicgames.launcher://apps/{app}?action=launch&silent=true",
                Exe = loc.Length > 0 && Str("LaunchExecutable").Length > 0 ? Path.Combine(loc, Str("LaunchExecutable")) : null,
                Folder = loc,
            };
        }
    }

    static IEnumerable<GameInfo> Gog()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (key == null) yield break;
        foreach (var id in key.GetSubKeyNames())
        {
            using var g = key.OpenSubKey(id);
            var exe = g?.GetValue("exe") as string;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;
            yield return new GameInfo
            {
                Key = "gog:" + id,
                Name = g!.GetValue("gameName") as string ?? Path.GetFileNameWithoutExtension(exe),
                Source = "GOG",
                Exe = exe,
                Args = g.GetValue("launchParam") as string,
                WorkDir = g.GetValue("workingDir") as string,
                Folder = g.GetValue("path") as string,
            };
        }
    }

    // ------------------------------------------------------------------ more launchers

    static readonly Dictionary<string, string> RiotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["league_of_legends"] = "League of Legends",
        ["valorant"] = "VALORANT",
        ["bacon"] = "Legends of Runeterra",
        ["teamfighttactics"] = "Teamfight Tactics",
        ["lion"] = "2XKO",
    };

    /// <summary>Riot games (League, VALORANT, TFT…): installs are listed in Riot's metadata, and start through the Riot Client.</summary>
    static IEnumerable<GameInfo> Riot()
    {
        var meta = Path.Combine(ProgramData, "Riot Games", "Metadata");
        var installs = Path.Combine(ProgramData, "Riot Games", "RiotClientInstalls.json");
        if (!Directory.Exists(meta) || !File.Exists(installs)) yield break;
        string? client = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(installs));
            foreach (var key in new[] { "rc_default", "rc_live" })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.GetString() is { } p && File.Exists(p)) { client = Norm(p); break; }
        }
        catch { }
        if (client == null) yield break;
        foreach (var dir in Directory.GetDirectories(meta))
        {
            var id = Path.GetFileName(dir); // e.g. league_of_legends.live
            var parts = id.Split('.');
            if (parts.Length != 2 || parts[0].Equals("riot_client", StringComparison.OrdinalIgnoreCase)) continue;
            var yaml = Directory.GetFiles(dir, "*.product_settings.yaml").FirstOrDefault();
            if (yaml == null) continue;
            // Riot writes <id>.ok once a game has finished installing; skip half-done downloads
            if (!File.Exists(Path.Combine(dir, id + ".ok"))) continue;
            var text = File.ReadAllText(yaml);
            var path = Regex.Match(text, "product_install_full_path:\\s*\"([^\"]+)\"").Groups[1].Value;
            if (path.Length == 0 || !Directory.Exists(path)) continue;
            var shortcut = Regex.Match(text, "shortcut_name:\\s*\"([^\"]+)\"").Groups[1].Value;
            var name = shortcut.Length > 0 ? Path.GetFileNameWithoutExtension(shortcut)
                     : RiotNames.TryGetValue(parts[0], out var n) ? n : parts[0].Replace('_', ' ');
            if (!parts[1].Equals("live", StringComparison.OrdinalIgnoreCase)) name += $" ({parts[1].ToUpperInvariant()})";
            // Riot keeps a proper icon for each game next to its metadata
            var ico = Path.Combine(dir, id + ".ico");
            var icon = File.Exists(ico) ? ico
                : Directory.GetFiles(path, "*.exe").Where(f => !Junk.IsMatch(Path.GetFileName(f))).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            yield return new GameInfo
            {
                Key = "riot:" + id,
                Name = name,
                Source = "Riot",
                Exe = client,
                Args = $"--launch-product={parts[0]} --launch-patchline={parts[1]}",
                Folder = Norm(path),
                IconExe = icon,
                LastPlayed = SafeTime(path),
            };
        }
    }

    /// <summary>Ubisoft Connect lists installs in the registry and starts them by id.</summary>
    static IEnumerable<GameInfo> Ubisoft()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (key == null) yield break;
        foreach (var id in key.GetSubKeyNames())
        {
            using var g = key.OpenSubKey(id);
            var dir = g?.GetValue("InstallDir") as string;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var cands = ExeCandidates(dir);
            yield return new GameInfo
            {
                Key = "ubisoft:" + id,
                Name = NameFor(dir, PickExe(dir, cands) ?? dir),
                Source = "Ubisoft Connect",
                LaunchUri = $"uplay://launch/{id}/0",
                Exe = PickExe(dir, cands),
                Folder = Norm(dir),
                Candidates = cands,
            };
        }
    }

    /// <summary>EA app and Battle.net games, from Windows' list of installed programs.</summary>
    static IEnumerable<GameInfo> PublisherInstalls()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var root = hive.OpenSubKey(path);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    var publisher = k?.GetValue("Publisher") as string ?? "";
                    var source = publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) ? "EA app"
                               : publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) ? "Battle.net" : null;
                    if (source == null) continue;
                    var name = k!.GetValue("DisplayName") as string ?? "";
                    var dir = (k.GetValue("InstallLocation") as string ?? "").Trim('"');
                    if (name.Length == 0 || dir.Length == 0 || !Directory.Exists(dir)) continue;
                    if (Regex.IsMatch(name, @"(?i)^(ea app|origin|battle\.net)\b|desktop app|launcher")) continue;
                    if (!seen.Add(Norm(dir))) continue;
                    var cands = ExeCandidates(dir);
                    var exe = PickExe(dir, cands);
                    if (exe == null) continue;
                    yield return new GameInfo
                    {
                        Key = "pub:" + Norm(dir).ToLowerInvariant(),
                        Name = name,
                        Source = source,
                        Exe = exe,
                        Folder = Norm(dir),
                        Candidates = cands,
                    };
                }
            }
    }

    /// <summary>Xbox / Game Pass PC games live in X:\XboxGames and start through their app id.</summary>
    static IEnumerable<GameInfo> Xbox()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            string root;
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                root = Path.Combine(d.RootDirectory.FullName, "XboxGames");
                if (!Directory.Exists(root)) continue;
            }
            catch { continue; }
            foreach (var dir in Directory.GetDirectories(root))
            {
                var manifest = Path.Combine(dir, "Content", "appxmanifest.xml");
                if (!File.Exists(manifest)) continue;
                string text;
                try { text = File.ReadAllText(manifest); } catch { continue; }
                var identity = Regex.Match(text, "<Identity[^>]*Name=\"([^\"]+)\"[^>]*Publisher=\"([^\"]+)\"");
                if (!identity.Success) identity = Regex.Match(text, "<Identity[^>]*Publisher=\"([^\"]+)\"[^>]*Name=\"([^\"]+)\"");
                var app = Regex.Match(text, "<Application[^>]*Id=\"([^\"]+)\"").Groups[1].Value;
                if (!identity.Success || app.Length == 0) continue;
                bool nameFirst = identity.Value.IndexOf("Name=", StringComparison.Ordinal) < identity.Value.IndexOf("Publisher=", StringComparison.Ordinal);
                var name = nameFirst ? identity.Groups[1].Value : identity.Groups[2].Value;
                var publisher = System.Net.WebUtility.HtmlDecode(nameFirst ? identity.Groups[2].Value : identity.Groups[1].Value);
                var display = Regex.Match(text, "<DisplayName>([^<]+)</DisplayName>").Groups[1].Value;
                var content = Path.Combine(dir, "Content");
                yield return new GameInfo
                {
                    Key = "xbox:" + name,
                    Name = display.Length > 0 && !display.StartsWith("ms-resource") ? display : Path.GetFileName(dir),
                    Source = "Xbox",
                    LaunchUri = $"shell:AppsFolder\\{name}_{PublisherId(publisher)}!{app}",
                    Folder = Norm(dir),
                    IconExe = ExeCandidates(content).FirstOrDefault(),
                };
            }
        }
    }

    /// <summary>The 13-character publisher id Windows puts in package family names.</summary>
    static string PublisherId(string publisher)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong bits = 0;
        for (int i = 0; i < 8; i++) bits = (bits << 8) | hash[i];
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var sb = new StringBuilder();
        for (int i = 0; i < 13; i++)
        {
            int shift = 64 - 5 * (i + 1);
            int index = shift >= 0 ? (int)((bits >> shift) & 31) : (int)((bits << -shift) & 31);
            sb.Append(alphabet[index]);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ fingerprints (like the NVIDIA app)

    // Files that ship with games and almost never with anything else: engines, store SDKs, upscalers
    static readonly HashSet<string> Fingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityPlayer.dll", "GameAssembly.dll", "steam_api64.dll", "steam_api.dll", "EOSSDK-Win64-Shipping.dll",
        "EOSSDK-Win32-Shipping.dll", "bink2w64.dll", "binkw64.dll", "binkw32.dll", "galaxy64.dll", "Galaxy64.dll",
        "nvngx_dlss.dll", "nvngx_dlssg.dll", "libxess.dll", "amd_fidelityfx_dx12.dll", "ffx_fsr2_api_x64.dll",
        "sl.interposer.dll", "GFSDK_Aftermath_Lib.x64.dll", "fmodstudio.dll",
    };

    static readonly Regex NotGameFolder = new(@"(?i)^(windows|windowsapps|microsoft|common files|dotnet|nvidia|intel|amd|realtek|steam|steamlibrary|epic games|launcher|riot client|ea desktop|origin|ubisoft game launcher|battle\.net|gog galaxy|unity|unity hub|wallpaper engine|brim|internet explorer|windows ?(defender|mail|media player|nt|photo viewer|portable devices|security|sidebar|powershell)|reference assemblies|msbuild|iis|inetpub|users|programdata|xboxgames|games)$");

    // Hardware/software vendors whose folders hold tools, not games
    static readonly Regex VendorFolder = new(@"(?i)^(nvidia|intel|amd|microsoft|windows|realtek|dell|hp|lenovo|asus|msi|razer|logitech|corsair|google|mozilla|adobe|oracle|java|python|git|nodejs|docker|vmware|virtualbox|jetbrains|common files|package cache|cuda)");

    static bool LooksLikeGame(string dir)
    {
        int seen = 0;
        bool Walk(string d, int depth)
        {
            if (depth > 3) return false;
            try
            {
                foreach (var f in Directory.EnumerateFiles(d))
                {
                    if (++seen > 6000) return false;
                    if (Fingerprints.Contains(Path.GetFileName(f))) return true;
                }
                foreach (var sub in Directory.EnumerateDirectories(d))
                {
                    var n = Path.GetFileName(sub);
                    if (n.StartsWith(".") || n is "node_modules" or "__pycache__") continue;
                    if (n.Equals("Engine", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(sub, "Binaries"))) return true; // Unreal
                    if (Walk(sub, depth + 1)) return true;
                }
            }
            catch { }
            return false;
        }
        return Walk(dir, 0);
    }

    /// <summary>Games installed anywhere: folders at the top of each drive and in Program Files that carry game fingerprints.</summary>
    static IEnumerable<GameInfo> FingerprintGames()
    {
        var places = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                places.AddRange(Directory.GetDirectories(d.RootDirectory.FullName));
            }
            catch { }
        }
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (Directory.Exists(pf)) try { places.AddRange(Directory.GetDirectories(pf)); } catch { }

        foreach (var place in places.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(place);
            if (name.StartsWith("$") || name.StartsWith(".") || NotGameFolder.IsMatch(name) || VendorFolder.IsMatch(name) || name.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(Path.Combine(place, "steamapps"))) continue;
            // A publisher folder ("Riot Games", "Rockstar Games") holds the games one level down
            bool hasExe = false;
            try { hasExe = Directory.EnumerateFiles(place, "*.exe").Any(); } catch { }
            var targets = hasExe ? new[] { place } : SafeDirs(place).Take(40).Prepend(place).ToArray();
            foreach (var dir in targets)
            {
                if (dir != place && NotGameFolder.IsMatch(Path.GetFileName(dir))) continue;
                if (dir == place && !hasExe) continue;
                if (!LooksLikeGame(dir)) continue;
                var cands = ExeCandidates(dir);
                var exe = PickExe(dir, cands);
                if (exe == null) continue;
                yield return new GameInfo
                {
                    Key = "found:" + Norm(dir).ToLowerInvariant(),
                    Name = NameFor(dir, exe),
                    Source = "Found on disk",
                    Exe = exe,
                    Folder = Norm(dir),
                    Candidates = cands,
                    LastPlayed = SafeTime(dir),
                };
            }
        }
    }

    static IEnumerable<string> SafeDirs(string d)
    {
        try { return Directory.GetDirectories(d); } catch { return Array.Empty<string>(); }
    }

    public static List<string> GameRoots()
    {
        if (S.GameFolders.Count > 0) return S.GameFolders.Where(Directory.Exists).ToList();
        var roots = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var g = Path.Combine(d.RootDirectory.FullName, "Games");
                if (Directory.Exists(g)) roots.Add(g);
            }
            catch { }
        }
        return roots;
    }

    static IEnumerable<GameInfo> FolderGames()
    {
        foreach (var root in GameRoots())
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); } catch { continue; }
            foreach (var dir in dirs)
            {
                if (Directory.Exists(Path.Combine(dir, "steamapps"))) continue;
                var cands = ExeCandidates(dir);
                var exe = PickExe(dir, cands);
                if (exe == null) continue;
                yield return new GameInfo
                {
                    Key = "folder:" + Norm(dir).ToLowerInvariant(),
                    Name = NameFor(dir, exe),
                    Source = "Game folder",
                    Exe = exe,
                    Folder = dir,
                    Candidates = cands,
                    LastPlayed = SafeTime(dir),
                };
            }
        }
    }

    static DateTime SafeTime(string dir)
    {
        try { return Directory.GetLastWriteTime(dir); } catch { return DateTime.MinValue; }
    }

    static readonly Regex Junk = new(@"(?i)unins|setup|crash|redist|dxsetup|directx|install|dotnet|runtime|oalinst|easyanticheat|eac_|battleye|be_service|prereq|crashreport|reporter|updater|cefprocess|physx|vcredist|touchup|uploader|helper|webhelper|elevate|launchpad_|notification|overlay|7z|unrar|python|java|node\.exe|ffmpeg|beservice|miniticket|vconsole|[-_]probe|capabilities|shadnet|checkenv|^8za|engine-assets|crashpad|crashhandler|server\.exe");

    public static List<string> ExeCandidates(string dir)
    {
        var list = new List<(string path, int depth, long size)>();
        void Walk(string d, int depth)
        {
            if (depth > 4) return;
            try
            {
                foreach (var f in Directory.GetFiles(d, "*.exe"))
                {
                    var name = Path.GetFileName(f);
                    if (Junk.IsMatch(name)) continue;
                    long size = new FileInfo(f).Length;
                    if (size < 60_000) continue;
                    list.Add((f, depth, size));
                }
                foreach (var sub in Directory.GetDirectories(d))
                {
                    var n = Path.GetFileName(sub);
                    if (n.StartsWith("_") || Regex.IsMatch(n, @"(?i)^(redist|redistributables|directx|dotnet|support|commonredist|prereqs?|installers?|__installer|tools|engine\\extras)$")) continue;
                    Walk(sub, depth + 1);
                }
            }
            catch { }
        }
        Walk(dir, 0);
        return list.OrderBy(x => x.depth).ThenByDescending(x => x.size).Select(x => x.path).Take(12).ToList();
    }

    /// <summary>Launchers first (some games must start through one), then the main exe in the game's own folder, then the biggest.</summary>
    static int Depth(string dir, string file) =>
        Path.GetDirectoryName(file)!.Length <= dir.TrimEnd('\\').Length ? 0
        : Path.GetDirectoryName(file)![(dir.TrimEnd('\\').Length + 1)..].Split('\\').Length;

    static string? PickExe(string dir, List<string> cands)
    {
        if (cands.Count == 0) return null;
        // A launcher near the top of the folder is how the game is meant to start (e.g. Bloodborne)
        var launcher = cands.Where(c => Path.GetFileName(c).Contains("launcher", StringComparison.OrdinalIgnoreCase) && Depth(dir, c) <= 1)
                            .OrderByDescending(c => new FileInfo(c).Length).FirstOrDefault();
        if (launcher != null) return launcher;
        // Otherwise the biggest exe at the shallowest level: that's the game's own start exe, not an
        // engine binary buried in Binaries\Win64 (Unreal games start through the small one at the top)
        int min = cands.Min(c => Depth(dir, c));
        return cands.Where(c => Depth(dir, c) == min && !Path.GetFileName(c).Contains("launcher", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => new FileInfo(c).Length).FirstOrDefault()
               ?? cands.OrderByDescending(c => new FileInfo(c).Length).First();
    }

    static string NameFor(string dir, string exe)
    {
        // Name it after the main game exe, not a launcher
        var main = ExeCandidates(dir).Where(c => !Path.GetFileName(c).Contains("launcher", StringComparison.OrdinalIgnoreCase))
                                     .OrderByDescending(c => new FileInfo(c).Length).FirstOrDefault();
        foreach (var source in new[] { main, exe })
        {
            if (source == null) continue;
            var n = NameFromExe(source);
            if (n != null) return n;
        }
        // The folder just inside usually carries the clean name ("Baldur's Gate 3", "The Wolf Among Us")
        var rel = (main ?? exe).Length > dir.TrimEnd('\\').Length ? (main ?? exe)[(dir.TrimEnd('\\').Length + 1)..] : "";
        var first = rel.Split('\\')[0];
        if (rel.Contains('\\') && first.Length >= 3 && !Regex.IsMatch(first, @"(?i)^(bin|binaries|game|x64|win64|win32|app|data)$"))
            return first;
        var name = Path.GetFileName(dir.TrimEnd('\\'));
        name = Regex.Replace(name, @"\[.*?\]|\(.*?\)", "");
        name = Regex.Replace(name, @"[-_.]+", " ").Trim();
        return name.Length > 0 ? name : Path.GetFileNameWithoutExtension(exe);
    }

    static string? NameFromExe(string exe)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            foreach (var candidate in new[] { v.ProductName, v.FileDescription })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var n = Regex.Replace(candidate!, @"(?i)\s*(launcher|game|client|\(64-bit\)|x64|shipping)\s*$", "").Trim();
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase)) continue; // e.g. "bg3.exe" says nothing
                if (n.Length >= 3 && n.Length <= 60 && !Regex.IsMatch(n, @"(?i)^(unreal|unity|bootstrap|launcher|application|game)$")) return n;
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ watching

    static void Watch()
    {
        if (watchers.Count > 0) return;
        debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        debounce.Tick += async (_, _) => { debounce.Stop(); await Refresh(); };
        void Add(string folder, string filter, bool subdirs, NotifyFilters what)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                var w = new FileSystemWatcher(folder, filter) { IncludeSubdirectories = subdirs, NotifyFilter = what };
                FileSystemEventHandler h = (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => { debounce!.Stop(); debounce.Start(); }));
                w.Created += h;
                w.Deleted += h;
                w.Changed += h;
                w.Renamed += (s, e) => h(s, e);
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch (Exception ex) { Log.Error("games watch " + folder, ex); }
        }
        foreach (var lib in SteamLibraries()) Add(Path.Combine(lib, "steamapps"), "appmanifest_*.acf", false, NotifyFilters.FileName);
        var root = SteamRoot();
        if (root != null) Add(Path.Combine(root, "userdata"), "shortcuts.vdf", true, NotifyFilters.FileName | NotifyFilters.LastWrite);
        Add(EpicManifests, "*.item", false, NotifyFilters.FileName);
        Add(Path.Combine(ProgramData, "Riot Games", "Metadata"), "*", true, NotifyFilters.DirectoryName | NotifyFilters.FileName);
        foreach (var r in GameRoots()) Add(r, "*", false, NotifyFilters.DirectoryName);
    }

    public static void Rewatch()
    {
        foreach (var w in watchers) w.Dispose();
        watchers.Clear();
        _ = Refresh();
    }

    // ------------------------------------------------------------------ launching

    public static void Launch(GameInfo g)
    {
        var o = S.GameOverrides.FirstOrDefault(x => x.Key == g.Key);
        if (!string.IsNullOrWhiteSpace(o?.RendererArgs))
        {
            LaunchWith(g, new LaunchOption("arg:" + o!.RendererArgs, "", null, o.RendererArgs, g.SteamAppId != null));
            return;
        }
        try
        {
            if (g.Exe != null && (g.LaunchUri == null || HasExeOverride(g)))
            {
                var psi = new ProcessStartInfo(g.Exe) { UseShellExecute = true, WorkingDirectory = g.WorkDir ?? Path.GetDirectoryName(g.Exe) ?? "" };
                if (!string.IsNullOrWhiteSpace(g.Args)) psi.Arguments = g.Args;
                Process.Start(psi);
            }
            else if (g.LaunchUri != null) Launcher.Open(g.LaunchUri);
            S.GameLaunches[g.Key] = DateTime.Now;
            g.LastPlayed = DateTime.Now;
            SettingsStore.Save();
        }
        catch (Exception ex) { Log.Error("launch game " + g.Name, ex); }
    }

    /// <summary>Start the game one particular way (Steam, an exe, or a renderer switch).</summary>
    public static void LaunchWith(GameInfo g, LaunchOption opt)
    {
        try
        {
            if (opt.Id == "steam" && g.SteamAppId != null) { Launcher.Open("steam://rungameid/" + g.SteamAppId); MarkPlayed(g); return; }
            if (opt.Exe != null) { LaunchExe(g, opt.Exe); return; }
            var args = string.Join(" ", new[] { g.Args, opt.Args }.Where(a => !string.IsNullOrWhiteSpace(a)));
            if (opt.ViaSteam && g.SteamAppId != null)
                Launcher.Open($"steam://run/{g.SteamAppId}//{Uri.EscapeDataString(args)}/"); // Steam passes these to the game
            else if (g.Exe != null)
            {
                var psi = new ProcessStartInfo(g.Exe) { UseShellExecute = true, WorkingDirectory = g.WorkDir ?? Path.GetDirectoryName(g.Exe) ?? "", Arguments = args };
                Process.Start(psi);
            }
            else if (g.LaunchUri != null) Launcher.Open(g.LaunchUri);
            MarkPlayed(g);
        }
        catch (Exception ex) { Log.Error("launch game " + g.Name, ex); }
    }

    /// <summary>Start the game with one particular exe, just this once (from "Ask every time").</summary>
    public static void LaunchExe(GameInfo g, string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            if (!string.IsNullOrWhiteSpace(g.Args)) psi.Arguments = g.Args;
            Process.Start(psi);
            MarkPlayed(g);
        }
        catch (Exception ex) { Log.Error("launch game " + g.Name, ex); }
    }

    public static void MarkPlayed(GameInfo g)
    {
        S.GameLaunches[g.Key] = DateTime.Now;
        g.LastPlayed = DateTime.Now;
        SettingsStore.Save();
    }

    static bool HasExeOverride(GameInfo g) => S.GameOverrides.Any(o => o.Key == g.Key && !string.IsNullOrWhiteSpace(o.Exe));

    public static GameOverride Override(string key)
    {
        var o = S.GameOverrides.FirstOrDefault(x => x.Key == key);
        if (o == null) { o = new GameOverride { Key = key }; S.GameOverrides.Add(o); }
        return o;
    }

    // ------------------------------------------------------------------ cover art

    public static async Task<ImageSource?> Cover(GameInfo g, int decodeWidth = 160)
    {
        if (covers.TryGetValue(g.Key, out var hit)) return hit;
        string? path = await Task.Run(() => LocalCover(g));
        if (path == null && S.GameCovers) path = await DownloadCover(g);
        ImageSource? img = null;
        if (path != null)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
            catch { }
        }
        covers[g.Key] = img;
        return img;
    }

    static string? LocalCover(GameInfo g)
    {
        var custom = S.GameOverrides.FirstOrDefault(o => o.Key == g.Key)?.Cover;
        if (custom != null && File.Exists(custom)) return custom;
        if (g.ShortcutAppId is uint sid && g.GridFolder != null && Directory.Exists(g.GridFolder))
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
            {
                var p = Path.Combine(g.GridFolder, sid + "p" + ext);
                if (File.Exists(p)) return p;
            }
        if (g.SteamAppId != null && SteamRoot() is { } root)
        {
            var cache = Path.Combine(root, "appcache", "librarycache");
            var flat = Path.Combine(cache, g.SteamAppId + "_library_600x900.jpg");
            if (File.Exists(flat)) return flat;
            var dir = Path.Combine(cache, g.SteamAppId);
            if (Directory.Exists(dir))
            {
                // Older Steam: library_600x900.jpg; newer Steam: library_capsule.jpg in a hashed sub-folder
                foreach (var pattern in new[] { "library_600x900.jpg", "library_capsule.jpg", "library_600x900*.jpg", "library_capsule*.jpg" })
                {
                    var found = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) return found;
                }
            }
        }
        var cached = Path.Combine(CoverFolder, SafeName(g.Key) + ".jpg");
        return File.Exists(cached) ? cached : null;
    }

    static string SafeName(string s) => Regex.Replace(s, @"[^\w\-]+", "_");

    static readonly HashSet<string> missing = new();

    static async Task<string?> DownloadCover(GameInfo g)
    {
        if (missing.Contains(g.Key)) return null;
        try
        {
            string? appId = g.SteamAppId;
            if (appId == null)
            {
                // Look the title up in the public Steam store to borrow its cover art.
                // Try the game's name, then the folder it lives in ("Fables" is in "The Wolf Among Us").
                foreach (var term in SearchNames(g))
                {
                    var json = await http.GetStringAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=US");
                    using var doc = JsonDocument.Parse(json);
                    foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
                    {
                        var name = it.GetProperty("name").GetString() ?? "";
                        if (Similar(name, term)) { appId = it.GetProperty("id").GetInt64().ToString(); break; }
                    }
                    if (appId != null) break;
                }
            }
            byte[]? bytes = appId != null ? await SteamCapsule(appId) : null;
            // Not on Steam (League, Bloodborne…): try SteamGridDB if you've added a key
            bytes ??= await SteamGridCover(g);
            if (bytes == null) { missing.Add(g.Key); return null; }
            Directory.CreateDirectory(CoverFolder);
            var path = Path.Combine(CoverFolder, SafeName(g.Key) + ".jpg");
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            missing.Add(g.Key);
            return null;
        }
    }

    static IEnumerable<string> SearchNames(GameInfo g)
    {
        var names = new List<string> { g.Name };
        if (g.Exe != null)
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(g.Exe) ?? "");
            if (!Regex.IsMatch(parent, @"(?i)^(bin|binaries|win64|x64|game)$")) names.Add(parent);
        }
        if (g.Folder != null) names.Add(Regex.Replace(Regex.Replace(Path.GetFileName(g.Folder.TrimEnd('\\')), @"\[.*?\]|\(.*?\)", ""), @"[-_.]+", " ").Trim());
        return names.Where(n => n.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).Take(3);
    }

    /// <summary>The portrait cover from Steam's store service, which knows the exact (hashed) file for every game.</summary>
    static async Task<byte[]?> SteamCapsule(string appId)
    {
        try
        {
            var input = Uri.EscapeDataString($"{{\"ids\":[{{\"appid\":{appId}}}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\"}},\"data_request\":{{\"include_assets\":true}}}}");
            var json = await http.GetStringAsync($"https://api.steampowered.com/IStoreBrowseService/GetItems/v1?input_json={input}");
            using var doc = JsonDocument.Parse(json);
            var item = doc.RootElement.GetProperty("response").GetProperty("store_items")[0];
            if (item.TryGetProperty("assets", out var assets) &&
                assets.TryGetProperty("asset_url_format", out var fmt) &&
                assets.TryGetProperty("library_capsule", out var cap))
            {
                var url = "https://shared.akamai.steamstatic.com/store_item_assets/" + fmt.GetString()!.Replace("${FILENAME}", cap.GetString());
                return await http.GetByteArrayAsync(url);
            }
        }
        catch { }
        try { return await http.GetByteArrayAsync($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg"); }
        catch { return null; }
    }

    /// <summary>SteamGridDB: community cover art for almost every game, including ones not on Steam. Needs a free key.</summary>
    /// <summary>For `--keys-test`: does the cover service answer (with whichever key Brim would use)?</summary>
    public static async Task<int> CoverBytesFor(string name) => (await SteamGridCover(new GameInfo { Key = "test", Name = name }))?.Length ?? 0;

    static async Task<byte[]?> SteamGridCover(GameInfo g)
    {
        var key = ApiKeys.SteamGrid;
        if (key.Length == 0) return null;
        try
        {
            foreach (var term in SearchNames(g))
            {
                using var search = new HttpRequestMessage(HttpMethod.Get, $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(term)}");
                search.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                using var sres = await http.SendAsync(search);
                if (!sres.IsSuccessStatusCode) continue;
                using var sdoc = JsonDocument.Parse(await sres.Content.ReadAsStringAsync());
                var data = sdoc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) continue;
                var id = data[0].GetProperty("id").GetInt64();
                using var grids = new HttpRequestMessage(HttpMethod.Get, $"https://www.steamgriddb.com/api/v2/grids/game/{id}?dimensions=600x900&types=static");
                grids.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                using var gres = await http.SendAsync(grids);
                if (!gres.IsSuccessStatusCode) continue;
                using var gdoc = JsonDocument.Parse(await gres.Content.ReadAsStringAsync());
                var list = gdoc.RootElement.GetProperty("data");
                if (list.GetArrayLength() == 0) continue;
                return await http.GetByteArrayAsync(list[0].GetProperty("url").GetString());
            }
        }
        catch (Exception ex) { Log.Error("steamgriddb", ex); }
        return null;
    }

    /// <summary>Forget failed cover lookups so they're tried again (after adding a key).</summary>
    public static void RetryCovers()
    {
        missing.Clear();
        foreach (var k in covers.Where(c => c.Value == null).Select(c => c.Key).ToList()) covers.Remove(k);
        Changed?.Invoke();
    }

    static bool Similar(string a, string b)
    {
        string N(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");
        var x = N(a);
        var y = N(b);
        return x.Length > 0 && y.Length > 0 && (x == y || (y.Length >= 5 && x.StartsWith(y)) || (x.Length >= 5 && y.StartsWith(x)));
    }
}
