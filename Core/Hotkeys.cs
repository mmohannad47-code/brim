using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace Brim.Core;

/// <summary>One of Brim's keyboard shortcuts as you set it up: on or off, and its keys.</summary>
public class HotkeySetting
{
    public bool On { get; set; } = true;
    public uint Mods { get; set; }
    /// <summary>The key (a virtual-key code). For "Open shortcut 1–9" only the held keys count: the number is the key.</summary>
    public uint Vk { get; set; }
}

/// <summary>Brim's keyboard shortcuts: what each does, its keys out of the box, and how to write keys down.</summary>
public static class HotkeyCatalog
{
    public sealed record Entry(string Id, int MessageId, string Name, uint Mods, uint Vk);

    const uint Ctrl = Native.MOD_CONTROL, Alt = Native.MOD_ALT, CtrlAlt = Ctrl | Alt;

    /// <summary>"shortcuts" is Ctrl+Alt+1 … 9 (message ids 2–10).</summary>
    public static readonly Entry[] All =
    {
        new("notch", 1, "Open or close the notch", CtrlAlt, 'N'),
        new("search", 15, "Search apps, games and files", Ctrl, 0x20),
        new("shortcuts", 2, "Open shortcut 1–9", CtrlAlt, 0),
        new("playpause", 12, "Play / pause music", CtrlAlt, 'P'),
        new("mic", 14, "Mute / unmute the microphone", CtrlAlt, 'M'),
        new("colour", 13, "Copy the colour under the pointer", CtrlAlt, 'C'),
        new("plaintext", 16, "Paste as plain text", CtrlAlt, 'V'),
        new("icons", 11, "Hide / show desktop icons", CtrlAlt, 'H'),
    };

    static AppSettings S => SettingsStore.Current;

    public static HotkeySetting For(string id)
    {
        if (S.Hotkeys.TryGetValue(id, out var h)) return h;
        var e = All.First(x => x.Id == id);
        return new HotkeySetting { On = true, Mods = e.Mods, Vk = e.Vk };
    }

    /// <summary>How the shortcut is written now ("Ctrl+Alt+N"), or null when it's switched off.</summary>
    public static string? Label(string id)
    {
        var h = For(id);
        if (!h.On) return null;
        return id == "shortcuts" ? Describe(h.Mods, 0) + "1…9" : Describe(h.Mods, h.Vk);
    }

    /// <summary>For shortcut tile n (1–9): "Ctrl+Alt+3", or null when those keys are off.</summary>
    public static string? ForShortcut(int n)
    {
        var h = For("shortcuts");
        return h.On ? Describe(h.Mods, (uint)('0' + n)) : null;
    }

    /// <summary>Which other shortcut (that's on) already uses these keys, if any.</summary>
    public static string? UsedBy(string id, uint mods, uint vk)
    {
        foreach (var e in All)
        {
            if (e.Id == id) continue;
            var h = For(e.Id);
            if (!h.On || h.Mods != mods) continue;
            if (e.Id == "shortcuts" ? vk >= '1' && vk <= '9' : h.Vk == vk) return e.Name;
            if (id == "shortcuts" && h.Vk >= '1' && h.Vk <= '9') return e.Name;
        }
        return null;
    }

    public static string Describe(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Native.MOD_WIN) != 0) parts.Add("Win");
        var s = string.Join("+", parts);
        return vk == 0 ? s + "+" : (s.Length > 0 ? s + "+" : "") + KeyName(vk);
    }

    static string KeyName(uint vk)
    {
        if (vk is >= '0' and <= '9' or >= 'A' and <= 'Z') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        return key switch
        {
            Key.Space => "Space", Key.Return => "Enter", Key.Back => "Backspace", Key.Escape => "Esc",
            Key.OemTilde => "`", Key.OemMinus => "-", Key.OemPlus => "=", Key.OemOpenBrackets => "[", Key.Oem6 => "]",
            Key.OemPipe => "\\", Key.OemSemicolon => ";", Key.OemQuotes => "'", Key.OemComma => ",", Key.OemPeriod => ".",
            Key.OemQuestion => "/", Key.Next => "Page Down", Key.Prior => "Page Up", Key.Snapshot => "Print Screen",
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
            _ => key.ToString(),
        };
    }
}
