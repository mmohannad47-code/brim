using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Brim.Core;

namespace Brim.Views;

public sealed record SearchResult(string Name, string Kind, string? Glyph, Func<ImageSource?>? Icon, Action Run, string? Detail = null);

/// <summary>Ctrl+Space: find and open apps, games, settings, recent files and Brim actions; do quick sums.</summary>
public sealed class SearchWindow : Window
{
    static SearchWindow? open;
    static List<(string name, string target)>? appsCache;
    static DateTime appsCachedAt;

    readonly TextBox box = new() { FontSize = 20, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White };
    readonly TextBlock hint = new() { Text = "Search apps, games, settings and files…", FontSize = 20, IsHitTestVisible = false };
    readonly StackPanel list = new();
    List<SearchResult> results = new();
    int selected;

    static AppSettings S => SettingsStore.Current;

    public static void Toggle()
    {
        if (open != null) { open.Close(); return; }
        open = new SearchWindow();
        open.Show();
    }

    /// <summary>For the snapshot test: a search window with some text typed, kept off-screen.</summary>
    internal static SearchWindow Preview(string text)
    {
        var w = new SearchWindow(preview: true) { Left = -4000, Top = 0 };
        w.box.Text = text;
        return w;
    }

    readonly bool preview;
    bool closing;

