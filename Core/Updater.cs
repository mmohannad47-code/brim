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
        timer.Tick += (_, _) => { if (SettingsStore.Current.CheckUpdates) _ = Check(); };
        timer.Start();
        // First look a little after start-up, out of the way of everything else
        var first = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1.5) };
        first.Tick += (_, _) => { first.Stop(); if (SettingsStore.Current.CheckUpdates) _ = Check(); };
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
            Log.Note("update", $"Brim {NewVersion} downloaded and ready");
            Set(State.Ready);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => Ready?.Invoke()));
        }
        catch (Exception ex)
        {
            Log.Note("update", "check failed: " + ex.Message);
            Set(State.Failed, "Couldn't reach GitHub; it'll try again later");
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
