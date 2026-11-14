using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brim.Core;

public enum NotchShape { Notch, Island }
public enum RestingFace { Clock, Date, Battery, Weather, Cpu, Nothing }
public enum TaskbarMode { Windows, AutoHide, Dock }
public enum WidgetKind { Clock, Day, Calendar, Music, Stats, Weather, Note, Progress, Headline, Custom, WorldClock }

/// <summary>A city for the world clock: where it is (for weather) and its time zone.</summary>
public class CityItem
{
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    /// <summary>IANA name like "Europe/London"</summary>
    public string TimeZone { get; set; } = "";
}

/// <summary>Your changes to a detected game: a nicer name, a different exe, launch arguments, or hidden.</summary>
public class GameOverride
{
    public string Key { get; set; } = "";
    public string? Name { get; set; }
    public string? Exe { get; set; }
    public string? Args { get; set; }
    public bool Hidden { get; set; }
    /// <summary>A cover image you picked yourself</summary>
    public string? Cover { get; set; }
    /// <summary>Show the "Launch with" choices (DirectX 11, Vulkan…) every time you start it</summary>
    public bool AskEachTime { get; set; }
    /// <summary>A renderer switch to start with, e.g. "-dx11" or "-force-vulkan" (games with several renderers in one exe)</summary>
    public string? RendererArgs { get; set; }
}

/// <summary>Which notifications of one app show in the notch's Messages tab.</summary>
public class MessageRule
{
    /// <summary>All, People (only from the names below) or Off</summary>
    public string Mode { get; set; } = "All";
    public List<string> People { get; set; } = new();
    /// <summary>Names whose messages are never shown</summary>
    public List<string> Muted { get; set; } = new();
    /// <summary>Windows' id for the app (to read its notification settings)</summary>
    public string? AppId { get; set; }
}

/// <summary>One line of a designed text widget. Text can hold tokens like {WEEKDAY} or {song}.</summary>
public class TextLine
{
    public string Text { get; set; } = "{time}";
    public string Font { get; set; } = "Default";
    public double Size { get; set; } = 24;
    public string Weight { get; set; } = "Regular";
    public string Spacing { get; set; } = "None";
    public string Case { get; set; } = "As typed";
    public double Opacity { get; set; } = 1;
    public string? Color { get; set; }
    public bool MusicOnly { get; set; }
}

public class ShortcutItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Args { get; set; }
}

public class WidgetItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public WidgetKind Kind { get; set; }
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public string? Text { get; set; }

    /// <summary>Set when several widgets are combined into one card.</summary>
    public List<WidgetKind>? Parts { get; set; }
    /// <summary>Column (stacked) or Row (side by side) for combined widgets.</summary>
    public string Layout { get; set; } = "Column";
    /// <summary>null = the default look from Settings</summary>
    public string? Look { get; set; }
    public string Font { get; set; } = "Default";
    public string? TextColor { get; set; }
    public double Scale { get; set; } = 1;
    public double Radius { get; set; } = 22;
    /// <summary>Lines of a designed (Custom) widget.</summary>
    public List<TextLine>? Lines { get; set; }
    public string Align { get; set; } = "Center";
    /// <summary>Cities of a world clock widget.</summary>
    public List<CityItem>? Cities { get; set; }

    [JsonIgnore]
    public IReadOnlyList<WidgetKind> AllKinds => Parts is { Count: > 0 } ? Parts : new[] { Kind };

    [JsonIgnore]
    public string Signature => string.Join(",", AllKinds) + $"|{Layout}|{Look}|{Font}|{TextColor}|{Scale}|{Radius}|{Align}|" +
                               (Lines == null ? "" : JsonSerializer.Serialize(Lines)) +
                               (Cities == null ? "" : "|" + string.Join(";", Cities.Select(c => c.Name)));
}

public class AppSettings
{
    // Notch
    public NotchShape Shape { get; set; } = NotchShape.Notch;
    public RestingFace Face { get; set; } = RestingFace.Clock;
    public bool ShowSongWhenClosed { get; set; } = true;
    public bool SongPopup { get; set; } = true;
    /// <summary>Pop the notch out for volume, charging, headphones, mic and timer changes</summary>
    public bool LiveActivities { get; set; } = true;
    public bool HideUntilHover { get; set; }
    /// <summary>Show the notch on the desktop, but tuck it away (until pointed at) while an app window is in front</summary>
    public bool HideOverApps { get; set; }

