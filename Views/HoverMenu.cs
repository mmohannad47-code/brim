using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Views;

/// <summary>
/// One rule for every menu in Brim: it stays while the pointer is on it (or one of its sub-menus) or on the thing it
/// was opened from, and closes itself about half a second after the pointer has left all of them. No more menus
/// left hanging until you click somewhere.
///
/// "The thing it was opened from" is, in order: the menu's own <see cref="OwnerProperty"/> (e.g. a dock icon), the
/// <see cref="RegionProperty"/> of the window it belongs to (the notch's visible card), or that whole window.
/// </summary>
public static class HoverMenu
{
    /// <summary>The element this menu belongs to (overrides the window's region).</summary>
    public static readonly DependencyProperty OwnerProperty =
        DependencyProperty.RegisterAttached("Owner", typeof(FrameworkElement), typeof(HoverMenu));
    public static void SetOwner(ContextMenu m, FrameworkElement? e) => m.SetValue(OwnerProperty, e);

    /// <summary>The part of a window that counts as "still on it" for its menus (the notch: its visible card).</summary>
    public static readonly DependencyProperty RegionProperty =
        DependencyProperty.RegisterAttached("Region", typeof(FrameworkElement), typeof(HoverMenu));
    public static void SetRegion(Window w, FrameworkElement? e) => w.SetValue(RegionProperty, e);

    /// <summary>Give every context menu in the app this behaviour (once, at start-up).</summary>
    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnOpened));

    static void OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || e.OriginalSource != menu) return;
        var owner = menu.GetValue(OwnerProperty) as FrameworkElement;
        if (owner == null && menu.PlacementTarget is DependencyObject t && Window.GetWindow(t) is { } win)
            owner = win.GetValue(RegionProperty) as FrameworkElement ?? win;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var openedAt = DateTime.Now;
        int away = 0;
        timer.Tick += (_, _) =>
        {
            if (!menu.IsOpen) { timer.Stop(); return; }
            if (DateTime.Now - openedAt < TimeSpan.FromMilliseconds(300)) return; // time to move onto it
            Native.GetCursorPos(out var pt);
            bool over = OverMenu(menu, pt) || (owner != null && Over(owner, pt));
            away = over ? 0 : away + 1;
            if (away < 2) return; // a fifth of a second away from all of it: snappy, but forgives a slip
            timer.Stop();
            menu.IsOpen = false;
        };
        menu.Closed += (_, _) => timer.Stop();
        timer.Start();
    }

    /// <summary>The pointer (screen pixels) is over this element.</summary>
    internal static bool Over(FrameworkElement el, Native.POINT pt)
    {
        try
        {
            if (!el.IsVisible || PresentationSource.FromVisual(el) == null) return false;
            var tl = el.PointToScreen(new Point(0, 0));
            var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            return pt.X >= Math.Min(tl.X, br.X) && pt.X < Math.Max(tl.X, br.X) && pt.Y >= Math.Min(tl.Y, br.Y) && pt.Y < Math.Max(tl.Y, br.Y);
        }
        catch { return false; }
    }

    /// <summary>Over the menu's own pop-up, or any of its open sub-menus.</summary>
    static bool OverMenu(ItemsControl menu, Native.POINT pt)
    {
        if (PresentationSource.FromVisual(menu) is HwndSource src && Inside(src.Handle, pt)) return true;
        foreach (var item in menu.Items.OfType<object>().Select(i => menu.ItemContainerGenerator.ContainerFromItem(i)).OfType<MenuItem>())
        {
            if (!item.IsSubmenuOpen) continue;
            // The sub-menu draws in its own pop-up: find it through one of its items
            var first = item.Items.Cast<object>().Select(i => item.ItemContainerGenerator.ContainerFromItem(i)).OfType<FrameworkElement>().FirstOrDefault();
            if (first != null && PresentationSource.FromVisual(first) is HwndSource sub && Inside(sub.Handle, pt)) return true;
            if (OverMenu(item, pt)) return true;
        }
        return false;
    }

    static bool Inside(IntPtr hwnd, Native.POINT pt) =>
        Native.GetWindowRect(hwnd, out var r) && pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
}
