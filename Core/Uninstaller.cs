using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Brim.Core;

public sealed class InstalledProgram
{
    public string Key { get; init; } = "";          // registry path, unique
    public string KeyName { get; init; } = "";      // last part of the key ("Uplay Install 635", "Steam App 1086940"…)
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Installed { get; init; }
    public string? Location { get; init; }
    public string Uninstall { get; init; } = "";
    public string? Icon { get; init; }
    public long SizeKb { get; init; }
    public string SizeText => SizeKb <= 0 ? "" : SizeKb > 1024 * 1024 ? $"{SizeKb / 1048576.0:0.0} GB" : $"{SizeKb / 1024.0:0} MB";
    /// <summary>Program (Windows' installed list), StoreApp (Microsoft Store / Xbox package) or GameFolder
    /// (a game Windows doesn't list, e.g. from a launcher that doesn't register its games)</summary>
    public string Kind { get; init; } = "Program";
    public string? PackageFullName { get; init; }
    public string KindText => Kind switch { "StoreApp" => "Store app", "GameFolder" => "Game folder", _ => "" };
}

/// <summary>Why a program is suggested alongside the one you're removing, and whether it's safe to.</summary>
public sealed record RelatedProgram(InstalledProgram Program, string Reason, string? StillUsedBy);

/// <summary>
/// Removes apps and games with the uninstallers they shipped with (the same ones Settings › Apps runs), and can
/// take what came with them along too: the launcher (Ubisoft Connect with Rainbow Six Siege), companion tools from
/// the same publisher installed at the same time. Shared frameworks (DirectX, Visual C++, .NET, Vulkan…) are
/// never offered, since other programs depend on them.
/// </summary>
public static class Uninstaller
{
    /// <summary>Runtimes and frameworks other software relies on: never removed with an app.</summary>
    static readonly Regex Framework = new(@"(?i)visual c\+\+|redistributable|\bredist\b|directx|\.net (framework|runtime|desktop|core)|windows desktop runtime|asp\.net|microsoft \.net|windows (sdk|app runtime|software development kit)|winrt|vulkan|openal|physx|xna framework|webview2|gameinput|update health|windows driver|visual studio .*(runtime|tools for applications)|microsoft (edge|odbc|ole db|access database engine)|java\b|jre\b|jdk\b|python \d|python launcher|easyanticheat|easy anti-cheat|battleye|vanguard|nvidia (graphics|physx|hd audio|frameview|usbc)|amd (chipset|software)|intel\(r\)|realtek|dolby|microsoft gameinput|msvc|universal crt|kb\d{6,}");

    /// <summary>Game launchers: the key-name / publisher clues that tie a game to its launcher.</summary>
    static readonly (Regex launcher, Regex game)[] Launchers =
    {
        (new(@"(?i)^ubisoft connect|^uplay"), new(@"(?i)^uplay install|ubisoft")),
        (new(@"(?i)^steam$"), new(@"(?i)^steam app \d+|steam://uninstall")),
        (new(@"(?i)^ea app$|^origin$|^ea desktop"), new(@"(?i)electronic arts|\bea\b|origin")),
        (new(@"(?i)^epic games launcher"), new(@"(?i)epic games")),
        (new(@"(?i)^battle\.net"), new(@"(?i)blizzard|battle\.net")),
        (new(@"(?i)^rockstar games launcher"), new(@"(?i)rockstar")),
        (new(@"(?i)^gog galaxy"), new(@"(?i)gog\.com|gog sp")),
        (new(@"(?i)^riot client"), new(@"(?i)riot games")),
        (new(@"(?i)^amazon games"), new(@"(?i)amazon game")),
        (new(@"(?i)^wargaming\.net game center"), new(@"(?i)wargaming")),
        (new(@"(?i)^hoyoplay"), new(@"(?i)cognosphere|hoyoverse|mihoyo")),
    };