    /// <summary>Keep the clipboard history (text and files) after a restart, encrypted for this Windows account</summary>
    public bool ClipRemember { get; set; }
    /// <summary>Accent colour follows the wallpaper (including Wallpaper Engine scenes)</summary>
    public bool AccentFromWallpaper { get; set; }
    /// <summary>Switch between a day and a night theme at sunrise and sunset</summary>
    public bool AutoTheme { get; set; }
    public string DayTheme { get; set; } = "Graphite";
    public string NightTheme { get; set; } = "Black";
    /// <summary>Screen (device name) for the notch and dock; empty = main screen</summary>
    public string NotchScreen { get; set; } = "";
    public string DockScreen { get; set; } = "*"; // "*" = every screen (like Windows' own taskbar)
    /// <summary>Set once older settings were moved to "every screen" for the dock</summary>
    public bool DockEveryScreenOffered { get; set; }
    public Dictionary<string, double> GamePlaySeconds { get; set; } = new();
    /// <summary>Always, Uncovered (pause when covered) or Desktop (only while the desktop is focused)</summary>
    public string LivePlayMode { get; set; } = "Desktop";
    /// <summary>Pause live wallpapers while running on battery (unplugged)</summary>
    public bool LivePauseOnBattery { get; set; }
    /// <summary>Pause live wallpapers while Windows' Battery saver is on</summary>
    public bool LivePauseOnSaver { get; set; } = true;

    // OLED care (burn-in protection)
    public bool OledCare { get; set; }
    public bool OledShift { get; set; } = true;
    /// <summary>Minutes without input before the notch, dock and widgets dim (0 = never)</summary>
    public int OledDimMinutes { get; set; } = 2;
    /// <summary>Minutes without input before the whole screen goes black (0 = never)</summary>
    public int OledBlackMinutes { get; set; }
    public int HoverDelayMs { get; set; } = 20;
    public string Speed { get; set; } = "Smooth";
    public bool HideInFullscreen { get; set; } = true;
    public bool HideFromCapture { get; set; }
    public double IslandTop { get; set; } = 8;
    public bool ReopenLastTab { get; set; }
    /// <summary>Solid, Glass (blurred see-through) or Clear (invisible when closed, glass when open)</summary>
    public string NotchSurface { get; set; } = "Solid";
    /// <summary>Where along the top edge the notch sits: Left, Center or Right</summary>
    public string NotchPosition { get; set; } = "Center";
    /// <summary>The notch's tabs, in order (anything not listed is hidden)</summary>
    public List<string> NotchTabs { get; set; } = new(NotchCatalog.DefaultTabs);
    /// <summary>Every tab this person has been offered (so tabs added in an update show up once, and stay hidden if removed)</summary>
    public List<string> SeenTabs { get; set; } = new();
    /// <summary>What the Home tab shows, in order</summary>
    public List<string> HomeItems { get; set; } = new(NotchCatalog.DefaultHome);

    // Messages tab
    public Dictionary<string, MessageRule> MessageApps { get; set; } = new();
    /// <summary>Pop the notch out when a message you'd see in the Messages tab arrives</summary>
    public bool MessagePopups { get; set; } = true;
    /// <summary>Mirror every app's Windows notifications, not only chat and mail apps'</summary>
    public bool MessagesAllApps { get; set; }


    // Look
    public string Theme { get; set; } = "Black";
    public string Accent { get; set; } = "#7C9CFF";
    public bool AlbumColors { get; set; } = true;
    public string WidgetStyle { get; set; } = "Glass";

    // Shortcuts
    public List<ShortcutItem> Shortcuts { get; set; } = new();

    // Widgets
    public List<WidgetItem> Widgets { get; set; } = new();
    public bool WidgetsLocked { get; set; }
    public bool Clock24 { get; set; } = true;
    public string WeatherCity { get; set; } = "";
    public double? WeatherLat { get; set; }
    public double? WeatherLon { get; set; }
    public bool Fahrenheit { get; set; }

