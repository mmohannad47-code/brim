using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Views;

/// <summary>
/// Hover labels for Brim's always-on-top pieces (dock, notch, panels). Normal tooltips there pop up on top of the
/// dock, get covered when it re-asserts itself on top, and can inherit the icon font. These sit clear of the bar
/// (above the dock, below the notch) and stay in front.
/// </summary>
public sealed class HoverTip : Window
{
    static HoverTip? current;
    static FrameworkElement? target;
    static readonly DispatcherTimer showDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly TextBlock text = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5)), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };

    public static bool IsShowing => current != null;

    HoverTip()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;
        Left = -10000;
        Top = -10000;
        FontFamily = (FontFamily)Application.Current.FindResource("Brim.Font");
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 5, 9, 6),
            Child = text,
        };
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle, true);
        ContentRendered += (_, _) => Place();
    }

    /// <summary>
    /// Route every tooltip inside Brim's floating windows through this instead of the standard popup.
    /// </summary>
    public static void Install()
    {
        showDelay.Tick += (_, _) => { showDelay.Stop(); ShowNow(); };
        // Watch the pointer entering anything with a tooltip inside Brim's floating windows
        EventManager.RegisterClassHandler(typeof(FrameworkElement), UIElement.MouseEnterEvent,
            new System.Windows.Input.MouseEventHandler((s, e) =>
            {
                if (s is not FrameworkElement el || el.ToolTip == null) return;
                var win = Window.GetWindow(el);
                if (win is not (NotchWindow or DockWindow or Flyout or WindowPreview)) return;
                ToolTipService.SetIsEnabled(el, false); // never the standard popup here
                Begin(el);
            }));
    }

    static void Begin(FrameworkElement el)
    {
        if (target == el && current != null) return;
        Hide(null);
        target = el;
        el.MouseLeave += OnLeave;
        el.PreviewMouseDown += OnLeave;
        showDelay.Stop();
        showDelay.Start();
    }

    static void OnLeave(object? s, EventArgs e) => Hide(s as FrameworkElement);

    static void Hide(FrameworkElement? from)
    {
        if (from != null && from != target) return;
        showDelay.Stop();
        if (target != null)
        {
            target.MouseLeave -= OnLeave;
            target.PreviewMouseDown -= OnLeave;
        }
        target = null;
        current?.Close();
        current = null;
    }

    static void ShowNow()
    {
        if (target == null || !target.IsMouseOver) { Hide(null); return; }
        var content = target.ToolTip switch
        {
            string s => s,
            ToolTip { Content: string s } => s,
            TextBlock tb => tb.Text,
            null => null,
            var o => o.ToString(),
        };
        if (string.IsNullOrWhiteSpace(content)) return;
        current?.Close();
        current = new HoverTip();
        current.text.Text = content;
        current.Show();
    }

    void Place()
    {
        if (target == null || PresentationSource.FromVisual(target) is not { CompositionTarget: { } ct }) { Close(); return; }
        var from = ct.TransformFromDevice;
        var topLeft = from.Transform(target.PointToScreen(new Point(0, 0)));
        var bottomRight = from.Transform(target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight)));
        double centre = (topLeft.X + bottomRight.X) / 2;
        bool upper = topLeft.Y < SystemParameters.PrimaryScreenHeight / 2;
        // Below things at the top of the screen (the notch), above things at the bottom (the dock)
        double y = upper ? bottomRight.Y + 8 : topLeft.Y - ActualHeight - 10;
        Left = Math.Clamp(centre - ActualWidth / 2, SystemParameters.VirtualScreenLeft + 6, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth - 6);
        Top = Math.Clamp(y, SystemParameters.VirtualScreenTop + 4, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight - 4);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
