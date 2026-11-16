using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Views;

/// <summary>
/// Live previews of an app's windows when you point at it on the dock, like Windows' taskbar.
/// The pictures are drawn by Windows itself (DWM thumbnails), so they stay live and cost almost nothing.
/// </summary>
public sealed class WindowPreview : Window
{
    static WindowPreview? current;
    static readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    [StructLayout(LayoutKind.Sequential)]
    struct THUMBPROPS
    {
        public int dwFlags;
        public Native.RECT rcDestination;
        public Native.RECT rcSource;
        public byte opacity;
        public bool fVisible;
        public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)] struct PSIZE { public int x, y; }

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMBPROPS props);

    readonly List<(AppWindow win, Border slot, IntPtr thumb)> items = new();
    readonly FrameworkElement anchor;
    IntPtr hwnd;

    static WindowPreview() => hideTimer.Tick += (_, _) =>
    {
        hideTimer.Stop();
        if (current != null && !current.IsMouseOver) current.Close();
    };

    WindowPreview(DockApp app, FrameworkElement anchor)
    {
        this.anchor = anchor;
        appIcon = app.Icon;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -10000;
        Top = -10000;
        FontFamily = (FontFamily)FindResource("Brim.Font");
        SetResourceReference(BackgroundProperty, "Brim.Raised");
        SetResourceReference(ForegroundProperty, "Brim.Text");
        UseLayoutRounding = true;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3) };
        var shown = app.Windows.GetRange(0, Math.Min(app.Windows.Count, 5));
        var sizes = SlotsFor(shown.Select(x => x.Hwnd).ToList());
        for (int n = 0; n < shown.Count; n++)
        {
            var w = shown[n];
            var win = w;
            var (sw, sh) = sizes[n];
            var card = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(5), Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top };
            var stack = new StackPanel();

            // The title row is exactly as wide as the picture (long titles are trimmed), so the card hugs the picture
            var head = new Grid { Margin = new Thickness(1, 0, 0, 4), Width = sw };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (app.Icon != null) head.Children.Add(new Image { Source = app.Icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) });
            var title = new TextBlock { Text = w.Title, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1);
            head.Children.Add(title);
            var close = new Button { Content = "", Width = 20, Height = 20, FontSize = 9, ToolTip = "Close window" };
            close.SetResourceReference(StyleProperty, "CloseButton");
            close.Click += (_, _) => { Native.PostMessage(win.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); Close(); };
            Grid.SetColumn(close, 2);
            head.Children.Add(close);
            stack.Children.Add(head);

            // Windows draws the live picture into this area, shaped like the window (tall ones like a friends list
            // get a tall preview instead of a thin strip in a wide box)
            // (see-through but there for the pointer: peek starts over the picture, not over the title or its close button)
            var slot = new Border { Width = sw, Height = sh, Background = Brushes.Transparent };
            slot.MouseEnter += (_, _) => PeekSoon(win.Hwnd);
            slot.MouseLeave += (_, _) => EndPeek();
            stack.Children.Add(slot);

            card.Child = stack;
            card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "Brim.Hover");
            card.MouseLeave += (_, _) => { card.Background = Brushes.Transparent; EndPeek(); };
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInside(d, close)) return;
                Native.ForceForeground(win.Hwnd);
                Close();
            };
            row.Children.Add(card);
            items.Add((w, slot, IntPtr.Zero));
        }
        Content = row;

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, true);
            int round = 2; // rounded corners on Windows 11
            Native.DwmSetWindowAttribute(hwnd, 33, ref round, 4);
        };
        ContentRendered += (_, _) => { Place(); Attach(); };
        MouseLeave += (_, _) => HideSoon();
        MouseEnter += (_, _) => hideTimer.Stop();
        Closed += (_, _) =>
        {
            EndPeek();
            foreach (var (_, _, t) in items) if (t != IntPtr.Zero) DwmUnregisterThumbnail(t);
            if (current == this) current = null;
        };
    }

    // ---------- peek: point at a preview for a moment and every other window turns see-through, showing that one in place
    // (Windows' own "peek", through the same DWM call its taskbar uses)

    [DllImport("dwmapi.dll", EntryPoint = "#113")]
    static extern int DwmpActivateLivePreview(uint peekOn, IntPtr peekWindow, IntPtr topmostWindow, uint peekType, IntPtr reserved);

    readonly System.Windows.Threading.DispatcherTimer peekDelay = new() { Interval = TimeSpan.FromMilliseconds(550) };
    IntPtr peekTarget;
    bool peeking;
    static bool peekBroken;

    void PeekSoon(IntPtr target)
    {
        if (peekBroken) return; // (minimised windows too: Windows shows the last picture of them, where they open)
        peekTarget = target;
        peekDelay.Stop();
        peekDelay.Tick -= PeekNow;
        peekDelay.Tick += PeekNow;
        peekDelay.Start();
    }

    void PeekNow(object? s, EventArgs e)
    {
        peekDelay.Stop();
        if (peekTarget == IntPtr.Zero || hwnd == IntPtr.Zero) return;
        try
        {
            // The preview itself stays on top, so you can move on to the next window or click this one
            int hr = DwmpActivateLivePreview(1, peekTarget, hwnd, 1, IntPtr.Zero);
            if (hr != 0) Log.Note("preview", $"Windows wouldn't peek at '{Native.TitleOf(peekTarget)}' ({Native.ClassOf(peekTarget)}): 0x{hr:X8}");
            peeking = true;
        }
        catch (Exception ex) { peekBroken = true; Log.Note("preview", "peek isn't available on this Windows: " + ex.Message); }
    }

    void EndPeek()
    {
        peekDelay.Stop();
        peekTarget = IntPtr.Zero;
        if (!peeking) return;
        peeking = false;
        try { DwmpActivateLivePreview(0, IntPtr.Zero, IntPtr.Zero, 1, IntPtr.Zero); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT { public int length, flags, showCmd; public Native.POINT min, max; public Native.RECT normal; }
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT r, int size);

    /// <summary>
    /// How much of a window is invisible resize border (left, top, right, bottom), as a share of its size. Windows
    /// includes that border in the live picture, which showed up as empty edges. Remembered per window, because a
    /// minimised window can't be measured.
    /// </summary>
    static readonly Dictionary<IntPtr, (double l, double t, double r, double b)> borders = new();

    static (double l, double t, double r, double b) BordersOf(IntPtr hwnd)
    {
        if (!Native.IsIconic(hwnd) && Native.GetWindowRect(hwnd, out var wr) &&
            DwmGetWindowAttribute(hwnd, 9 /* visible frame */, out var vis, Marshal.SizeOf<Native.RECT>()) == 0)
        {
            double w = Math.Max(1, wr.Right - wr.Left), h = Math.Max(1, wr.Bottom - wr.Top);
            var b = (Math.Max(0, vis.Left - wr.Left) / w, Math.Max(0, vis.Top - wr.Top) / h,
                     Math.Max(0, wr.Right - vis.Right) / w, Math.Max(0, wr.Bottom - vis.Bottom) / h);
            if (b.Item1 + b.Item3 < 0.2 && b.Item2 + b.Item4 < 0.2) { borders[hwnd] = b; return b; }
        }
        return borders.TryGetValue(hwnd, out var known) ? known : (0, 0, 0, 0);
    }

    /// <summary>The visible window's width ÷ height.</summary>
    static double AspectOf(IntPtr hwnd)
    {
        try
        {
            Native.RECT r;
            if (Native.IsIconic(hwnd))
            {
                var p = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                GetWindowPlacement(hwnd, ref p);
                r = p.normal;
                // Minimised from maximised (Chrome while you're on the desktop): Windows' last picture of it is the
                // maximised window, so its shape is its screen's, not the smaller size it would restore to
                if ((p.flags & 0x2 /* WPF_RESTORETOMAXIMIZED */) != 0)
                {
                    var mon = Native.MonitorFromPoint(new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 }, 2);
                    var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
                    if (Native.GetMonitorInfo(mon, ref mi))
                    {
                        double mw = mi.rcWork.Right - mi.rcWork.Left, mh = mi.rcWork.Bottom - mi.rcWork.Top;
                        if (mw > 50 && mh > 50) return mw / mh;
                    }
                }
                // Otherwise the size it has when restored, without its invisible border
                var (l, t, rr, b) = BordersOf(hwnd);
                double ww = (r.Right - r.Left) * (1 - l - rr), hh = (r.Bottom - r.Top) * (1 - t - b);
                if (ww > 50 && hh > 50) return ww / hh;
            }
            else if (DwmGetWindowAttribute(hwnd, 9, out r, Marshal.SizeOf<Native.RECT>()) == 0 || Native.GetWindowRect(hwnd, out r))
            {
                double w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w > 50 && h > 50) return w / h;
            }
        }
        catch { }
        return 16.0 / 10;
    }

    /// <summary>
    /// Preview sizes (DIPs), each exactly its window's shape so the picture fills it edge to edge.
    /// One window: about the same amount of picture whatever its shape (a squarer window like Steam's used to come
    /// out much taller than a wide one like Chrome's). Several (Steam's main window and its friends list): all the
    /// same height, like Windows' own previews, each as wide as its shape needs.
    /// </summary>
    static List<(double w, double h)> SlotsFor(List<IntPtr> windows)
    {
        var aspects = windows.Select(h => Math.Clamp(AspectOf(h), 0.25, 4)).ToList();
        var result = new List<(double, double)>();
        if (aspects.Count == 1)
        {
            const double area = 210 * 124; // a maximised window on a 16:10 screen: 210 × 124
            double a = aspects[0], w = Math.Sqrt(area * a), h = Math.Sqrt(area / a);
            if (w > 240) { w = 240; h = w / a; }
            if (h > 220) { h = 220; w = h * a; }
            if (w < 90) { w = 90; h = w / a; }   // very tall: room for the title
            if (h < 84) { h = 84; w = h * a; }   // very wide: still a picture you can make out
            result.Add((Math.Round(w), Math.Round(h)));
            return result;
        }
        // A shared height: taller when a tall window is among them, so it doesn't end up a sliver
        double row = aspects.Any(a => a < 1) ? 140 : 112;
        foreach (var a in aspects)
        {
            double w = row * a, h = row;
            if (w > 340) { w = 340; h = w / a; }      // extremely wide (ultrawide windows): a little shorter
            if (w < 80) { w = 80; h = w / a; }        // very tall: room for the title (a little taller)
            result.Add((Math.Round(w), Math.Round(h)));
        }
        return result;
    }

    static bool IsInside(DependencyObject d, DependencyObject target)
    {
        while (d != null) { if (d == target) return true; d = VisualTreeHelper.GetParent(d); }
        return false;
    }

    public static void ShowFor(DockApp app, FrameworkElement anchor)
    {
        hideTimer.Stop();
        if (app.Windows.Count == 0) { current?.Close(); return; }
        if (current != null)
        {
            if (current.anchor == anchor) return;
            current.Close();
        }
        current = new WindowPreview(app, anchor);
        current.Show();
    }

    public static void HideSoon()
    {
        hideTimer.Stop();
        hideTimer.Start();
    }

    public static void HideNow() => current?.Close();

    readonly System.Windows.Media.ImageSource? appIcon;

    void ShowIcon(Border slot)
    {
        if (appIcon != null) slot.Child = new Image { Source = appIcon, Width = 48, Height = 48, Opacity = 0.8 };
    }

    internal static string Debug = "";
    internal static WindowPreview? Current => current;

    /// <summary>Developer check: show a real preview for a running app, screenshot it, and quit.</summary>
    public static async System.Threading.Tasks.Task SelfTest(string png)
    {
        var all = WindowTracker.TaskbarWindows();
        var win = all.FirstOrDefault(w => Native.IsIconic(w.Hwnd)) ?? all.FirstOrDefault();
        Window? tall = null;
        var mode = Environment.GetEnvironmentVariable("BRIM_PREVIEW_TALL"); var wide = win;
        if (mode is "1" or "2")
        {
            // A tall window shaped like a friends list, to check the preview keeps its shape
            tall = new Window { Title = "Friends (test)", Width = 300, Height = 720, Left = 40, Top = 40, ShowActivated = false, Background = new LinearGradientBrush(Colors.SteelBlue, Colors.MidnightBlue, 90) };
            tall.Content = new TextBlock { Text = "Friends\n\nAlex\nSam\nJordan\nTaylor", Foreground = Brushes.White, FontSize = 22, Margin = new Thickness(20) };
            tall.Show();
            await System.Threading.Tasks.Task.Delay(400);
            win = new AppWindow { Hwnd = new WindowInteropHelper(tall).Handle, Title = tall.Title };
        }
        if (mode == "3")
        {
            // Maximised, then minimised (Chrome while you're on the desktop): the preview must have the maximised shape
            tall = new Window { Title = "Maximised then minimised (test)", Width = 500, Height = 600, Left = 40, Top = 40, ShowActivated = false, Background = new LinearGradientBrush(Colors.DarkOrange, Colors.Purple, 0) };
            tall.Content = new TextBlock { Text = "Maximised window", Foreground = Brushes.White, FontSize = 40, Margin = new Thickness(40) };
            tall.Show();
            tall.WindowState = WindowState.Maximized;
            await System.Threading.Tasks.Task.Delay(700);
            tall.WindowState = WindowState.Minimized;
            await System.Threading.Tasks.Task.Delay(700);
            win = new AppWindow { Hwnd = new WindowInteropHelper(tall).Handle, Title = tall.Title };
        }
        if (win == null) { System.IO.File.WriteAllText(png + ".txt", "no window"); return; }
        var app = new DockApp { Key = "test", Title = win.Title, Icon = win.ExePath != null ? IconHelper.ForPath(win.ExePath) : null, Windows = mode == "2" && wide != null ? new List<AppWindow> { wide, win } : new List<AppWindow> { win } };
        var host = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true, ShowActivated = false, Width = 40, Height = 40 };
        var anchor = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        host.Content = anchor;
        host.Left = SystemParameters.PrimaryScreenWidth / 2 - 20;
        host.Top = SystemParameters.PrimaryScreenHeight - 120;
        host.Show();
        await System.Threading.Tasks.Task.Delay(200);
        ShowFor(app, anchor);
        await System.Threading.Tasks.Task.Delay(1500);
        var p = current;
        string rect = "";
        if (p != null && PresentationSource.FromVisual(p) != null)
        {
            var tl = p.PointToScreen(new Point(0, 0));
            var br = p.PointToScreen(new Point(p.ActualWidth, p.ActualHeight));
            rect = $"{(int)tl.X} {(int)tl.Y} {(int)(br.X - tl.X)} {(int)(br.Y - tl.Y)}";
        }
        System.IO.File.WriteAllText(png + ".txt", rect + "\n" + Debug);
        await System.Threading.Tasks.Task.Delay(2500); // time for the screenshot
        HideNow();
        host.Close();
        tall?.Close();
    }

    void Place()
    {
        if (PresentationSource.FromVisual(anchor) is not { CompositionTarget: { } ct }) return;
        var side = DockWindow.SideFor(anchor);
        if (side != "Bottom")
        {
            // A dock down a side: the previews open beside it, level with the app
            var edge = ct.TransformFromDevice.Transform(anchor.PointToScreen(new Point(side == "Left" ? anchor.ActualWidth : 0, anchor.ActualHeight / 2)));
            Left = side == "Left" ? edge.X + 10 : edge.X - ActualWidth - 10;
            Top = Math.Clamp(edge.Y - ActualHeight / 2, SystemParameters.VirtualScreenTop + 8, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight - 8);
            return;
        }
        var dip = ct.TransformFromDevice.Transform(anchor.PointToScreen(new Point(anchor.ActualWidth / 2, 0)));
        Left = Math.Clamp(dip.X - ActualWidth / 2, SystemParameters.VirtualScreenLeft + 8, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth - 8);
        Top = Math.Max(8, dip.Y - ActualHeight - 10);
    }

    void Attach()
    {
        if (PresentationSource.FromVisual(this) is not { CompositionTarget: { } ct }) return;
        var toDevice = ct.TransformToDevice;
        for (int i = 0; i < items.Count; i++)
        {
            var (win, slot, _) = items[i];
            // Minimised windows too: Windows keeps their last picture. Only if it has nothing usable, show the app's icon.
            int hr = DwmRegisterThumbnail(hwnd, win.Hwnd, out var thumb);
            if (hr != 0) { Debug += $"[{win.Title}: register failed 0x{hr:X8}] "; ShowIcon(slot); continue; }
            DwmQueryThumbnailSourceSize(thumb, out var src);
            Debug += $"[{win.Title}: source {src.x}x{src.y}{(Native.IsIconic(win.Hwnd) ? " minimised" : "")}] ";
            if (src.x < 200 || src.y < 120)
            {
                DwmUnregisterThumbnail(thumb);
                ShowIcon(slot);
                continue;
            }

            // Only the visible window (not its invisible resize border), drawn to fill the slot exactly: the slot
            // already has the window's shape, so nothing is stretched and no empty edges show
            var (bl, bt, br, bb) = BordersOf(win.Hwnd);
            var source = new Native.RECT
            {
                Left = (int)Math.Round(src.x * bl) + 1, Top = (int)Math.Round(src.y * bt) + 1,
                Right = src.x - (int)Math.Round(src.x * br) - 1, Bottom = src.y - (int)Math.Round(src.y * bb) - 1,
            };
            var topLeft = toDevice.Transform(slot.TransformToAncestor(this).Transform(new Point(0, 0)));
            var size = toDevice.Transform(new Point(slot.ActualWidth, slot.ActualHeight));
            Debug += $"[slot {slot.ActualWidth:0}x{slot.ActualHeight:0}] ";
            // Never stretch: if the picture's shape still differs from the slot's (a window that changed size since
            // its last picture), fit it inside, centred, instead of squashing it
            double pictureAspect = (double)(source.Right - source.Left) / Math.Max(1, source.Bottom - source.Top);
            double slotAspect = size.X / Math.Max(1, size.Y);
            if (Math.Abs(pictureAspect / slotAspect - 1) > 0.03)
            {
                var fitted = pictureAspect > slotAspect ? new Point(size.X, size.X / pictureAspect) : new Point(size.Y * pictureAspect, size.Y);
                topLeft = new Point(topLeft.X + (size.X - fitted.X) / 2, topLeft.Y + (size.Y - fitted.Y) / 2);
                size = fitted;
                Debug += $"[{win.Title}: fitted, picture {pictureAspect:0.00} vs slot {slotAspect:0.00}] ";
            }
            int x = (int)Math.Round(topLeft.X), y = (int)Math.Round(topLeft.Y);

            var props = new THUMBPROPS
            {
                dwFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10, // destination, source, opacity, visible, client area only
                rcDestination = new Native.RECT { Left = x, Top = y, Right = x + (int)Math.Round(size.X), Bottom = y + (int)Math.Round(size.Y) },
                rcSource = source,
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            DwmUpdateThumbnailProperties(thumb, ref props);
            items[i] = (win, slot, thumb);
        }
    }
}