    // Taskbar
    public TaskbarMode Taskbar { get; set; } = TaskbarMode.Windows;
    /// <summary>Solid, Glass or Clear for the Brim dock</summary>
    public string DockSurface { get; set; } = "Solid";
    public bool DockAutoHide { get; set; }
    public bool DockReserve { get; set; } = true;
    public string DockSize { get; set; } = "Medium";
    public bool DockClock { get; set; } = true;
    public bool DockWeather { get; set; } = true;
    /// <summary>Where along the bottom edge the dock sits: Left, Center or Right</summary>
    public string DockPosition { get; set; } = "Center";
    /// <summary>Dock icon size in pixels (0 = from the old Small/Medium/Large setting)</summary>
    public int DockIconPx { get; set; }
    /// <summary>Space above and below the icons (the dock's height)</summary>
    public int DockPadding { get; set; } = 6;
    public int DockSpacing { get; set; }
    /// <summary>Gap between the floating dock and the bottom of the screen</summary>
    public int DockGap { get; set; } = 8;
    public int DockRadius { get; set; } = 18;
    /// <summary>Red dot / count on dock icons when an app has something for you</summary>
    public bool DockBadges { get; set; } = true;
    /// <summary>A Task Manager button on the dock</summary>
    public bool DockTaskManager { get; set; }
    /// <summary>With a maximised window above the dock: Bar (solid, edge to edge) or Floating (wallpaper shows beside it)</summary>
    public string DockWhenMaximized { get; set; } = "Bar";
    public List<ShortcutItem> DockPins { get; set; } = new();
    /// <summary>Your own order of dock icons (app keys), set by dragging them</summary>
    public List<string> DockOrder { get; set; } = new();
    public bool? OriginalAutoHide { get; set; }

    // Desktop
    public bool HideDesktopIcons { get; set; }
    public string? LiveWallpaper { get; set; }
    /// <summary>Who shows the wallpaper: "Brim", "WallpaperEngine" or "" (never chosen through Brim)</summary>
    public string WallpaperOwner { get; set; } = "";
    /// <summary>The Wallpaper Engine wallpaper Brim took over from; if Wallpaper Engine brings that one back, Brim closes it again</summary>
    public string? WeSuppressedFile { get; set; }
    /// <summary>The picture Brim set, so it can be put back if Wallpaper Engine restores an older one as it closes</summary>
    public string? BrimPicture { get; set; }
    public string PexelsKey { get; set; } = "";
    public string PixabayKey { get; set; } = "";
    public bool LivePauseWhenCovered { get; set; } = true;

    // Games
    public List<GameOverride> GameOverrides { get; set; } = new();
    /// <summary>Folders whose sub-folders are games. Empty = look for a "Games" folder on every drive.</summary>
    public List<string> GameFolders { get; set; } = new();
    public List<ShortcutItem> ManualGames { get; set; } = new();
    public Dictionary<string, DateTime> GameLaunches { get; set; } = new();
    public bool GameCovers { get; set; } = true;
    /// <summary>Your own order for the Games tab (game keys). Empty = most recently played first.</summary>
    public List<string> GameOrder { get; set; } = new();
    /// <summary>Optional SteamGridDB key, for cover art of games that aren't on Steam</summary>
    public string SteamGridKey { get; set; } = "";
    /// <summary>Also find games anywhere on your drives by the files games ship with (like the NVIDIA app)</summary>
    public bool GameFingerprints { get; set; } = true;

    // General
    public bool StartWithWindows { get; set; }
    /// <summary>Turn off Windows' own notification pop-up for apps shown in the notch, so only the notch pops up</summary>
    public bool NotchOnlyPopups { get; set; }
    /// <summary>Apps whose Windows pop-ups Brim turned off (and must turn back on)</summary>
    public List<string> BannersHidden { get; set; } = new();
    /// <summary>Look for new versions on GitHub and download them in the background (installing always asks first)</summary>
    public bool CheckUpdates { get; set; } = true;
    public bool FirstRunDone { get; set; }
}

/// <summary>The notch's tabs and Home items: ids, names and glyphs.</summary>
public static class NotchCatalog
{
    public static readonly (string id, string name, string glyph)[] Tabs =
    {
        ("Home", "Home", ""), ("Apps", "Shortcuts", ""), ("Games", "Games", ""), ("Messages", "Messages", ""),
        ("Shelf", "Shelf", ""), ("Clip", "Clipboard", ""), ("Ctrl", "Controls", ""),
        ("Timer", "Focus timer", ""), ("Notes", "Notes", "\uE70B"),
    };

