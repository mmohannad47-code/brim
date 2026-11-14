using System;
using System.Linq;
using Microsoft.Win32;

namespace Brim.Core;

/// <summary>
/// "Only pop up in the notch": turns off Windows' own corner pop-up (Settings › System › Notifications › the app ›
/// Show notification banners) for the apps Brim shows. Their notifications still go to the notification centre, which
/// is where Brim reads them, so they keep arriving in the notch. Only apps Brim switched off are switched back on (when
/// this is turned off, Brim's pop-ups are turned off, an app is set to Off, Brim quits or is uninstalled), so an app
/// you'd already silenced yourself stays the way you set it.
/// </summary>
public static class WindowsBanners
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\";
    static AppSettings S => SettingsStore.Current;
    static readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(1) };

    public static void Start()
    {
        timer.Tick += (_, _) => Sync(); // apps that send their first notification later
        timer.Start();
        Sync();
    }

    /// <summary>Make Windows' banners match the setting for every app Brim knows.</summary>
    public static void Sync()
    {
        bool want = S.NotchOnlyPopups && S.MessagePopups;
        bool changed = false;
        foreach (var (app, rule) in S.MessageApps.ToList())
        {
            if (string.IsNullOrWhiteSpace(rule.AppId)) continue;
            bool hide = want && rule.Mode != "Off";
            bool ours = S.BannersHidden.Contains(rule.AppId, StringComparer.OrdinalIgnoreCase);
            if (hide && !ours)
            {
                if (Read(rule.AppId) == 0) continue; // already off (you did it): leave it as yours
                if (Write(rule.AppId, false)) { S.BannersHidden.Add(rule.AppId); changed = true; Log.Note("banners", $"Windows' pop-up off for {app}"); }
            }
            else if (!hide && ours)
            {
                Write(rule.AppId, true);
                S.BannersHidden.RemoveAll(a => string.Equals(a, rule.AppId, StringComparison.OrdinalIgnoreCase));
                changed = true;
                Log.Note("banners", $"Windows' pop-up back on for {app}");
            }
        }
        if (changed) SettingsStore.Save();
    }

    /// <summary>Put back every banner Brim turned off (quitting, uninstalling).</summary>
    public static void RestoreAll()
    {
        if (S.BannersHidden.Count == 0) return;
        foreach (var id in S.BannersHidden) Write(id, true);
        Log.Note("banners", $"Windows' pop-ups back on for {S.BannersHidden.Count} app(s)");
        S.BannersHidden.Clear();
        SettingsStore.Save();
    }

    static int? Read(string appId)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(Root + appId); return k?.GetValue("ShowBanner") as int?; }
        catch { return null; }
    }

    static bool Write(string appId, bool show)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Root + appId);
            if (show) k.DeleteValue("ShowBanner", false); // back to Windows' default (pop-ups on)
            else k.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex) { Log.Error("banners " + appId, ex); return false; }
    }
}
