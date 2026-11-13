using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Brim.Core;

/// <param name="Stroke">Hairline borders; null = a soft white line</param>
/// <param name="Tagline">One line shown under the theme in Settings › Look</param>
/// <param name="OwnAccent">Picking the theme also picks its accent (unless the accent follows the wallpaper)</param>
public record Palette(string Name, string Surface, string Raised, string Text, string Dim, string Swatch, string DefaultAccent,
                      string? Stroke = null, string? Tagline = null, bool OwnAccent = false, string? Swatch2 = null);

public static class ThemeManager
{
    public static readonly List<Palette> Palettes = new()
    {
        new("Black",    "#FF000000", "#FF1C1C1F", "#FFF5F5F7", "#FF8E8E93", "#FF000000", "#7C9CFF"),
        new("Graphite", "#FF1C1C1F", "#FF2C2C30", "#FFF5F5F7", "#FF98989F", "#FF2C2C30", "#7C9CFF"),
        new("Smoke",    "#D9101014", "#33FFFFFF", "#FFF5F5F7", "#FFA0A0A8", "#FF505058", "#9AE6C8"),
        new("Rosé",     "#FF241519", "#FF3A2329", "#FFFFEEF2", "#FFC79AA6", "#FFE8A0B4", "#FF8FAB", OwnAccent: true),
        new("Lavender", "#FF1B1728", "#FF2C2640", "#FFF3EEFF", "#FFA79DC4", "#FFB8A6F0", "#B79CFF", OwnAccent: true),
        new("Peach",    "#FF241A14", "#FF3A2A20", "#FFFFF1E8", "#FFC9A48E", "#FFF5B48C", "#FFB37A", OwnAccent: true),
        new("Mint",     "#FF101F1B", "#FF1B322C", "#FFEAFFF7", "#FF8FBFAE", "#FF8EE0C2", "#6FE3B5", OwnAccent: true),
        new("Ocean",    "#FF0C1726", "#FF16263C", "#FFEAF3FF", "#FF8EA6C4", "#FF6FA8E8", "#5FB4FF", OwnAccent: true),

        // Statement themes: each brings its own accent and border colour
        new("Vampire",  "#FF0A0204", "#FF1F070C", "#FFF8E8EA", "#FFB27D84", "#FF8A0F1C", "#E3122F",
            Stroke: "#4DB0182B", Tagline: "Blood red on black", OwnAccent: true, Swatch2: "#FFE3122F"),
        new("Synthwave", "#FF140A24", "#FF241238", "#FFFFEEFC", "#FFB48FCB", "#FFFF3EA5", "#FF3EA5",
            Stroke: "#4D7A3EFF", Tagline: "Neon pink and violet", OwnAccent: true, Swatch2: "#FF3ED7FF"),
        new("Matrix",   "#FF020803", "#FF0B1A0E", "#FFD9FFE0", "#FF6FAE7B", "#FF0F3D18", "#39FF6A",
            Stroke: "#4039FF6A", Tagline: "Green phosphor on black", OwnAccent: true, Swatch2: "#FF39FF6A"),
        new("Gold",     "#FF0C0A06", "#FF1E1910", "#FFFFF6E0", "#FFBFAE86", "#FF3A2F17", "#E8B84A",
            Stroke: "#40E8B84A", Tagline: "Black and gold", OwnAccent: true, Swatch2: "#FFE8B84A"),
        new("Ember",    "#FF120B08", "#FF261712", "#FFFFEDE4", "#FFC49A87", "#FF5A2210", "#FF6A2B",
            Stroke: "#40FF6A2B", Tagline: "Charcoal and embers", OwnAccent: true, Swatch2: "#FFFF6A2B"),
        new("Abyss",    "#FF020B10", "#FF08202A", "#FFE2FBFF", "#FF7FA9B5", "#FF06303D", "#22D3EE",
            Stroke: "#4022D3EE", Tagline: "Deep sea teal", OwnAccent: true, Swatch2: "#FF22D3EE"),
        new("Glacier",  "#FF161B26", "#FF222A39", "#FFECEFF4", "#FF9AA5B8", "#FF3B4252", "#88C0D0",
            Stroke: "#3388C0D0", Tagline: "Slate and frost blue", OwnAccent: true, Swatch2: "#FF88C0D0"),
        new("Toxic",    "#FF0A0C04", "#FF1A1F0A", "#FFF4FFE0", "#FFA4B57A", "#FF2B3A06", "#B6FF1A",
            Stroke: "#40B6FF1A", Tagline: "Acid lime on black", OwnAccent: true, Swatch2: "#FFB6FF1A"),
    };

