namespace Brim.Core;

/// <summary>
/// What Brim may fetch from the internet. Everything works without it; these only add extras. One switch turns all
/// of them off (Settings › General › Online), and each can be turned off on its own.
/// </summary>
public static class Online
{
    static AppSettings S => SettingsStore.Current;

    /// <summary>Weather for the dock, Home tab and widgets (Open-Meteo)</summary>
    public static bool Weather => S.UseInternet && S.OnlineWeather;
    /// <summary>Synced lyrics for the song playing (LRCLIB)</summary>
    public static bool Lyrics => S.UseInternet && S.Lyrics;
    /// <summary>Cover art for games that don't have their own (Steam, SteamGridDB)</summary>
    public static bool Covers => S.UseInternet && S.GameCovers;
    /// <summary>Website logos for shortcuts to websites</summary>
    public static bool Icons => S.UseInternet && S.OnlineIcons;
    /// <summary>Searching and downloading wallpapers (Pixabay, Wallhaven)</summary>
    public static bool Wallpapers => S.UseInternet && S.OnlineWallpapers;
    /// <summary>Looking for new versions of Brim (GitHub)</summary>
    public static bool Updates => S.UseInternet && S.CheckUpdates;
}
