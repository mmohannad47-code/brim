using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brim.Core;

namespace Brim.Views;

/// <summary>The notch's focus timer tab: pick a length, start, and the closed notch counts down.</summary>
public sealed class FocusTimerView : Grid
{
    readonly TextBlock time = new() { FontSize = 52, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Button start;

    public FocusTimerView()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        time.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Text");
        left.Children.Add(time);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        controls.Children.Add(Icon("", "5 minutes less", () => Nudge(-5)));
        start = new Button { Content = "", Margin = new Thickness(10, 0, 10, 0) };
        start.SetResourceReference(StyleProperty, "RoundButton");
        start.Click += (_, _) => FocusTimer.StartPause();
        controls.Children.Add(start);
        controls.Children.Add(Icon("", "5 minutes more", () => Nudge(5)));
        controls.Children.Add(Icon("", "Reset", FocusTimer.Reset));
        left.Children.Add(controls);
        Children.Add(left);

        var presets = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (int m in new[] { 5, 15, 25, 45, 60, 90 })
        {
            var b = new Button { Content = $"{m} min", Margin = new Thickness(0, 0, 6, 6) };
            b.SetResourceReference(StyleProperty, "Pill");
            int minutes = m;
            b.Click += (_, _) => FocusTimer.Set(TimeSpan.FromMinutes(minutes));
            presets.Children.Add(b);
        }
        var hint = new TextBlock { Text = "The closed notch counts down, and pops out when time's up.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { presets, hint } };
        Grid.SetColumn(right, 1);
        Children.Add(right);

        FocusTimer.Changed += Update;
        Unloaded += (_, _) => FocusTimer.Changed -= Update;
        Update();
    }

    static void Nudge(int minutes)
    {
        var d = FocusTimer.Duration + TimeSpan.FromMinutes(minutes);
        if (d < TimeSpan.FromMinutes(1)) d = TimeSpan.FromMinutes(1);
        FocusTimer.Set(d);
    }

    Button Icon(string glyph, string tip, Action a)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Width = 36, Height = 36 };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.Click += (_, _) => a();
        return b;
    }

    void Update()
    {
        time.Text = FocusTimer.Format(FocusTimer.Left);
        start.Content = FocusTimer.Running ? "" : "";
    }
}

/// <summary>
/// The notch's Games tab: a scrolling row of cover art. Click to play, drag a cover left or right to put
/// your games in the order you like, right-click for more.
/// </summary>
public sealed class GamesStrip : Grid
{
    readonly StackPanel row = new() { Orientation = Orientation.Horizontal, AllowDrop = true, Background = Brushes.Transparent };
    readonly ScrollViewer scroller = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    readonly Canvas overlay = new() { IsHitTestVisible = false };
    readonly Border marker = new() { Width = 3, Height = 150, CornerRadius = new CornerRadius(1.5), Visibility = Visibility.Collapsed };
    readonly TextBlock note = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Action launched;
    readonly Action<ShortcutItem> pin;
    readonly Action menuOpened, menuClosed;
    const string DragFormat = "brim/game";

    Point downAt;
    FrameworkElement? downTile;
    bool dragging, justDragged;