    public static readonly string[] DefaultTabs = { "Home", "Apps", "Games", "Messages", "Notes", "Shelf", "Clip", "Ctrl", "Timer" };
    /// <summary>The tabs Brim had before tabs could be added by updates</summary>
    public static readonly string[] OriginalTabs = { "Home", "Apps", "Games", "Messages", "Shelf", "Clip", "Ctrl", "Timer" };

    public static readonly (string id, string name)[] Home =
    {
        ("Music", "Now playing (or your recent games)"), ("Date", "Day and date"), ("Devices", "Controller and headphone batteries"),
        ("Weather", "Weather"), ("Battery", "Laptop battery"), ("Cpu", "CPU"), ("Memory", "Memory"), ("Volume", "Volume"),
        ("Message", "Latest message"),
    };

    public static readonly string[] DefaultHome = { "Music", "Date", "Devices", "Cpu", "Memory", "Volume" };
}

public static class SettingsStore
{
    public static readonly string Folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Brim");
    static readonly string FilePath = System.IO.Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppSettings Current { get; private set; } = new();
    public static bool ReadOnly { get; set; }

    /// <summary>For checks: behave like a brand-new install (factory settings, nothing saved).</summary>
    public static void UseFactorySettings()
    {
        ReadOnly = true;
        Current = new AppSettings();
        if (Current.DockIconPx <= 0) Current.DockIconPx = 30;
        if (Current.NotchTabs.Count == 0) Current.NotchTabs.AddRange(NotchCatalog.DefaultTabs);
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts) ?? new AppSettings();
            // Older versions only had Small / Medium / Large for the dock
            if (Current.DockIconPx <= 0) Current.DockIconPx = Current.DockSize switch { "Small" => 24, "Large" => 36, _ => 30 };
            // Tabs from older versions that no longer exist (e.g. Lighting)
            Current.NotchTabs.RemoveAll(t => !NotchCatalog.Tabs.Any(x => x.id == t));
            if (Current.NotchTabs.Count == 0) Current.NotchTabs.AddRange(NotchCatalog.DefaultTabs);
            // Tabs added by an update (e.g. Notes) appear once, next to where they'd sit by default
            if (Current.SeenTabs.Count == 0) Current.SeenTabs.AddRange(NotchCatalog.OriginalTabs);
            var added = NotchCatalog.DefaultTabs.Where(id => !Current.SeenTabs.Contains(id)).ToList();
            foreach (var id in added)
            {
                if (!Current.NotchTabs.Contains(id))
                {
                    int at = Array.IndexOf(NotchCatalog.DefaultTabs, id);
                    var before = NotchCatalog.DefaultTabs.Take(at).LastOrDefault(Current.NotchTabs.Contains);
                    Current.NotchTabs.Insert(before == null ? 0 : Current.NotchTabs.IndexOf(before) + 1, id);
                }
                Current.SeenTabs.Add(id);
            }
            // The dock used to be on the main screen only: show it on every screen, like Windows' taskbar (once;
            // anyone who picked one particular other screen keeps it)
            bool moved = false;
            if (!Current.DockEveryScreenOffered) { if (Current.DockScreen == "") Current.DockScreen = "*"; Current.DockEveryScreenOffered = true; moved = true; }
            if ((added.Count > 0 || moved) && File.Exists(FilePath)) Save();
        }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
            Current = new AppSettings();
        }
    }

    public static void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Opts));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }
}

public static class Log
{
    /// <summary>A short line about what Brim decided and why (kept small: only written when something changes).</summary>
    public static void Note(string where, string text)
    {
        try
        {
            var file = System.IO.Path.Combine(SettingsStore.Folder, "brim.log");
            if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Delete(file); // never let it grow big
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {text}\n");
        }
        catch { }
    }

    /// <summary>The focused window, described for the log.</summary>
    public static string Foreground()
    {
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out uint pid);
        var exe = System.IO.Path.GetFileName(Native.ProcessPath(pid) ?? "?");
        var title = Native.TitleOf(fg);
        if (title.Length > 40) title = title[..40];
        return $"(focus: {exe} [{Native.ClassOf(fg)}] '{title}' zoomed={Native.IsZoomed(fg)} style=0x{Native.GetWindowLongPtr(fg, -16).ToInt64():X})";
    }

    public static void Error(string where, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.AppendAllText(System.IO.Path.Combine(SettingsStore.Folder, "brim.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex}\n");
        }
        catch { }
    }
}