    public static List<InstalledProgram> List()
    {
        var list = new List<InstalledProgram>();
        void Read(RegistryKey root, string path)
        {
            try
            {
                using var k = root.OpenSubKey(path);
                if (k == null) return;
                foreach (var sub in k.GetSubKeyNames())
                {
                    try
                    {
                        using var e = k.OpenSubKey(sub);
                        if (e == null) continue;
                        var name = (e.GetValue("DisplayName") as string)?.Trim();
                        var un = (e.GetValue("UninstallString") as string)?.Trim();
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(un)) continue;
                        if (e.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (e.GetValue("ParentKeyName") != null) continue; // updates of something else
                        if (e.GetValue("ReleaseType") is string rt && Regex.IsMatch(rt, "(?i)update|hotfix")) continue;
                        DateTime? date = null;
                        if (e.GetValue("InstallDate") is string d && DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) date = dt;
                        var loc = (e.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                        list.Add(new InstalledProgram
                        {
                            Key = (root == Registry.CurrentUser ? "HKCU\\" : "HKLM\\") + path + "\\" + sub,
                            KeyName = sub,
                            Name = name,
                            Publisher = (e.GetValue("Publisher") as string)?.Trim() ?? "",
                            Version = e.GetValue("DisplayVersion") as string ?? "",
                            Installed = date,
                            Location = string.IsNullOrEmpty(loc) ? null : loc.TrimEnd('\\'),
                            Uninstall = un,
                            Icon = (e.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"'),
                            SizeKb = e.GetValue("EstimatedSize") is int kb ? kb : 0,
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }
        const string U = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        Read(Registry.LocalMachine, U);
        Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        Read(Registry.CurrentUser, U);
        return list.GroupBy(p => p.Name + "|" + p.Version, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                   .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Everything that can be removed on this PC, read fresh each time: Windows' installed programs, Microsoft Store and
    /// Xbox apps, and games Brim found that Windows doesn't list (launchers like AnkerGames or Epic don't register theirs).
    /// </summary>
    public static List<InstalledProgram> Everything()
    {
        var programs = List();
        var result = new List<InstalledProgram>(programs);
        try { result.AddRange(StoreApps()); } catch (Exception ex) { Log.Error("store apps", ex); }
        var gameFolders = GameLibrary.Current.Where(x => x.Folder != null).Select(x => x.Folder!.TrimEnd('\\')).ToList();
        foreach (var g in GameLibrary.Current)
        {
            if (g.Folder == null || !Directory.Exists(g.Folder) || ForGame(g, programs) != null) continue;
            if (g.Source == "Xbox") continue; // listed as a Store app
            if (!SafeToRecycle(g.Folder, gameFolders)) continue;
            result.Add(new InstalledProgram
            {
                Key = "folder:" + g.Folder.ToLowerInvariant(),
                KeyName = g.Key,
                Name = g.Name,
                Publisher = "",
                Location = g.Folder.TrimEnd('\\'),
                Kind = "GameFolder",
                Installed = SafeDate(g.Folder),
            });
        }
        return result.GroupBy(p => p.Key).Select(x => x.First()).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static DateTime? SafeDate(string dir) { try { return Directory.GetCreationTime(dir); } catch { return null; } }

    /// <summary>
    /// A game's own folder, never something bigger: not a drive, not a Windows / Program Files / user folder (or a
    /// folder just above one), and not a folder that holds other games.
    /// </summary>
    static bool SafeToRecycle(string folder, List<string> gameFolders)
    {
        var f = folder.TrimEnd('\\');
        if (f.Length <= 3 || !Path.IsPathRooted(f)) return false;
        var special = new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
        }.Select(s => Environment.GetFolderPath(s).TrimEnd('\\')).Where(s => s.Length > 0).ToList();
        special.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        foreach (var s in special)
            if (string.Equals(f, s, StringComparison.OrdinalIgnoreCase) || s.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (Regex.IsMatch(Path.GetFileName(f), @"(?i)^(games?|steamlibrary|steamapps|common|program files.*|xboxgames|epic games|gog games|windowsapps)$")) return false;
        // Holds another game's folder → it's a library, not a game
        if (gameFolders.Any(o => o.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    /// <summary>The entry to remove for a game in the Games tab: Windows' own, or (if Windows doesn't list it) its folder.</summary>
    public static InstalledProgram? ForGameAny(GameInfo g, List<InstalledProgram> everything)
    {
        var registered = everything.Where(p => p.Kind == "Program").ToList();
        return ForGame(g, registered)
               ?? (g.Folder != null ? everything.FirstOrDefault(p => p.Kind == "GameFolder" && p.Key == "folder:" + g.Folder.ToLowerInvariant()) : null)
               ?? everything.FirstOrDefault(p => p.Kind == "StoreApp" && g.Folder != null && p.Location != null &&
                                                 (string.Equals(p.Location.TrimEnd('\\'), g.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || IsInside(g.Folder, p.Location)));
    }

    /// <summary>Microsoft Store and Xbox apps you can remove (not frameworks, not parts of Windows).</summary>
    static IEnumerable<InstalledProgram> StoreApps()
    {
        var pm = new Windows.Management.Deployment.PackageManager();
        foreach (var pkg in pm.FindPackagesForUser(""))
        {
            InstalledProgram? p = null;
            try
            {
                if (pkg.IsFramework || pkg.IsResourcePackage || pkg.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System) continue;
                var name = pkg.DisplayName;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) continue;
                string? loc = null;
                try { loc = pkg.InstalledLocation?.Path; } catch { }
                DateTime? installed = null;
                try { installed = pkg.InstalledDate.LocalDateTime; } catch { }
                p = new InstalledProgram
                {
                    Key = "pkg:" + pkg.Id.FullName,
                    KeyName = pkg.Id.FamilyName,
                    Name = name,
                    Publisher = pkg.PublisherDisplayName ?? "",
                    Version = $"{pkg.Id.Version.Major}.{pkg.Id.Version.Minor}.{pkg.Id.Version.Build}",
                    Installed = installed,
                    Location = loc,
                    Kind = "StoreApp",
                    PackageFullName = pkg.Id.FullName,
                };
            }
            catch { }
            if (p != null) yield return p;
        }
    }

    public static bool IsFramework(InstalledProgram p) => Framework.IsMatch(p.Name);

    static string Company(string publisher)
    {
        var p = Regex.Replace(publisher.ToLowerInvariant(), @"[,.]|\b(inc|ltd|llc|gmbh|corp|corporation|co|sa|s\.a|entertainment|interactive|studios?|games|software|technologies|limited)\b", " ");
        return Regex.Replace(p, @"\s+", " ").Trim();
    }

    static bool IsInside(string? child, string? parent) =>
        child != null && parent != null && parent.Length > 3 &&
        child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    static int LauncherIndex(InstalledProgram p) => Array.FindIndex(Launchers, l => l.launcher.IsMatch(p.Name));
    static bool BelongsTo(InstalledProgram p, int launcher) =>
        launcher >= 0 && LauncherIndex(p) < 0 && (Launchers[launcher].game.IsMatch(p.KeyName) || Launchers[launcher].game.IsMatch(p.Publisher) || Launchers[launcher].game.IsMatch(p.Uninstall));

    // ---------- launchers nobody told Brim about: recognised by what they're called and where their games live

    // Only clear launcher names: "client", "app", "hub" alone would catch NVIDIA App, GitHub Desktop and the like
    static readonly Regex LauncherWords = new(@"(?i)\b(launcher|game ?center|games? client|game ?store)\b");
    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    { "launcher", "games", "game", "client", "connect", "software", "studio", "studios", "entertainment", "interactive", "microsoft", "windows", "corporation", "limited", "center", "store", "desktop" };

    static string Squeeze(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

    /// <summary>Distinctive words of a launcher's name and publisher ("anker", "ankergames"), to find its games by.</summary>
    static IEnumerable<string> Marks(InstalledProgram launcher)
    {
        var words = Regex.Split((launcher.Name + " " + launcher.Publisher).ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length >= 4 && !Generic.Contains(w)).ToList();
        var pub = Squeeze(launcher.Publisher);
        if (pub.Length >= 5 && !Generic.Contains(pub)) words.Add(pub);
        return words.Distinct();
    }

    static bool LooksLikeLauncher(InstalledProgram p) => LauncherIndex(p) >= 0 || (p.Kind == "Program" && LauncherWords.IsMatch(p.Name) && !IsCompanion(p));

    /// <summary>
    /// Was this game installed by that launcher? Its uninstaller runs from the launcher's folder, it lives inside the
    /// launcher's folder, or the launcher's distinctive name is in the game's folder or registry entry.
    /// </summary>
    static bool CameFrom(InstalledProgram game, InstalledProgram launcher)
    {
        if (game.Key == launcher.Key || LooksLikeLauncher(game)) return false;
        int li = LauncherIndex(launcher);
        if (li >= 0 && BelongsTo(game, li)) return true;
        if (launcher.Location is { Length: > 3 } lloc)
        {
            if (game.Uninstall.Contains(lloc, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsInside(game.Location, lloc)) return true;
        }
        // Whole words only ("steam" must not match "MSTeams"); a long mark may start a word ("ankergames…")
        var words = Regex.Split((game.Location + " " + game.KeyName + " " + game.Uninstall + " " + game.Publisher).ToLowerInvariant(), "[^a-z0-9]+")
                         .Where(w => w.Length >= 4).ToHashSet();
        return Marks(launcher).Any(m => words.Contains(m) || (m.Length >= 6 && words.Any(w => w.StartsWith(m))));
    }

    /// <summary>Not a game or app of its own but a part of one (a publisher's anti-cheat, a helper service).</summary>
    static readonly Regex Companion = new(@"(?i)anti-?cheat|sentinel|\bhelper\b|\bservice\b|add-?in|overlay|crash ?report");

    static bool IsCompanion(InstalledProgram p) => Companion.IsMatch(p.Name) || Companion.IsMatch(p.KeyName);

    /// <summary>
    /// Suggestions that are safe to tick for you: the launcher when nothing else needs it, and parts that came with
    /// it. "Same publisher, same day" guesses are never ticked for you.
    /// </summary>
    public static bool Recommended(RelatedProgram r) =>
        r.StillUsedBy == null && !r.Reason.StartsWith("Also from") &&
        // A launcher recognised only by its name might have other games Brim can't tie to it: let you decide
        !(r.Reason.StartsWith("The launcher") && LauncherIndex(r.Program) < 0);

    /// <summary>What came with this program and can go with it. Shared frameworks are left out on purpose.</summary>
    public static List<RelatedProgram> Related(InstalledProgram target, List<InstalledProgram> all)
    {
        var result = new List<RelatedProgram>();
        var company = Company(target.Publisher);
        // Big publishers (Microsoft, Google…) install all sorts of unrelated things on the same day
        bool bigPublisher = company.Length < 3 || all.Count(p => Company(p.Publisher) == company) > 4;
        foreach (var p in all)
        {
            if (p.Key == target.Key || IsFramework(p)) continue;
            string? reason = null;
            int li = LauncherIndex(p);
            bool launcher = LooksLikeLauncher(p) && CameFrom(target, p);
            if (launcher) reason = "The launcher it was installed with";
            else if (IsInside(p.Location, target.Location)) reason = "Installed inside its folder";
            // A publisher's anti-cheat or helper that came with the game (Ubisoft Anti-Cheat with Siege)
            else if (!IsCompanion(target) && IsCompanion(p) && li < 0 &&
                     Enumerable.Range(0, Launchers.Length).Any(i => !Launchers[i].launcher.IsMatch("Steam") && BelongsTo(p, i) && BelongsTo(target, i))) // not via Steam: it hosts everything
                reason = "Came with it (part of the publisher's setup)";
            // Same publisher, same day: companion tools. Never another game of the same launcher, and only between
            // ordinary programs (Store apps all live side by side in WindowsApps; game folders have no publisher).
            else if (!bigPublisher && target.Kind == "Program" && p.Kind == "Program" && Company(p.Publisher) == company && LauncherIndex(target) < 0 &&
                     !Enumerable.Range(0, Launchers.Length).Any(i => BelongsTo(p, i)))
            {
                bool sameDay = p.Installed != null && target.Installed != null && Math.Abs((p.Installed.Value - target.Installed.Value).TotalDays) <= 1;
                bool sameFolder = p.Location != null && target.Location != null &&
                                  string.Equals(Path.GetDirectoryName(p.Location), Path.GetDirectoryName(target.Location), StringComparison.OrdinalIgnoreCase);
                if (sameDay || sameFolder) reason = sameDay ? $"Also from {target.Publisher}, installed the same day" : $"Also from {target.Publisher}, in the same folder";
            }
            if (reason == null) continue;

            // Is something else still using it? (other games of that launcher, other apps from that publisher)
            string? usedBy = null;
            if (launcher)
            {
                // Parts of a game (its anti-cheat…) don't count as other things needing the launcher
                var others = all.Where(o => o.Key != target.Key && o.Key != p.Key && !IsCompanion(o) && !IsFramework(o) && CameFrom(o, p)).Select(o => o.Name).Take(3).ToList();
                if (others.Count > 0) usedBy = string.Join(", ", others);
            }
            result.Add(new RelatedProgram(p, reason, usedBy));
        }
        return result;
    }

    /// <summary>The installed program for a game in the Games tab, if Windows lists one.</summary>
    public static InstalledProgram? ForGame(GameInfo g, List<InstalledProgram> all)
    {
        if (g.SteamAppId != null)
            return all.FirstOrDefault(p => p.KeyName == "Steam App " + g.SteamAppId);
        if (g.LaunchUri?.StartsWith("uplay://launch/", StringComparison.OrdinalIgnoreCase) == true)
        {
            var id = g.LaunchUri.Split('/')[3];
            var hit = all.FirstOrDefault(p => p.KeyName == "Uplay Install " + id);
            if (hit != null) return hit;
        }
        if (g.Folder != null)
        {
            var hit = all.FirstOrDefault(p => p.Location != null && (string.Equals(p.Location, g.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || IsInside(g.Folder, p.Location) && p.Location.Length > 12));
            if (hit != null) return hit;
        }
        string Simple(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");
        var name = Simple(g.Name);
        return name.Length < 4 ? null : all.FirstOrDefault(p => Simple(p.Name) == name) ?? all.FirstOrDefault(p => Simple(p.Name).StartsWith(name));
    }

    static bool StillInstalled(InstalledProgram p)
    {
        switch (p.Kind)
        {
            case "GameFolder":
                return p.Location != null && Directory.Exists(p.Location);
            case "StoreApp":
                try { return new Windows.Management.Deployment.PackageManager().FindPackageForUser("", p.PackageFullName) != null; }
                catch { return false; }
            default:
                var root = p.Key.StartsWith("HKCU") ? Registry.CurrentUser : Registry.LocalMachine;
                using (var k = root.OpenSubKey(p.Key[5..])) return k != null;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
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

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>
    /// A game Windows doesn't list: send its folder to the Recycle Bin with Windows' own confirmation and progress
    /// (Windows offers to delete it outright if it's too big for the bin). Nothing is removed without that prompt.
    /// </summary>
    static bool RecycleFolder(string folder)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 0x3,                 // FO_DELETE
            pFrom = folder.TrimEnd('\\') + "\0\0",
            fFlags = 0x40,               // FOF_ALLOWUNDO (Recycle Bin); Windows asks to confirm
            lpszProgressTitle = "Removing game",
        };
        int r = SHFileOperation(ref op);
        return r == 0 && op.fAnyOperationsAborted == 0;
    }

    /// <summary>
    /// Run each program's own uninstaller, one after another (the game before its launcher, which some games need to
    /// uninstall). Waits until Windows no longer lists it, since many uninstallers hand off to a copy of themselves.
    /// </summary>
    public static async Task<List<(InstalledProgram p, bool removed, string note)>> Run(IEnumerable<InstalledProgram> programs, IProgress<string> progress, CancellationToken stop)
    {
        var results = new List<(InstalledProgram, bool, string)>();
        foreach (var p in programs)
        {
            if (stop.IsCancellationRequested) { results.Add((p, false, "skipped")); continue; }
            progress.Report($"Removing {p.Name}… follow its uninstaller's window if one opens.");
            Log.Note("uninstall", $"start {p.Name} ({p.Key})");
            try
            {
                if (p.Kind == "StoreApp")
                {
                    progress.Report($"Removing {p.Name}…");
                    var res = await new Windows.Management.Deployment.PackageManager().RemovePackageAsync(p.PackageFullName!);
                    bool ok = res.IsRegistered == false && string.IsNullOrEmpty(res.ErrorText) || !StillInstalled(p);
                    results.Add((p, ok, ok ? "removed" : "Windows said: " + res.ErrorText));
                    Log.Note("uninstall", $"{p.Name}: {(ok ? "removed" : res.ErrorText)}");
                    continue;
                }
                if (p.Kind == "GameFolder")
                {
                    progress.Report($"Removing {p.Name}… confirm in Windows' window.");
                    bool ok = RecycleFolder(p.Location!) && !StillInstalled(p);
                    results.Add((p, ok, ok ? "moved to the Recycle Bin" : "kept (cancelled or in use)"));
                    Log.Note("uninstall", $"{p.Name} folder: {(ok ? "recycled" : "kept")}");
                    continue;
                }
                Start(p);
                var until = DateTime.Now.AddMinutes(20);
                while (StillInstalled(p) && DateTime.Now < until && !stop.IsCancellationRequested)
                    await Task.Delay(1500);
                bool gone = !StillInstalled(p);
                results.Add((p, gone, gone ? "removed" : stop.IsCancellationRequested ? "stopped waiting" : "still installed"));
                Log.Note("uninstall", $"{p.Name}: {(gone ? "removed" : "still installed")}");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                results.Add((p, false, "cancelled"));
            }
            catch (Exception ex)
            {
                Log.Error("uninstall " + p.Name, ex);
                results.Add((p, false, "couldn't start its uninstaller"));
            }
        }
        return results;
    }

    static void Start(InstalledProgram p)
    {
        var cmd = Environment.ExpandEnvironmentVariables(p.Uninstall);
        // Windows Installer packages: /I (repair/change) means /X (remove) here
        var msi = Regex.Match(cmd, @"(?i)msiexec(\.exe)?\s+/[ix]\s*(\{[0-9a-f\-]+\})");
        string exe, args;
        if (msi.Success) { exe = "msiexec.exe"; args = "/x " + msi.Groups[2].Value; }
        else if (cmd.StartsWith("\"")) { int end = cmd.IndexOf('"', 1); exe = cmd[1..end]; args = cmd[(end + 1)..].Trim(); }
        else
        {
            // Unquoted paths with spaces: take the longest prefix that is a real file
            exe = cmd; args = "";
            var parts = cmd.Split(' ');
            for (int i = parts.Length; i > 0; i--)
            {
                var candidate = string.Join(" ", parts.Take(i));
                if (File.Exists(candidate)) { exe = candidate; args = string.Join(" ", parts.Skip(i)); break; }
            }
        }
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
        try { Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740) // needs admin
        {
            psi.Verb = "runas";
            Process.Start(psi);
        }
    }
}
