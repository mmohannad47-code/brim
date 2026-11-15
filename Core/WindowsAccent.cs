using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.Win32;

namespace Brim.Core;

/// <summary>
/// "Windows uses Brim's accent": writes Brim's accent into Windows' own accent settings (the same values Settings ›
/// Personalisation › Colours writes), so Start, the taskbar, title bars and other apps match the notch. Windows' own
/// values are saved first and put back when this is turned off or Brim is uninstalled.
/// </summary>
public static class WindowsAccent
{
    static AppSettings S => SettingsStore.Current;

    const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    const string DwmKey = @"Software\Microsoft\Windows\DWM";
    const string DesktopKey = @"Control Panel\Desktop";

    static readonly (string key, string name)[] Values =
    {
        (AccentKey, "AccentPalette"), (AccentKey, "AccentColorMenu"), (AccentKey, "StartColorMenu"),
        (DwmKey, "AccentColor"), (DwmKey, "ColorizationColor"), (DwmKey, "ColorizationAfterglow"),
        (DesktopKey, "AutoColorization"),
    };

    static System.Windows.Threading.DispatcherTimer? soon;

    /// <summary>Bring Windows in line with the setting, a moment after the accent stops changing (dragging the colour wheel).</summary>
    public static void SyncSoon()
    {
        if (SettingsStore.ReadOnly) return; // snapshots and self-tests never touch Windows
        if (!S.WindowsAccent && S.WindowsAccentBefore == null) return;
        if (soon == null)
        {
            soon = new() { Interval = TimeSpan.FromMilliseconds(500) };
            soon.Tick += (_, _) => { soon.Stop(); Sync(); };
        }
        soon.Stop();
        soon.Start();
    }

    public static void Sync()
    {
        try
        {
            if (!S.WindowsAccent) { Restore(); return; }
            var c = ThemeManager.Parse(S.Accent);
            var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            if (S.WindowsAccentApplied == hex) return;
            S.WindowsAccentBefore ??= Save();
            Write(c);
            S.WindowsAccentApplied = hex;
            SettingsStore.Save();
            Log.Note("accent", $"Windows accent set to {hex}");
            Broadcast();
        }
        catch (Exception ex) { Log.Error("windows accent", ex); }
    }

    /// <summary>Give Windows back the accent it had before Brim (turning this off, uninstalling).</summary>
    public static void Restore()
    {
        var before = S.WindowsAccentBefore;
        if (before == null) return;
        try
        {
            foreach (var (key, name) in Values)
            {
                if (!before.TryGetValue(key + "\\" + name, out var v)) continue;
                using var k = Registry.CurrentUser.CreateSubKey(key, true);
                if (v == "none") { k.DeleteValue(name, false); continue; }
                var kind = v[..v.IndexOf(':')];
                var data = v[(kind.Length + 1)..];
                switch (kind)
                {
                    case "dword": k.SetValue(name, unchecked((int)uint.Parse(data)), RegistryValueKind.DWord); break;
                    case "binary": k.SetValue(name, Convert.FromHexString(data), RegistryValueKind.Binary); break;
                    default: k.SetValue(name, data, RegistryValueKind.String); break;
                }
            }
            Log.Note("accent", "Windows accent given back");
        }
        catch (Exception ex) { Log.Error("windows accent restore", ex); }
        S.WindowsAccentBefore = null;
        S.WindowsAccentApplied = null;
        SettingsStore.Save();
        Broadcast();
    }

    static Dictionary<string, string> Save()
    {
        var d = new Dictionary<string, string>();
        foreach (var (key, name) in Values)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            var v = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            d[key + "\\" + name] = v == null ? "none" : k!.GetValueKind(name) switch
            {
                RegistryValueKind.DWord => "dword:" + unchecked((uint)(int)v),
                RegistryValueKind.Binary => "binary:" + Convert.ToHexString((byte[])v),
                _ => "sz:" + v,
            };
        }
        return d;
    }

    static void Write(Color c)
    {
        // Windows' eight shades: three lighter, the accent, three darker (and a spare)
        var shades = new[] { Mix(c, Colors.White, 0.6), Mix(c, Colors.White, 0.4), Mix(c, Colors.White, 0.2), c,
                             Mix(c, Colors.Black, 0.2), Mix(c, Colors.Black, 0.4), Mix(c, Colors.Black, 0.6), Mix(c, Colors.Black, 0.75) };
        var palette = shades.SelectMany(s => new byte[] { s.R, s.G, s.B, 0 }).ToArray();

        using (var k = Registry.CurrentUser.CreateSubKey(AccentKey, true))
        {
            k.SetValue("AccentPalette", palette, RegistryValueKind.Binary);
            // As Windows 11 writes them: the menu colour is the accent itself (Windows derives the DWM values from it), Start one shade darker
            k.SetValue("AccentColorMenu", Abgr(c), RegistryValueKind.DWord);
            k.SetValue("StartColorMenu", Abgr(shades[4]), RegistryValueKind.DWord);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(DwmKey, true))
        {
            k.SetValue("AccentColor", Abgr(c), RegistryValueKind.DWord);
            int argb = unchecked((int)(0xC4000000u | (uint)(c.R << 16 | c.G << 8 | c.B)));
            k.SetValue("ColorizationColor", argb, RegistryValueKind.DWord);
            k.SetValue("ColorizationAfterglow", argb, RegistryValueKind.DWord);
        }
        // Windows picking its own accent from the wallpaper would undo this at the next wallpaper change
        using (var k = Registry.CurrentUser.CreateSubKey(DesktopKey, true))
        {
            var kind = k.GetValue("AutoColorization") == null ? RegistryValueKind.DWord : k.GetValueKind("AutoColorization");
            if (kind == RegistryValueKind.String) k.SetValue("AutoColorization", "0", kind); else k.SetValue("AutoColorization", 0, RegistryValueKind.DWord);
        }
    }

    static int Abgr(Color c) => unchecked((int)(0xFF000000u | (uint)(c.B << 16 | c.G << 8 | c.R)));

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Tell Explorer and apps the colours changed (off the UI thread: every window gets asked).</summary>
    static void Broadcast() => Task.Run(() =>
    {
        try { Native.BroadcastSettingChange("ImmersiveColorSet"); } catch { }
    });

    /// <summary>Dev check: what Windows' accent values are now.</summary>
    public static string Describe() => string.Join("\n", Values.Select(v =>
    {
        using var k = Registry.CurrentUser.OpenSubKey(v.key);
        var x = k?.GetValue(v.name);
        return $"{v.name} = {(x is byte[] b ? Convert.ToHexString(b) : x is int i ? unchecked((uint)i).ToString("X8") : x?.ToString() ?? "(none)")}";
    }));
}
