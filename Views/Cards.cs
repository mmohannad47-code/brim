using System;
using System.Windows;
using System.Windows.Controls;
using Brim.Core;

namespace Brim.Views;

/// <summary>
/// The notch's one card look: everything below the tab bar sits on cards of the theme's raised colour, so it reads
/// the same on a solid notch and on glass over any wallpaper.
/// </summary>
public static class Cards
{
    public const double Radius = 18;

    public static Border Make(UIElement? child = null, Thickness? padding = null)
    {
        var b = new Border { CornerRadius = new CornerRadius(Radius), Padding = padding ?? new Thickness(12), Child = child };
        b.SetResourceReference(Border.BackgroundProperty, "Brim.Card");
        Lift(b);
        return b;
    }

    /// <summary>
    /// Inside a card, what normally uses the raised colour (pills, keys, the selected tab) takes a lighter shade so it
    /// still stands out from the card; it follows theme changes.
    /// </summary>
    public static void Lift(FrameworkElement e)
    {
        void Set() => e.Resources["Brim.Raised"] = Application.Current.Resources["Brim.Lift"];
        Set();
        Action refresh = Set;
        e.Loaded += (_, _) => { ThemeManager.Applied -= refresh; ThemeManager.Applied += refresh; Set(); };
        e.Unloaded += (_, _) => ThemeManager.Applied -= refresh;
    }
}