    public GamesStrip(Action launched, Action<ShortcutItem> pin, Action menuOpened, Action menuClosed)
    {
        this.launched = launched;
        this.pin = pin;
        this.menuOpened = menuOpened;
        GameRenderer.Warm(GameLibrary.Current); // so "Launch with" can say Vulkan / DirectX by the time you right-click
        this.menuClosed = menuClosed;
        note.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        marker.SetResourceReference(Border.BackgroundProperty, "Brim.Accent");
        overlay.Children.Add(marker);
        scroller.Content = row;
        scroller.PreviewMouseWheel += (_, e) =>
        {
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta * 0.35);
            e.Handled = true;
        };
        row.DragOver += Row_DragOver;
        row.DragLeave += (_, _) => marker.Visibility = Visibility.Collapsed;
        row.Drop += Row_Drop;
        Children.Add(scroller);
        Children.Add(overlay);
        Children.Add(note);
        GameLibrary.Changed += Build;
        Unloaded += (_, _) => GameLibrary.Changed -= Build;
        Loaded += async (_, _) =>
        {
            if (!GameLibrary.Scanned) { note.Text = "Looking for your games…"; await GameLibrary.Refresh(); }
            else Build();
        };
    }

    List<GameInfo> Visible => GameLibrary.Current.Where(g => !g.Hidden).ToList();

    void Build()
    {
        if (dragging) return;
        double offset = scroller.HorizontalOffset;
        row.Children.Clear();
        var games = Visible;
        note.Text = games.Count == 0 ? "No games found yet. Add folders in Settings › Games." : "";
        note.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in games) row.Children.Add(Tile(g));
        var more = new Button { Width = 70, Height = 156, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Manage games" };
        more.SetResourceReference(StyleProperty, "Tile");
        more.Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        more.Click += (_, _) => { launched(); ((App)Application.Current).OpenSettings("Games"); };
        row.Children.Add(more);
        scroller.ScrollToHorizontalOffset(offset);
    }

    static string GameTip(GameInfo g)
    {
        var parts = new System.Collections.Generic.List<string> { g.Name, g.Source };
        var played = PlayTime.Format(g);
        if (played.Length > 0) parts.Add(played + " played");
        var ago = PlayTime.Ago(g.LastPlayed);
        if (ago.Length > 0) parts.Add(ago);
        return string.Join("  ·  ", parts);
    }

    FrameworkElement Tile(GameInfo g)
    {
        const double w = 104, h = 156;
        var art = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(10) };
        art.SetResourceReference(Border.BackgroundProperty, "Brim.Raised");

        // Until (or instead of) cover art: the game's icon, big, on a backdrop in the icon's own colour
        var fallback = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
        var iconPath = g.IconExe ?? g.Exe;
        var iconSource = iconPath != null && File.Exists(iconPath) ? IconHelper.ForPath(iconPath) : null;
        fallback.Children.Add(new Image { Width = 56, Height = 56, Source = iconSource });
        var title = new TextBlock { Text = g.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), MaxHeight = 64 };
        fallback.Children.Add(title);
        if (iconSource is System.Windows.Media.Imaging.BitmapSource bs && IconHelper.Tint(bs) is Color tint)
        {
            var bg = new LinearGradientBrush(
                Color.FromRgb((byte)(tint.R * 0.75), (byte)(tint.G * 0.75), (byte)(tint.B * 0.75)),
                Color.FromRgb((byte)(tint.R * 0.22), (byte)(tint.G * 0.22), (byte)(tint.B * 0.22)), 90);
            bg.Freeze();
            art.Background = bg;
        }

        var label = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            Padding = new Thickness(6, 14, 6, 6),
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(220, 0, 0, 0), 90),
            Opacity = 0,
            Child = new TextBlock { Text = g.Name, FontSize = 11, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
        };

        var cell = new Grid { Width = w, Height = h, Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand, ToolTip = GameTip(g), Tag = g };
        cell.Children.Add(art);
        cell.Children.Add(fallback);
        cell.Children.Add(label);
        cell.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        cell.RenderTransform = scale;
        cell.MouseEnter += (_, _) => { label.Opacity = fallback.Visibility == Visibility.Visible ? 0 : 1; scale.ScaleX = scale.ScaleY = 1.03; };
        cell.MouseLeave += (_, _) => { label.Opacity = 0; scale.ScaleX = scale.ScaleY = 1; };
        cell.PreviewMouseLeftButtonDown += (_, e) => { downAt = e.GetPosition(this); downTile = cell; justDragged = false; };
        cell.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || downTile != cell || dragging) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - downAt.X) > 8 || Math.Abs(p.Y - downAt.Y) > 8) StartDrag(g, cell);
        };
        cell.MouseLeftButtonUp += (_, _) =>
        {
            if (justDragged) { justDragged = false; return; }
            if (GameLibrary.Override(g.Key).AskEachTime && GameRenderer.Options(g).Count > 1) { LaunchMenu(g, cell); return; }
            GameLibrary.Launch(g);
            launched();
        };
        cell.MouseRightButtonUp += (_, e) => { e.Handled = true; Menu(g, cell); };
        LoadCover(g, art, fallback);
        return cell;
    }

    static async void LoadCover(GameInfo g, Border art, FrameworkElement fallback)
    {
        var img = await GameLibrary.Cover(g);
        if (img == null) return;
        art.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        fallback.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ drag to reorder

    void StartDrag(GameInfo g, FrameworkElement cell)
    {
        dragging = true;
        menuOpened(); // keep the notch open while dragging
        cell.Opacity = 0.35;
        try { DragDrop.DoDragDrop(cell, new DataObject(DragFormat, g.Key), DragDropEffects.Move); }
        catch (Exception ex) { Log.Error("game drag", ex); }
        finally
        {
            cell.Opacity = 1;
            dragging = false;
            justDragged = true;
            marker.Visibility = Visibility.Collapsed;
            menuClosed();
        }
    }

    /// <summary>Where a drop at this point would go: the index among the game tiles, and the x for the marker.</summary>
    (int index, double x) DropSpot(DragEventArgs e)
    {
        var tiles = row.Children.OfType<Grid>().ToList();
        double px = e.GetPosition(this).X;
        for (int i = 0; i < tiles.Count; i++)
        {
            var left = tiles[i].TranslatePoint(new Point(0, 0), this).X;
            if (px < left + tiles[i].ActualWidth / 2) return (i, left - 5);
        }
        var last = tiles.LastOrDefault();
        return (tiles.Count, last == null ? 0 : last.TranslatePoint(new Point(last.ActualWidth, 0), this).X + 2);
    }

    void Row_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        var (_, x) = DropSpot(e);
        Canvas.SetLeft(marker, x);
        Canvas.SetTop(marker, 3);
        marker.Visibility = Visibility.Visible;
        // Scroll when dragging towards either edge
        double px = e.GetPosition(this).X;
        if (px < 40) scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - 14);
        else if (px > ActualWidth - 40) scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + 14);
    }

    void Row_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        marker.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DragFormat) is not string key) return;
        var (index, _) = DropSpot(e);
        var order = Visible.Select(g => g.Key).ToList();
        int from = order.IndexOf(key);
        if (from < 0) return;
        order.RemoveAt(from);
        if (index > from) index--;
        order.Insert(Math.Clamp(index, 0, order.Count), key);
        // Hidden games keep their place at the end
        order.AddRange(GameLibrary.Current.Where(g => g.Hidden).Select(g => g.Key));
        dragging = false;
        GameLibrary.SetOrder(order);
    }

    // ------------------------------------------------------------------ right-click

    void Menu(GameInfo g, UIElement target)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Play", () => { GameLibrary.Launch(g); launched(); }));
        menu.Items.Add(Item("Move to the front", () =>
        {
            var order = GameLibrary.Current.Select(x => x.Key).Where(k => k != g.Key).Prepend(g.Key).ToList();
            GameLibrary.SetOrder(order);
        }));
        menu.Items.Add(Item("Add to Shortcuts", () => pin(new ShortcutItem
        {
            Name = g.Name,
            Path = g.LaunchUri ?? g.Exe ?? "",
            Args = g.Args,
        })));
        menu.Items.Add(Item("Choose cover image…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Cover for {g.Name} (portrait pictures look best)", Filter = "Pictures|*.jpg;*.jpeg;*.png;*.webp;*.bmp" };
            if (dlg.ShowDialog() == true) GameLibrary.SetCustomCover(g, dlg.FileName);
        }));
        if (GameLibrary.Override(g.Key).Cover != null)
            menu.Items.Add(Item("Use the automatic cover", () => GameLibrary.SetCustomCover(g, null)));
        var options = GameRenderer.Options(g);
        if (options.Count > 1 || g.SteamAppId != null)
        {
            var pick = new MenuItem { Header = "Launch with" };
            var ov = GameLibrary.Override(g.Key);
            var current = GameRenderer.CurrentId(g, ov);
            string? lastKind = null;
            foreach (var o in options)
            {
                var opt = o;
                var kind = o.Id.Split(':')[0];
                if (lastKind != null && kind != lastKind) pick.Items.Add(new Separator());
                lastKind = kind;
                pick.Items.Add(Item((o.Id == current ? "✓ " : "") + o.Label, () =>
                {
                    GameRenderer.Choose(g, ov, opt);
                    SettingsStore.Save();
                    _ = GameLibrary.Refresh();
                }));
            }
            pick.Items.Add(new Separator());
            pick.Items.Add(Item("Choose another program to start it…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"How should {g.Name} start?", Filter = "Programs|*.exe", InitialDirectory = g.Folder ?? "" };
                if (dlg.ShowDialog() != true) return;
                ov.Exe = dlg.FileName;
                ov.RendererArgs = null;
                SettingsStore.Save();
                _ = GameLibrary.Refresh();
            }));
            if (g.SteamAppId != null)
                pick.Items.Add(Item("Steam's launch options…", () => { Launcher.Open($"steam://launch/{g.SteamAppId}/dialog"); launched(); }));
            var ask = new MenuItem { Header = "Ask every time I play", IsCheckable = true, IsChecked = ov.AskEachTime };
            ask.Click += (_, _) => { ov.AskEachTime = !ov.AskEachTime; SettingsStore.Save(); };
            pick.Items.Add(ask);
            menu.Items.Add(pick);
        }
        menu.Items.Add(Item("Uninstall…", () =>
        {
            var match = Uninstaller.ForGameAny(g, Uninstaller.Everything());
            launched();
            if (match != null) ((App)Application.Current).OpenUninstall(match);
            else ((App)Application.Current).OpenUninstallSearch(g.Name);
        }));
        if (g.Folder != null && Directory.Exists(g.Folder)) menu.Items.Add(Item("Open folder", () => Launcher.Open(g.Folder)));
        menu.Items.Add(Item("Hide from this list", () =>
        {
            GameLibrary.Override(g.Key).Hidden = true;
            SettingsStore.Save();
            _ = GameLibrary.Refresh();
        }));
        menuOpened();
        menu.Closed += (_, _) => menuClosed();
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>"Ask every time": pick how to start it, just this once.</summary>
    void LaunchMenu(GameInfo g, UIElement target)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Play " + g.Name + " with…", IsEnabled = false });
        foreach (var o in GameRenderer.Options(g))
        {
            var opt = o;
            menu.Items.Add(Item(o.Label, () => { GameLibrary.LaunchWith(g, opt); launched(); }));
        }
        if (g.SteamAppId != null)
            menu.Items.Add(Item("Steam's launch options…", () => { Launcher.Open($"steam://launch/{g.SteamAppId}/dialog"); GameLibrary.MarkPlayed(g); launched(); }));
        menuOpened();
        menu.Closed += (_, _) => menuClosed();
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    static string RelativeTo(string? folder, string path)
    {
        if (folder != null && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return path[folder.Length..].TrimStart('\\');
        return Path.GetFileName(path);
    }

    static MenuItem Item(string header, Action a)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => a();
        return mi;
    }
}
