namespace Brim.Core;

// Used when building without ApiKeys.Local.cs (e.g. from the public source): no built-in keys, so video wallpapers
// and SteamGridDB covers ask for your own free key in Settings.
public static partial class ApiKeys
{
    const string Mask = "";
    const string BuiltInPixabay = "";
    const string BuiltInSteamGrid = "";
}
