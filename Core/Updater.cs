using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Brim.Core;

/// <summary>
/// Keeps Brim up to date from its GitHub releases: checks now and then, downloads a newer Setup in the background,
/// checks it against the SHA-256 in the release notes, then offers "restart to update". Only the installed copy
/// updates itself. Nothing about the PC or the person is sent: just a plain request for the latest release.
/// </summary>
public static class Updater
{
    public const string Repo = "mmohannad47-code/brim";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim", "Updates");
    static readonly HttpClient http = MakeClient();
    static readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromHours(6) };
    static bool busy;

    public enum State { Idle, Checking, UpToDate, Downloading, Ready, Failed }
    public static State Now { get; private set; } = State.Idle;
    public static string? NewVersion { get; private set; }
    public static string? ReadyFile { get; private set; }
    public static string? Problem { get; private set; }
    public static double Progress { get; private set; }
    public static DateTime LastChecked { get; private set; }
    /// <summary>For `--update-test`: act as if this older version were installed.</summary>
    internal static string? PretendVersion;
    internal static string? ExpectedHash { get; private set; }

    /// <summary>A newer version finished downloading and can be installed.</summary>
    public static event Action? Ready;
    public static event Action? Changed;
    /// <summary>A quiet moment came with an update ready: restart into it now (like other apps updating themselves).</summary>
    public static event Action? InstallNow;

    /// <summary>How long nobody has to touch the PC before Brim restarts into an update.</summary>
    internal static TimeSpan QuietFor = TimeSpan.FromMinutes(3);
    static readonly System.Windows.Threading.DispatcherTimer quiet = new() { Interval = TimeSpan.FromMinutes(1) };

    /// <summary>Away from the PC, and no game or video fullscreen anywhere (a restart would never interrupt you).</summary>
    internal static bool QuietMoment(out string why)
    {
        if (WindowTracker.FullscreenMonitors().Count > 0) { why = "something is fullscreen"; return false; }
        // Just started (the PC was turned on): update straight away, before you get going
        if (DateTime.Now - startedAt < StartupWindow) { why = "just started"; return true; }
        var idle = OledCare.Idle;
        if (idle < QuietFor) { why = $"in use (idle {idle.TotalSeconds:0} s)"; return false; }
        why = $"idle {idle.TotalMinutes:0.#} min, nothing fullscreen";
        return true;
    }

    static readonly DateTime startedAt = DateTime.Now;
    /// <summary>An update found this soon after Brim starts is installed right away.</summary>
    static readonly TimeSpan StartupWindow = TimeSpan.FromMinutes(5);
    static int failedChecks;

    /// <summary>"Install updates by themselves" was switched on: an update already waiting gets installed too.</summary>
    public static void AutoInstallChanged()
    {
        if (Now == State.Ready && SettingsStore.Current.AutoInstallUpdates) quiet.Start();
    }

    /// <summary>Once a minute while an update waits: install it when the moment is right.</summary>
    static void QuietTick()
    {
        var s = SettingsStore.Current;
        if (Now != State.Ready || !Online.Updates || !s.AutoInstallUpdates) { quiet.Stop(); return; }
        // Tried this very version a moment ago and still here (the install didn't take): don't go round in circles
        if (s.UpdateTried == NewVersion && DateTime.Now - s.UpdateTriedAt < TimeSpan.FromHours(6))
        {
            quiet.Stop();
            Log.Note("update", $"not installing {NewVersion} by itself again yet (it was tried at {s.UpdateTriedAt:HH:mm}); Settings › General › Restart to update still can");
            return;
        }
        if (!QuietMoment(out var why)) return;
        quiet.Stop();
        Log.Note("update", $"installing Brim {NewVersion} by itself ({why})");
        InstallNow?.Invoke();
    }

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Brim-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    static bool Allowed => Installer.RunningInstalled || Environment.GetEnvironmentVariable("BRIM_UPDATE_TEST") == "1";

    public static void Start()
    {
        CleanOld();
        if (!Allowed) return;
        quiet.Tick += (_, _) => QuietTick();
        timer.Tick += (_, _) => { if (Online.Updates) _ = Check(); };
        timer.Start();
        // First look soon after start-up (turning the PC on is a good time to update), once the network is up
        var first = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        first.Tick += (_, _) => { first.Stop(); if (Online.Updates) _ = Check(); };
        first.Start();
    }

    static void Set(State s, string? problem = null)
    {
        Now = s;
        Problem = problem;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
    }

    /// <summary>Look for a newer release and, if there is one, download it. Safe to call any time.</summary>
    public static async Task Check()
    {
        if (busy || Now == State.Ready) return;
        busy = true;
        try
        {
            Set(State.Checking);
            LastChecked = DateTime.Now;
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !Version.TryParse(PretendVersion ?? Installer.Version, out var mine) || latest <= mine)
            {
                Set(State.UpToDate);
                return;
            }
            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind != JsonValueKind.Object) { Set(State.Failed, "The new release has no Setup file"); return; }
            NewVersion = latest.ToString(3);
            var name = asset.GetProperty("name").GetString()!;
            var url = asset.GetProperty("browser_download_url").GetString()!;
            var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var expected = Regex.Match(body, @"\b[0-9A-Fa-f]{64}\b").Value;
            ExpectedHash = expected;

            Directory.CreateDirectory(Folder);
            var dest = Path.Combine(Folder, name);
            if (!File.Exists(dest) || (expected.Length > 0 && !HashMatches(dest, expected)))
            {
                Set(State.Downloading);
                Log.Note("update", $"downloading Brim {NewVersion}");
                var part = dest + ".part";
                using (var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    res.EnsureSuccessStatusCode();
                    long total = res.Content.Headers.ContentLength ?? 0, got = 0;
                    await using var src = await res.Content.ReadAsStreamAsync();
                    await using (var file = File.Create(part))
                    {
                        var buf = new byte[81920];
                        int n;
                        while ((n = await src.ReadAsync(buf)) > 0)
                        {
                            await file.WriteAsync(buf.AsMemory(0, n));
                            got += n;
                            if (total > 0) Progress = (double)got / total;
                        }
                    }
                }
                if (expected.Length > 0 && !HashMatches(part, expected))
                {
                    File.Delete(part);
                    Log.Note("update", "download didn't match the release's checksum, thrown away");
                    Set(State.Failed, "The download was damaged; it'll try again later");
                    return;
                }
                File.Move(part, dest, true);
            }
            ReadyFile = dest;
            failedChecks = 0;
            Log.Note("update", $"Brim {NewVersion} downloaded and ready");
            Set(State.Ready);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                Ready?.Invoke();
                // Install at a quiet moment, maybe right now (unless installing is left to you)
                if (SettingsStore.Current.AutoInstallUpdates) { quiet.Start(); QuietTick(); }
            }));
        }
        catch (Exception ex)
        {
            Log.Note("update", "check failed: " + ex.Message);
            Set(State.Failed, "Couldn't reach GitHub; it'll try again later");
            // Right after the PC starts the network may not be up yet: try again in a couple of minutes, not hours
            if (++failedChecks <= 3)
            {
                var retry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
                retry.Tick += (_, _) => { retry.Stop(); if (Online.Updates) _ = Check(); };
                retry.Start();
            }
        }
        finally { busy = false; }
    }

    static bool HashMatches(string file, string expected)
    {
        try
        {
            using var s = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(s)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Hand over to the new Setup (it waits for this Brim to close, installs, and starts the new one).</summary>
    public static bool Install()
    {
        if (ReadyFile == null || !File.Exists(ReadyFile)) return false;
        try
        {
            Log.Note("update", $"installing Brim {NewVersion}");
            SettingsStore.Current.UpdateTried = NewVersion;
            SettingsStore.Current.UpdateTriedAt = DateTime.Now;
            SettingsStore.Save();
            Process.Start(new ProcessStartInfo(ReadyFile, $"--install --after {Environment.ProcessId}")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(ReadyFile)!,
            });
            return true;
        }
        catch (Exception ex) { Log.Error("update install", ex); return false; }
    }

    /// <summary>Remove Setup files for versions that are already installed.</summary>
    static void CleanOld()
    {
        try
        {
            if (!Directory.Exists(Folder) || !Version.TryParse(Installer.Version, out var mine)) return;
            foreach (var f in Directory.GetFiles(Folder))
            {
                var m = Regex.Match(Path.GetFileName(f), @"(\d+\.\d+\.\d+)");
                if (f.EndsWith(".part") || (m.Success && Version.Parse(m.Groups[1].Value) <= mine))
                    try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
