using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Brim.Core;

/// <summary>One way to start a game: through Steam, a particular exe, or the game's own renderer switch.</summary>
public sealed record LaunchOption(string Id, string Label, string? Exe, string? Args, bool ViaSteam);

/// <summary>
/// Works out, for any game on any PC, the ways it can be started and which graphics API each uses, so "Launch with"
/// can offer "DirectX 11 / DirectX 12 / Vulkan". Nothing is hard-coded per game:
///   • games that ship one exe per renderer (bg3.exe + bg3_dx11.exe): read from each exe;
///   • games that switch with a launch option: found from the engine they're built on and the renderers built in —
///     Unreal (-dx11 / -dx12 / -vulkan), Unity (-force-d3d11 / -force-d3d12 / -force-vulkan), Source 2 (-dx11 / -vulkan).
/// Results are kept on disk per file (path, size, date), so each exe is looked at once and again only after an update.
/// </summary>
public static class GameRenderer
{
    static readonly string CacheFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim", "renderers.json");
    static Dictionary<string, string>? disk;
    static readonly object gate = new();
    static bool dirty;

    static Dictionary<string, string> Disk
    {
        get
        {
            lock (gate)
            {
                if (disk != null) return disk;
                try { disk = File.Exists(CacheFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheFile)) : null; } catch { }
                return disk ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    static string? FileKey(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? $"{path.ToLowerInvariant()}|{f.Length}|{f.LastWriteTimeUtc.Ticks}" : null; }
        catch { return null; }
    }

    static bool TryCached(string kind, string path, out string value)
    {
        value = "";
        var k = FileKey(path);
        if (k == null) return false;
        lock (gate) return Disk.TryGetValue(kind + "|" + k, out value!);
    }

    static void Remember(string kind, string path, string value)
    {
        var k = FileKey(path);
        if (k == null) return;
        lock (gate) { Disk[kind + "|" + k] = value; dirty = true; }
    }

    static void SaveCache()
    {
        lock (gate)
        {
            if (!dirty || disk == null) return;
            dirty = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(disk));
            }
            catch (Exception ex) { Log.Error("renderer cache", ex); }
        }
    }

    // ------------------------------------------------------------------ the choices

    public static List<LaunchOption> Options(GameInfo g)
    {
        var list = new List<LaunchOption>();
        if (g.SteamAppId != null) list.Add(new("steam", "Through Steam (its default)", null, null, true));
        foreach (var c in g.Candidates)
            list.Add(new("exe:" + c.ToLowerInvariant(), Label(c, g.Folder), c, null, false));
        foreach (var (api, arg) in Switches(g))
            list.Add(new("arg:" + arg, $"{api}  ({arg})", null, arg, g.SteamAppId != null));
        return list;
    }

    /// <summary>The option the game starts with now (from your choice), for the ✓ in the menu.</summary>
    public static string CurrentId(GameInfo g, GameOverride ov)
    {
        if (!string.IsNullOrWhiteSpace(ov.RendererArgs)) return "arg:" + ov.RendererArgs;
        if (g.SteamAppId != null && string.IsNullOrWhiteSpace(ov.Exe)) return "steam";
        return g.Exe != null ? "exe:" + g.Exe.ToLowerInvariant() : "";
    }

    /// <summary>Make this the way the game starts from now on.</summary>
    public static void Choose(GameInfo g, GameOverride ov, LaunchOption o)
    {
        if (o.Id == "steam") { ov.Exe = null; ov.RendererArgs = null; }
        else if (o.Exe != null) { ov.Exe = o.Exe; ov.RendererArgs = null; }
        else ov.RendererArgs = o.Args;
    }

    // ------------------------------------------------------------------ separate exes

    /// <summary>A friendly name for one exe: "Vulkan  (bin\bg3.exe)", "Launcher  (…)", or just its path.</summary>
    public static string Label(string exe, string? folder)
    {
        var file = Path.GetFileNameWithoutExtension(exe);
        string where = folder != null && exe.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? exe[folder.Length..].TrimStart('\\') : Path.GetFileName(exe);
        if (Regex.IsMatch(file, "(?i)launcher")) return $"Launcher  ({where})";
        string? api = FromName(file) ?? Detect(exe);
        return api != null ? $"{api}  ({where})" : where;
    }

    static string? FromName(string file)
    {
        var f = file.ToLowerInvariant();
        if (Regex.IsMatch(f, @"dx12|d3d12")) return "DirectX 12";
        if (Regex.IsMatch(f, @"dx11|d3d11")) return "DirectX 11";
        if (Regex.IsMatch(f, @"dx9|d3d9")) return "DirectX 9";
        if (Regex.IsMatch(f, @"vulkan|_vk\b|-vk\b")) return "Vulkan";
        if (Regex.IsMatch(f, @"opengl|_gl\b")) return "OpenGL";
        return null;
    }

    /// <summary>The graphics API of one exe: from its import table, else (once looked at) from the libraries it names.</summary>
    public static string? Detect(string exe)
    {
        if (TryCached("api", exe, out var hit)) return hit.Length == 0 ? null : hit;
        string result = "";
        try
        {
            var dlls = Imports(exe);
            bool Has(string n) => dlls.Contains(n);
            result = Has("vulkan-1.dll") && !Has("d3d12.dll") && !Has("d3d11.dll") ? "Vulkan"
                   : Has("d3d12.dll") ? "DirectX 12"
                   : Has("d3d11.dll") ? "DirectX 11"
                   : Has("vulkan-1.dll") ? "Vulkan"
                   : Has("d3d9.dll") ? "DirectX 9"
                   : Has("opengl32.dll") ? "OpenGL"
                   : "";
        }
        catch { }
        // Only a quick answer here (the menu is open); the thorough look happens in Warm
        return result.Length == 0 ? null : result;
    }

    // ------------------------------------------------------------------ renderer switches (one exe, several renderers)

    static readonly (string api, string[] marks)[] Apis =
    {
        ("DirectX 12", new[] { "d3d12.dll", "D3D12RHI" }),
        ("DirectX 11", new[] { "d3d11.dll", "D3D11RHI" }),
        ("Vulkan", new[] { "vulkan-1.dll", "VulkanRHI" }),
    };

    /// <summary>Launch options that pick the renderer, for engines that have them, limited to the renderers built in.</summary>
    public static List<(string api, string arg)> Switches(GameInfo g)
    {
        var result = new List<(string, string)>();
        var engine = Engine(g);
        if (engine == null) return result;
        var (kind, file) = engine.Value;
        if (!TryCached("switch", file, out var apis)) return result; // not looked at yet: Warm does it
        var found = apis.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (found.Count < 2) return result; // only one renderer: nothing to choose
        foreach (var (api, _) in Apis)
        {
            if (!found.Contains(api)) continue;
            string arg = (kind, api) switch
            {
                ("Unity", "DirectX 12") => "-force-d3d12",
                ("Unity", "DirectX 11") => "-force-d3d11",
                ("Unity", "Vulkan") => "-force-vulkan",
                ("Unreal", "DirectX 12") => "-dx12",
                ("Unreal", "DirectX 11") => "-dx11",
                ("Unreal", "Vulkan") => "-vulkan",
                ("Source2", "DirectX 11") => "-dx11",
                ("Source2", "Vulkan") => "-vulkan",
                _ => "",
            };
            if (arg.Length > 0) result.Add((api, arg));
        }
        return result;
    }

    /// <summary>Which engine the game uses and the file that tells which renderers it has.</summary>
    static (string kind, string file)? Engine(GameInfo g)
    {
        var exes = g.Candidates.Concat(g.Exe != null ? new[] { g.Exe } : Array.Empty<string>()).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var exe in exes)
        {
            var dir = Path.GetDirectoryName(exe)!;
            var unity = Path.Combine(dir, "UnityPlayer.dll");
            if (File.Exists(unity)) return ("Unity", unity);
        }
        var shipping = exes.FirstOrDefault(e => Regex.IsMatch(Path.GetFileName(e), @"(?i)-Win64-(Shipping|Test)\.exe$") || e.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase));
        if (shipping != null) return ("Unreal", shipping);
        foreach (var exe in exes)
        {
            var dir = Path.GetDirectoryName(exe)!;
            if (File.Exists(Path.Combine(dir, "engine2.dll")) && (File.Exists(Path.Combine(dir, "rendersystemvulkan.dll")) || File.Exists(Path.Combine(dir, "rendersystemdx11.dll"))))
                return ("Source2", Path.Combine(dir, "engine2.dll"));
        }
        return null;
    }

    // ------------------------------------------------------------------ looking at files (in the background)

    /// <summary>
    /// Look at every game's exes and engine once (results kept on disk), so the menu can name the renderers.
    /// Runs when the Games tab opens; new or updated games are picked up the same way.
    /// </summary>
    public static Task Warm(IEnumerable<GameInfo> games)
    {
        var list = games.ToList();
        return Task.Run(() =>
        {
            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal;
            foreach (var g in list)
            {
                try
                {
                    // Separate exes whose imports don't say: look for the libraries they load by name
                    var plain = g.Candidates.Where(c => !Regex.IsMatch(Path.GetFileName(c), "(?i)launcher") && FromName(Path.GetFileNameWithoutExtension(c)) == null).ToList();
                    if (g.Candidates.Count >= 2)
                        foreach (var exe in plain)
                        {
                            if (TryCached("api", exe, out _)) continue;
                            var api = Detect(exe) ?? First(Scan(exe));
                            Remember("api", exe, api ?? "");
                        }

                    // One exe, several renderers
                    if (Engine(g) is { } e && !TryCached("switch", e.file, out _))
                    {
                        HashSet<string> found;
                        if (e.kind == "Source2")
                        {
                            var dir = Path.GetDirectoryName(e.file)!;
                            found = new HashSet<string>();
                            if (File.Exists(Path.Combine(dir, "rendersystemdx11.dll"))) found.Add("DirectX 11");
                            if (File.Exists(Path.Combine(dir, "rendersystemvulkan.dll"))) found.Add("Vulkan");
                        }
                        else found = Scan(e.file);
                        Remember("switch", e.file, string.Join(",", found));
                    }
                }
                catch (Exception ex) { Log.Error("renderer " + g.Name, ex); }
            }
            SaveCache();
        });
    }

    static string? First(HashSet<string> found) => Apis.Select(a => a.api).FirstOrDefault(found.Contains);

    /// <summary>Which renderers a file names (library names, or Unreal's RHI module names in either text encoding).</summary>
    static HashSet<string> Scan(string file)
    {
        var hits = new HashSet<string>();
        try
        {
            var needles = Apis.SelectMany(a => a.marks.SelectMany(m => new[]
            {
                (a.api, Encoding.ASCII.GetBytes(m)), (a.api, Encoding.ASCII.GetBytes(m.ToUpperInvariant())), (a.api, Encoding.Unicode.GetBytes(m)),
            })).ToList();
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            if (fs.Length > 600_000_000) return hits;
            var buf = new byte[1 << 22];
            const int overlap = 32;
            int read, keep = 0;
            while ((read = fs.Read(buf, keep, buf.Length - keep)) > 0)
            {
                int len = keep + read;
                var span = new ReadOnlySpan<byte>(buf, 0, len);
                foreach (var (api, text) in needles)
                    if (!hits.Contains(api) && span.IndexOf(text) >= 0) hits.Add(api);
                if (hits.Count == Apis.Length) break;
                keep = Math.Min(overlap, len);
                Array.Copy(buf, len - keep, buf, 0, keep);
            }
        }
        catch { }
        return hits;
    }

    static HashSet<string> Imports(string exe)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var br = new BinaryReader(fs);
        fs.Position = 0x3C;
        int pe = br.ReadInt32();
        fs.Position = pe;
        if (br.ReadUInt32() != 0x00004550) return set; // "PE\0\0"
        fs.Position = pe + 6;
        int sections = br.ReadUInt16();
        fs.Position = pe + 20;
        int optSize = br.ReadUInt16();
        int opt = pe + 24;
        fs.Position = opt;
        bool pe64 = br.ReadUInt16() == 0x20B;
        int dirs = opt + (pe64 ? 112 : 96);
        fs.Position = dirs + 8;  // [1] imports
        uint importRva = br.ReadUInt32();
        fs.Position = dirs + 13 * 8; // [13] delay imports
        uint delayRva = br.ReadUInt32();

        var secs = new List<(uint va, uint size, uint raw)>();
        int secStart = opt + optSize;
        for (int i = 0; i < sections; i++)
        {
            fs.Position = secStart + i * 40 + 8;
            uint vsize = br.ReadUInt32(), va = br.ReadUInt32(), rawSize = br.ReadUInt32(), raw = br.ReadUInt32();
            secs.Add((va, Math.Max(vsize, rawSize), raw));
        }
        long Off(uint rva)
        {
            foreach (var s in secs) if (rva >= s.va && rva < s.va + s.size) return rva - s.va + s.raw;
            return -1;
        }
        string Name(uint rva)
        {
            long o = Off(rva);
            if (o < 0) return "";
            fs.Position = o;
            var sb = new StringBuilder();
            for (int i = 0; i < 64; i++) { int c = fs.ReadByte(); if (c <= 0) break; sb.Append((char)c); }
            return sb.ToString();
        }
        void Walk(uint rva, int stride, int nameAt)
        {
            long o = Off(rva);
            if (rva == 0 || o < 0) return;
            for (int i = 0; i < 512; i++)
            {
                fs.Position = o + i * stride + nameAt;
                uint nameRva = br.ReadUInt32();
                if (nameRva == 0) break;
                long keep = fs.Position;
                set.Add(Name(nameRva));
                fs.Position = keep;
            }
        }
        Walk(importRva, 20, 12); // IMAGE_IMPORT_DESCRIPTOR.Name
        Walk(delayRva, 32, 4);   // IMAGE_DELAYLOAD_DESCRIPTOR.DllNameRVA
        return set;
    }
}
