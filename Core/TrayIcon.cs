using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Brim.Core;

/// <summary>Brim's own icon in the notification area, done with Shell_NotifyIcon so WinForms never loads.</summary>
public sealed class TrayIcon : IDisposable
{
    const uint CallbackMsg = Native.WM_USER + 77;
    readonly HwndSource src;
    readonly App app;
    readonly IntPtr hIcon;

    static AppSettings S => SettingsStore.Current;

    public TrayIcon(App app)
    {
        this.app = app;
        src = new HwndSource(new HwndSourceParameters("BrimTrayIcon") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        src.AddHook(Hook);
        hIcon = MakeIcon();
        Add();
        TaskbarManager.ExplorerRestarted += Readd;
    }

    Native.NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = src.Handle,
        uID = 1,
        uFlags = 1 | 2 | 4, // message, icon, tip
        uCallbackMessage = CallbackMsg,
        hIcon = hIcon,
        szTip = "Brim",
        szInfo = "",
        szInfoTitle = "",
    };

    void Add()
    {
        var d = Data();
        Native.Shell_NotifyIcon(0, ref d);
    }

    public void Readd()
    {
        var d = Data();
        Native.Shell_NotifyIcon(2, ref d);
        Native.Shell_NotifyIcon(0, ref d);
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg != CallbackMsg) return IntPtr.Zero;
        int e = (int)(l.ToInt64() & 0xFFFF);
        if (e == Native.WM_LBUTTONUP) app.OpenSettings();
        else if (e == Native.WM_RBUTTONUP) ShowMenu();
        handled = true;
        return IntPtr.Zero;
    }

    /// <param name="near">Brim's icon in the dock's tray panel, when opened from there: the menu sits next to it and
    /// belongs to that panel (it stays while the pointer is on either)</param>
    public void ShowMenu(UIElement? near = null)
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        if (near != null) { menu.PlacementTarget = near; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; }
        menu.Items.Add(Item("Open notch", () => app.Notch?.ToggleOpen()));
        menu.Items.Add(Item("Settings", () => app.OpenSettings()));
        menu.Items.Add(Item("Wallpapers", () => app.OpenWallpapers()));
        menu.Items.Add(Item(S.HideDesktopIcons ? "Show desktop icons" : "Hide desktop icons", () =>
        {
            S.HideDesktopIcons = !S.HideDesktopIcons;
            SettingsStore.Save();
            app.ApplyAll();
            app.RefreshSettingsWindow();
        }));
        menu.Items.Add(new Separator { Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Margin = new Thickness(6, 3, 6, 3) });
        if (Updater.Now == Updater.State.Ready)
            menu.Items.Add(Item($"Restart to update to Brim {Updater.NewVersion}", () => app.InstallUpdate()));
        if (S.Taskbar != TaskbarMode.Windows)
            menu.Items.Add(Item("Use Windows' taskbar again", () =>
            {
                S.Taskbar = TaskbarMode.Windows;
                SettingsStore.Save();
                app.ApplyAll();
                app.RefreshSettingsWindow();
            }));
        menu.Items.Add(Item("Quit Brim", () => app.Quit()));
        Brim.Views.Flyout.CloseBrimMenus = () => menu.IsOpen = false; // the tray panel closes it when you move away
        menu.Opened += (_, _) =>
        {
            // A popup only closes on outside clicks when its window is in front
            if (PresentationSource.FromVisual(menu) is HwndSource ps) Native.SetForegroundWindow(ps.Handle);
        };
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, Action a)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => a();
        return mi;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    /// <summary>Brim's logo from its own exe; falls back to a drawn pill if that fails.</summary>
    static IntPtr MakeIcon()
    {
        try
        {
            var small = new IntPtr[1];
            if (Environment.ProcessPath != null && ExtractIconEx(Environment.ProcessPath, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
                return small[0];
        }
        catch { }
        return DrawIcon();
    }

    static IntPtr DrawIcon()
    {
        const int s = 32;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(200, 30, 30, 34)), 1.5), new Rect(3, 10, 26, 12), 6, 6);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(124, 156, 255)), null, new Point(23, 16), 3, 3);
        }
        var rtb = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[s * s * 4];
        rtb.CopyPixels(px, s * 4, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a is > 0 and < 255)
            {
                px[i] = (byte)Math.Min(255, px[i] * 255 / a);
                px[i + 1] = (byte)Math.Min(255, px[i + 1] * 255 / a);
                px[i + 2] = (byte)Math.Min(255, px[i + 2] * 255 / a);
            }
        }
        var color = Native.CreateBitmap(s, s, 1, 32, px);
        var mask = Native.CreateBitmap(s, s, 1, 1, new byte[s * s / 8]);
        var info = new Native.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = Native.CreateIconIndirect(ref info);
        Native.DeleteObject(color);
        Native.DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        TaskbarManager.ExplorerRestarted -= Readd;
        var d = Data();
        Native.Shell_NotifyIcon(2, ref d);
        Native.DestroyIcon(hIcon);
        src.Dispose();
    }
}