    SearchWindow(bool preview = false)
    {
        this.preview = preview;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 660;
        SizeToContent = SizeToContent.Height;
        FontFamily = (FontFamily)FindResource("Brim.Font");
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = SystemParameters.PrimaryScreenHeight * 0.18;

        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        var field = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.ColumnDefinitions.Add(new ColumnDefinition());
        var glass = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        glass.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        field.Children.Add(glass);
        var stack = new Grid();
        stack.Children.Add(hint);
        stack.Children.Add(box);
        Grid.SetColumn(stack, 1);
        field.Children.Add(stack);

        var panel = new StackPanel();
        panel.Children.Add(field);
        panel.Children.Add(list);
        var border = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, 0, 0, 8),
            Child = panel,
        };
        border.SetResourceReference(Border.BackgroundProperty, "Brim.Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Brim.Stroke");
        if (ThemeManager.Parse(ThemeManager.Current.Surface).A < 200) border.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18));
        Content = border;

        box.TextChanged += (_, _) => { hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Update(); };
        PreviewKeyDown += OnKey;
        Closing += (_, _) => closing = true;
        Deactivated += (_, _) => { if (!this.preview && !closing) Close(); };
        Closed += (_, _) => { if (open == this) open = null; ((App)Application.Current).ScheduleTrim(); };
        ContentRendered += (_, _) =>
        {
            if (this.preview) return; // never take focus during the snapshot test
            var h = new WindowInteropHelper(this).Handle;
            Activate();
            if (Native.GetForegroundWindow() != h) Native.ForceForeground(h);
            box.Focus();
        };
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle, false);
        Update();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Down: Select(selected + 1); e.Handled = true; break;
            case Key.Up: Select(selected - 1); e.Handled = true; break;
            case Key.Enter: RunSelected(); e.Handled = true; break;
        }
    }

    void RunSelected()
    {
        if (selected < 0 || selected >= results.Count) return;
        var r = results[selected];
        if (!closing) Close();
        try { r.Run(); } catch (Exception ex) { Log.Error("search run", ex); }
    }

    void Select(int i)
    {
        if (results.Count == 0) return;
        selected = (i + results.Count) % results.Count;
        for (int k = 0; k < list.Children.Count; k++)
            if (list.Children[k] is Border row)
            {
                if (k == selected) row.SetResourceReference(Border.BackgroundProperty, "Brim.Hover");
                else row.Background = Brushes.Transparent;
            }
    }

    void Update()
    {
        var q = box.Text.Trim();
        results = q.Length == 0 ? Suggestions() : Find(q);
        list.Children.Clear();
        for (int i = 0; i < results.Count; i++)
        {
            int index = i;
            var r = results[i];
            var row = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(8, 0, 8, 2), Padding = new Thickness(10, 7, 12, 7), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = r.Icon?.Invoke();
            if (icon != null) g.Children.Add(new Image { Source = icon, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left });
            else
            {
                var gl = new TextBlock { Text = r.Glyph ?? "", FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
                gl.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Text");
                g.Children.Add(gl);
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = r.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Text");
            text.Children.Add(name);
            if (r.Detail != null)
            {
                var d = new TextBlock { Text = r.Detail, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
                d.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
                text.Children.Add(d);
            }
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            var kind = new TextBlock { Text = r.Kind, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
            Grid.SetColumn(kind, 2);
            g.Children.Add(kind);
            row.Child = g;
            row.MouseEnter += (_, _) => Select(index);
            row.MouseLeftButtonUp += (_, _) => { selected = index; RunSelected(); };
            list.Children.Add(row);
        }
        selected = 0;
        Select(0);
    }

    // ------------------------------------------------------------------ sources

    static readonly (string name, string uri, string glyph)[] SettingsPages =
    {
        ("Wi-Fi settings", "ms-settings:network-wifi", ""), ("Bluetooth & devices", "ms-settings:bluetooth", ""),
        ("Display settings", "ms-settings:display", ""), ("Sound settings", "ms-settings:sound", ""),
        ("Apps & features", "ms-settings:appsfeatures", ""), ("Default apps", "ms-settings:defaultapps", ""),
        ("Power & battery", "ms-settings:powersleep", ""), ("Storage", "ms-settings:storagesense", ""),
        ("Background / wallpaper", "ms-settings:personalization-background", ""), ("Colours", "ms-settings:colors", ""),
        ("Themes", "ms-settings:themes", ""), ("Taskbar settings", "ms-settings:taskbar", ""),
        ("Windows Update", "ms-settings:windowsupdate", ""), ("Privacy & security", "ms-settings:privacy", ""),
        ("Mouse settings", "ms-settings:mousetouchpad", ""), ("Touchpad settings", "ms-settings:devices-touchpad", ""),
        ("Keyboard settings", "ms-settings:keyboard", ""), ("Date & time", "ms-settings:dateandtime", ""),
        ("Notifications", "ms-settings:notifications", ""), ("Night light", "ms-settings:nightlight", ""),
        ("About this PC", "ms-settings:about", ""), ("Mobile hotspot", "ms-settings:network-mobilehotspot", ""),
        ("VPN", "ms-settings:network-vpn", ""), ("Gaming / Game Mode", "ms-settings:gaming-gamemode", ""),
    };

    List<(string name, string kind, string? glyph, Func<ImageSource?>? icon, Action run, string? detail)> BrimActions()
    {
        var app = (App)Application.Current;
        return new()
        {
            ("Brim settings", "Brim", "", null, () => app.OpenSettings(), null),
            ("Wallpapers", "Brim", "", null, () => app.OpenWallpapers(), null),
            ("Show desktop", "Brim", "", null, () => Native.TapKeys(Native.VK_LWIN, (byte)'D'), null),
            (S.HideDesktopIcons ? "Show desktop icons" : "Hide desktop icons", "Brim", "", null, () => { S.HideDesktopIcons = !S.HideDesktopIcons; SettingsStore.Save(); app.ApplyAll(); }, null),
            ("Focus timer · 25 minutes", "Brim", "", null, () => { FocusTimer.Set(TimeSpan.FromMinutes(25)); FocusTimer.StartPause(); }, null),
            ("Take a screenshot", "Brim", "", null, () => app.TakeScreenshot(), null),
            ("Lock this PC", "Windows", "", null, () => Launcher.Open("rundll32.exe", "user32.dll,LockWorkStation"), null),
        };
    }

    /// <summary>Every app in the Start menu, including Store apps, through the same list Start uses.</summary>
    static List<(string name, string target)> Apps()
    {
        // Shared with the app picker, so the list is only read once
        return AppCatalog.All().Select(a => (a.Name, a.Id)).ToList();
    }

    static readonly Dictionary<string, ImageSource?> appIcons = new();

    static ImageSource? AppIcon(string target)
    {
        if (appIcons.TryGetValue(target, out var hit)) return hit;
        // Every Start menu app has an icon in the shell's app list, Store apps included
        var icon = IconHelper.ForShellItem("shell:AppsFolder\\" + target, 32);
        if (icon == null && (target.Contains(':') || target.StartsWith("\\"))) icon = IconHelper.ForPath(target);
        appIcons[target] = icon;
        return icon;
    }

    static void OpenApp(string target)
    {
        if (File.Exists(target)) Launcher.Open(target);
        else Launcher.Open("explorer.exe", "shell:AppsFolder\\" + target);
    }

    static IEnumerable<string> RecentFiles()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        try
        {
            return new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(150).Select(f => f.FullName).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    static int Score(string name, string q)
    {
        var n = name.ToLowerInvariant();
        q = q.ToLowerInvariant();
        if (n == q) return 100;
        if (n.StartsWith(q)) return 80;
        if (Regex.IsMatch(n, @"\b" + Regex.Escape(q))) return 60;
        var initials = string.Concat(Regex.Matches(n, @"\b\w").Select(m => m.Value));
        if (q.Length >= 2 && initials.StartsWith(q)) return 50;
        if (n.Contains(q)) return 40;
        return 0;
    }

    List<SearchResult> Suggestions()
    {
        var games = GameLibrary.Current.Where(g => !g.Hidden).OrderByDescending(g => g.LastPlayed).Take(3)
            .Select(g => new SearchResult(g.Name, "Game", "", () => GameIcon(g), () => GameLibrary.Launch(g)));
        var actions = BrimActions().Take(3).Select(a => new SearchResult(a.name, a.kind, a.glyph, a.icon, a.run, a.detail));
        return games.Concat(actions).ToList();
    }

    static ImageSource? GameIcon(GameInfo g)
    {
        var p = g.IconExe ?? g.Exe;
        if (p != null && File.Exists(p) && IconHelper.ForPath(p) is { } icon) return icon;
        // Games started through Steam/Epic usually have a Start menu entry with the game's icon
        var app = Apps().FirstOrDefault(a => string.Equals(a.name, g.Name, StringComparison.OrdinalIgnoreCase));
        return app.target != null ? AppIcon(app.target) : null;
    }

    List<SearchResult> Find(string q)
    {
        var found = new List<(int score, SearchResult r)>();

        // Quick sums: 12*7, (3+4)/2, 15% of 80 style input
        if (Regex.IsMatch(q, @"^[\d\s\.\,\+\-\*\/\(\)%x×÷\^]+$") && Regex.IsMatch(q, @"\d") && Regex.IsMatch(q, @"[\+\-\*\/x×÷%]"))
        {
            try
            {
                var expr = q.Replace("×", "*").Replace("x", "*").Replace("÷", "/").Replace(",", "");
                expr = Regex.Replace(expr, @"(\d+(?:\.\d+)?)\s*%", "($1/100)");
                var value = Convert.ToDouble(new DataTable().Compute(expr, null), CultureInfo.InvariantCulture);
                var text = value.ToString("0.########", CultureInfo.CurrentCulture);
                found.Add((1000, new SearchResult($"= {text}", "Copy", "", null, () => Clipboard.SetText(text), "Press Enter to copy the answer")));
            }
            catch { }
        }

        foreach (var g in GameLibrary.Current.Where(g => !g.Hidden))
        {
            int s = Score(g.Name, q);
            if (s > 0) found.Add((s + 6, new SearchResult(g.Name, "Game", "", () => GameIcon(g), () => GameLibrary.Launch(g), g.Source)));
        }
        var gameNames = new HashSet<string>(GameLibrary.Current.Where(g => !g.Hidden).Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, target) in Apps())
        {
            if (gameNames.Contains(name)) continue; // already listed as a game
            int s = Score(name, q);
            if (s > 0) found.Add((s + 5, new SearchResult(name, "App", "", () => AppIcon(target), () => OpenApp(target))));
        }
        foreach (var (name, uri, glyph) in SettingsPages)
        {
            int s = Score(name, q);
            if (s > 0) found.Add((s, new SearchResult(name, "Settings", glyph, null, () => Launcher.Open(uri))));
        }
        foreach (var a in BrimActions())
        {
            int s = Score(a.name, q);
            if (s > 0) found.Add((s + 2, new SearchResult(a.name, a.kind, a.glyph, a.icon, a.run, a.detail)));
        }
        if (q.Length >= 2)
            foreach (var lnk in RecentFiles())
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                int s = Score(name, q);
                if (s > 0) found.Add((s - 10, new SearchResult(name, "Recent file", "", () => IconHelper.ForPath(lnk), () => Launcher.Open(lnk))));
            }

        var top = found.OrderByDescending(f => f.score).Select(f => f.r)
            .GroupBy(r => r.Kind + "|" + r.Name).Select(g => g.First()).Take(8).ToList();
        top.Add(new SearchResult($"Search the web for “{q}”", "Web", "", null,
            () => Launcher.Open("https://www.google.com/search?q=" + Uri.EscapeDataString(q))));
        return top;
    }
}
