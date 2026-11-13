using System;
using System.Text;

namespace Brim.Core;

/// <summary>
/// The API keys Brim can ship with (video wallpapers from Pixabay, covers from SteamGridDB), so nobody has to make an
/// account. Someone's own key in Settings is used instead when they add one.
///
/// The built-in keys live in ApiKeys.Local.cs, which is never published with the source code. A build without that
/// file uses ApiKeys.Empty.cs instead, and then those two features simply ask for a key as before.
/// </summary>
public static partial class ApiKeys
{
    static AppSettings S => SettingsStore.Current;

    public static string Pixabay => Own(S.PixabayKey) ?? Unscramble(BuiltInPixabay);
    public static string SteamGrid => Own(S.SteamGridKey) ?? Unscramble(BuiltInSteamGrid);
    public static bool PixabayBuiltIn => BuiltInPixabay.Length > 0;
    public static bool SteamGridBuiltIn => BuiltInSteamGrid.Length > 0;

    static string? Own(string? k) => string.IsNullOrWhiteSpace(k) ? null : k.Trim();

    // Scrambled only so the keys aren't plain text inside Brim.exe: not a secret store
    static string Unscramble(string s)
    {
        if (s.Length == 0 || Mask.Length == 0) return "";
        try
        {
            var mask = Convert.FromBase64String(Mask);
            var b = Convert.FromBase64String(s);
            for (int i = 0; i < b.Length; i++) b[i] ^= mask[i % mask.Length];
            return Encoding.UTF8.GetString(b);
        }
        catch { return ""; }
    }
}