    public static readonly string[] Accents =
        { "#7C9CFF", "#5FB4FF", "#6FE3B5", "#B6E36F", "#FFD166", "#FFB37A", "#FF7A7A", "#E3122F", "#FF8FAB", "#FF3EA5", "#B79CFF", "#FFFFFF" };

    public static Color AccentColor { get; private set; } = Colors.CornflowerBlue;

    public static Palette Current
    {
        get
        {
            foreach (var p in Palettes) if (p.Name == SettingsStore.Current.Theme) return p;
            return Palettes[0];
        }
    }

    public static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.White; }
    }

    static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static SolidColorBrush B(string hex) => B(Parse(hex));

    public static bool IsLight(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 160;

    /// <summary>Mix two colours (t = 0 gives a, 1 gives b), fully opaque.</summary>
    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>A see-through colour drawn over black (for the Smoke theme's glassy surfaces).</summary>
    static Color Solid(Color c) => Color.FromRgb((byte)(c.R * c.A / 255), (byte)(c.G * c.A / 255), (byte)(c.B * c.A / 255));

    public static void Apply()
    {
        var s = SettingsStore.Current;
        var p = Current;
        var r = Application.Current.Resources;
        AccentColor = Parse(s.Accent);

        r["Brim.Surface"] = B(p.Surface);
        r["Brim.Raised"] = B(p.Raised);
        r["Brim.Text"] = B(p.Text);
        r["Brim.Dim"] = B(p.Dim);
        r["Brim.Hover"] = B("#1FFFFFFF");
        r["Brim.Stroke"] = B(p.Stroke ?? "#26FFFFFF");
        r["Brim.Track"] = B("#33FFFFFF");
        r["Brim.Accent"] = B(AccentColor);
        r["Brim.OnAccent"] = B(IsLight(AccentColor) ? Colors.Black : Colors.White);

        // Settings and Wallpapers windows follow the theme too
        var surface = Solid(Parse(p.Surface));
        var raised = Parse(p.Raised);
        if (raised.A < 255) raised = Mix(surface, Colors.White, raised.A / 255.0 * 0.5);
        bool plainBlack = p.Name == "Black";
        r["Set.Side"] = B(plainBlack ? Color.FromRgb(0x0B, 0x0B, 0x0D) : Mix(surface, Colors.Black, 0.25));
        r["Set.Bg"] = B(plainBlack ? Color.FromRgb(0x11, 0x11, 0x14) : Mix(surface, raised, 0.35));
        r["Set.Card"] = B(plainBlack ? Color.FromRgb(0x1A, 0x1A, 0x1F) : Mix(surface, raised, 0.8));
        r["Set.Field"] = B(plainBlack ? Color.FromRgb(0x25, 0x25, 0x2B) : Mix(raised, Colors.White, 0.07));
        r["Set.Line"] = B(p.Stroke != null ? Mix(raised, Parse(p.Stroke), 0.35) : plainBlack ? Color.FromRgb(0x2A, 0x2A, 0x31) : Mix(raised, Colors.White, 0.1));

        switch (s.WidgetStyle)
        {
            case "Solid":
                r["Brim.WidgetBg"] = B(p.Surface == "#FF000000" ? "#FF141417" : p.Surface);
                r["Brim.WidgetStroke"] = B(p.Stroke ?? "#1AFFFFFF");
                r["Brim.WidgetText"] = B(p.Text);
                r["Brim.WidgetDim"] = B(p.Dim);
                break;
            case "Light":
                r["Brim.WidgetBg"] = B("#D9F7F7FA");
                r["Brim.WidgetStroke"] = B("#33FFFFFF");
                r["Brim.WidgetText"] = B("#FF16161A");
                r["Brim.WidgetDim"] = B("#FF5E5E66");
                break;
            case "Bare":
                r["Brim.WidgetBg"] = B("#01000000");
                r["Brim.WidgetStroke"] = B("#00000000");
                r["Brim.WidgetText"] = B("#FFFFFFFF");
                r["Brim.WidgetDim"] = B("#D9FFFFFF");
                break;
            default: // Glass
                r["Brim.WidgetBg"] = B("#8C0E0E12");
                r["Brim.WidgetStroke"] = B("#2EFFFFFF");
                r["Brim.WidgetText"] = B("#FFF5F5F7");
                r["Brim.WidgetDim"] = B("#B3F5F5F7");
                break;
        }
    }
}
