using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Brim.Core;

namespace Brim.Views;

/// <summary>Browse wallpapers online; only the one you pick is downloaded.</summary>
public sealed class GalleryWindow : Window
{
    static AppSettings S => SettingsStore.Current;
    static App AppRef => (App)Application.Current;

    static readonly string[] PictureTopics = { "Popular", "Nature", "Space", "Minimal", "City", "Abstract", "Mountains", "Dark", "Cars", "Anime" };
    static readonly string[] OledTopics = { "Popular", "Amoled", "Space", "Minimal", "Neon", "Nature", "Abstract", "Anime" };
    static readonly string[] VideoTopics = { "Popular", "Nature", "Ocean", "Space", "Abstract", "Rain", "City", "Clouds", "Neon", "Fire" };
    static readonly string[] SceneTopics = { "All", "OLED" };

    readonly WrapPanel grid = new() { Margin = new Thickness(26, 4, 18, 24) };
    readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBox search = new() { Width = 260 };
    readonly WrapPanel topics = new() { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock status = new() { FontSize = 12, Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal };
    readonly Button stopLive;
    readonly Grid toolbar = new() { Margin = new Thickness(28, 0, 28, 14) };

    string tab = "Pictures";
    string? query;
    bool anime;
    int page;
    bool loading, more = true, busy;
    int generation;
    static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3));

    public GalleryWindow()
    {
        Title = "Brim wallpapers";
        Width = 1180;
        Height = 780;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("Brim.Font");
        SetResourceReference(BackgroundProperty, "Set.Bg");
        Foreground = Brushes.White;
        UseLayoutRounding = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });

        // Header: title, tabs, stop button
        var header = new Grid { Margin = new Thickness(28, 22, 28, 16) };
        var title = new TextBlock { Text = "Wallpapers", FontSize = 26, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title);
        var tabNames = new List<string> { "Pictures", "OLED", "Animated", "Videos" };
        if (WallpaperEngine.Installed) tabNames.Add("Wallpaper Engine");
        tabNames.Add("Downloaded");
        foreach (var t in tabNames)
        {
            var rb = new RadioButton { Content = t, GroupName = "gtab", IsChecked = t == tab };
            rb.SetResourceReference(StyleProperty, "Chip");
            var name = t;
            rb.Checked += (_, _) => { tab = name; query = null; anime = false; search.Text = ""; BuildTopics(); Reload(); };
            tabs.Children.Add(rb);
        }
        tabs.Margin = new Thickness(190, 4, 0, 0);
        tabs.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(tabs);
        stopLive = Pill("Stop live wallpaper", () => { AppRef.SetLiveWallpaper(null); UpdateStop(); Status("Live wallpaper stopped."); });
        stopLive.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(stopLive);
        root.Children.Add(header);

        // Toolbar: search + topics
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        search.SetResourceReference(StyleProperty, "Field");
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            query = search.Text.Trim();
            anime = false;
            foreach (var rb in topics.Children.OfType<RadioButton>()) rb.IsChecked = false;
            Reload();
        };
        toolbar.Children.Add(search);
        Grid.SetColumn(topics, 1);
        toolbar.Children.Add(topics);
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        scroller.Content = grid;
        scroller.ScrollChanged += (_, e) =>
        {
            if (e.VerticalOffset + e.ViewportHeight > e.ExtentHeight - 500) LoadMore();
        };
        Grid.SetRow(scroller, 2);
        root.Children.Add(scroller);

        var foot = new Grid();
        foot.SetResourceReference(Panel.BackgroundProperty, "Set.Side");
        status.Foreground = Gray;
        foot.Children.Add(status);
        Grid.SetRow(foot, 3);
        root.Children.Add(foot);

        Content = root;
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4);
        };
        Closed += (_, _) => { grid.Children.Clear(); AppRef.ScheduleTrim(); };

        BuildTopics();
        UpdateStop();
        Reload();
    }

    void UpdateStop() => stopLive.Visibility = S.LiveWallpaper != null ? Visibility.Visible : Visibility.Collapsed;

    void Status(string text) => status.Text = text;

    void BuildTopics()
    {
        topics.Children.Clear();
        toolbar.Visibility = tab == "Downloaded" ? Visibility.Collapsed : Visibility.Visible;
        search.Visibility = tab is "Animated" or "Wallpaper Engine" ? Visibility.Collapsed : Visibility.Visible;
        var list = tab switch
        {
            "Videos" => VideoTopics,
            "OLED" => OledTopics,
            "Animated" => SceneTopics,
            "Wallpaper Engine" => new[] { "All", "Plays in Brim" },
            _ => PictureTopics,
        };
        foreach (var t in list)
        {
            var rb = new RadioButton { Content = t, GroupName = "topic", IsChecked = t is "Popular" or "All" };
            rb.SetResourceReference(StyleProperty, "Chip");
            var name = t;
            rb.Checked += (_, _) =>
            {
                search.Text = "";
                anime = name == "Anime";
                query = name is "Popular" or "Anime" or "All" ? null : name.ToLowerInvariant();
                Reload();
            };
            topics.Children.Add(rb);
        }
    }

    void Reload()
    {
        generation++;
        grid.Children.Clear();
        page = 0;
        more = true;
        loading = false;
        scroller.ScrollToTop();
        if (tab == "Videos" && string.IsNullOrWhiteSpace(ApiKeys.Pixabay)) { ShowKeyPanel(null); return; }
        if (tab == "Downloaded") { ShowLocal(); return; }
        if (tab == "Animated") { ShowScenes(); return; }
        if (tab == "Wallpaper Engine") { ShowWallpaperEngine(); return; }
        LoadMore();
    }

    readonly List<FileSystemWatcher> weWatchers = new();
    System.Windows.Threading.DispatcherTimer? weRefresh;

    /// <summary>Re-read the list when Steam adds or removes a wallpaper (downloads write many files, so wait for quiet).</summary>
    void WatchWallpaperEngine()
    {
        if (weWatchers.Count > 0) return;
        weRefresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        weRefresh.Tick += (_, _) => { weRefresh.Stop(); if (tab == "Wallpaper Engine") Reload(); };
        foreach (var folder in WallpaperEngine.WatchFolders())
        {
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                };
                FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(new Action(() => { weRefresh.Stop(); weRefresh.Start(); }));
                w.Created += changed;
                w.Deleted += changed;
                w.Renamed += (s, e) => changed(s, e);
                w.EnableRaisingEvents = true;
                weWatchers.Add(w);
            }
            catch (Exception ex) { Log.Error("we watch", ex); }
        }
        Closed += (_, _) => { foreach (var w in weWatchers) w.Dispose(); weWatchers.Clear(); };
    }

    void ShowWallpaperEngine()
    {
        WatchWallpaperEngine();
        var items = WallpaperEngine.Items();
        bool videosOnly = query == "plays in brim" || query == "videos";
        foreach (var it in items.Where(i => !videosOnly || i.Type == "video")) grid.Children.Add(WeCard(it));
        var more = Pill("Find more video wallpapers on the Workshop", () => Launcher.Open(WallpaperEngine.WorkshopVideos));
        more.Margin = new Thickness(8);
        more.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(more);
        Status($"{items.Count} from your Wallpaper Engine library. Pick one and Wallpaper Engine shows it (videos, scenes and web wallpapers alike), with its own playback and settings.");
    }

    FrameworkElement WeCard(WeItem it)
    {
        const double w = 262, h = 147;
        var image = new Border { CornerRadius = new CornerRadius(12) };
        image.SetResourceReference(Border.BackgroundProperty, "Set.Card");
        var name = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(8),
            MaxWidth = w - 16,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = it.Title, FontSize = 11.5, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis },
        };
        var badges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) };
        badges.Children.Add(Badge(it.Type switch { "video" => "Video · Wallpaper Engine", "web" => "Web · Wallpaper Engine", _ => "Scene · Wallpaper Engine" }));

        var hover = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            Opacity = 0,
            Child = new TextBlock
            {
                Text = "Show with Wallpaper Engine",
                FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var cell = new Grid { Width = w, Height = h, Margin = new Thickness(6), Cursor = Cursors.Hand, ToolTip = it.Folder };
        cell.Children.Add(image);
        cell.Children.Add(name);
        cell.Children.Add(badges);
        cell.Children.Add(hover);
        cell.MouseEnter += (_, _) => hover.Opacity = 1;
        cell.MouseLeave += (_, _) => hover.Opacity = 0;
        cell.MouseLeftButtonUp += (_, _) =>
        {
            // Wallpaper Engine plays its own wallpapers (videos too): it does it better than Brim's player
            if (S.LiveWallpaper != null) AppRef.SetLiveWallpaper(null, refresh: false);
            WallpaperEngine.HandBack();
            Status(WallpaperEngine.Open(it)
                ? $"Wallpaper Engine is showing \"{it.Title}\". Pick any Brim wallpaper to switch back."
                : "Couldn't start Wallpaper Engine.");
            UpdateStop();
        };
        if (it.Preview != null) LoadThumb(it.Preview, image);
        return cell;
    }

    void ShowScenes()
    {
        bool oledOnly = query == "oled";
        foreach (var s in Scenes.All.Where(s => !oledOnly || s.Oled)) grid.Children.Add(SceneCard(s));
        Status("Animated wallpapers drawn live by Brim: nothing to download. Point at one to preview it moving, click to use it.");
    }

    FrameworkElement SceneCard(SceneInfo info)
    {
        const double w = 262, h = 147;
        var view = new SceneView(info.Make(), startTime: 40, animate: false) { Width = w, Height = h };
        var clip = new Border { CornerRadius = new CornerRadius(12), Child = view, ClipToBounds = true };
        clip.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 12, 12);
        var name = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = info.Name, FontSize = 11.5, Foreground = Brushes.White },
        };
        var badges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) };
        badges.Children.Add(Badge("LIVE"));
        if (info.Oled) badges.Children.Add(Badge("OLED"));
        if (S.LiveWallpaper == Scenes.Prefix + info.Id) badges.Children.Add(Badge("✓ On"));

        var cell = new Grid { Width = w, Height = h, Margin = new Thickness(6), Cursor = Cursors.Hand, ToolTip = "Click to use as your wallpaper" };
        cell.Children.Add(clip);
        cell.Children.Add(name);
        cell.Children.Add(badges);
        cell.MouseEnter += (_, _) => view.Paused = false;
        cell.MouseLeave += (_, _) => view.Paused = true;
        cell.MouseLeftButtonUp += (_, _) =>
        {
            AppRef.SetLiveWallpaper(Scenes.Prefix + info.Id);
            UpdateStop();
            Status($"{info.Name} is on. It pauses by itself while a game or maximised window covers the screen.");
            Reload();
        };
        return cell;
    }

    async void LoadMore()
    {
        if (loading || !more || tab is "Downloaded" or "Animated") return;
        if (tab == "Videos" && string.IsNullOrWhiteSpace(ApiKeys.Pixabay)) return;
        loading = true;
        int gen = generation;
        Status("Loading…");
        try
        {
            page++;
            var items = tab switch
            {
                "Videos" => await Wallpapers.Videos(query, page),
                "OLED" => await Wallpapers.Pictures(query == "amoled" ? "amoled" : query, anime, page, oled: true),
                _ => await Wallpapers.Pictures(query, anime, page),
            };
            if (gen != generation) return;
            if (items.Count == 0) more = false;
            foreach (var it in items) grid.Children.Add(Card(it));
            Status(tab switch
            {
                "Videos" => "Videos from Pixabay. Click one to download it and play it behind your icons.",
                "OLED" => "Mostly true-black pictures from wallhaven.cc: black pixels on OLED are switched off, so they look deep and save power.",
                _ => "Pictures from wallhaven.cc. Click one to download it and set it as your wallpaper.",
            });
            if (grid.Children.Count == 0) Status("Nothing found. Try another word.");
        }
        catch (UnauthorizedAccessException)
        {
            if (gen == generation) ShowKeyPanel("That Pexels key didn't work. Check it and paste it again.");
        }
        catch (Exception ex)
        {
            Log.Error("gallery", ex);
            if (gen == generation) Status("Couldn't load wallpapers. Check your internet connection.");
            page--;
        }
        finally { if (gen == generation) loading = false; }
    }

    void ShowLocal()
    {
        var items = Wallpapers.Local();
        foreach (var it in items) grid.Children.Add(Card(it, local: true));
        var add = Pill("Add a video or picture from this PC…", AddFromPc);
        add.Margin = new Thickness(8, 8, 8, 8);
        add.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(add);
        Status(items.Count == 0 ? "Nothing downloaded yet." : $"{items.Count} saved in {Wallpapers.Folder}. Right-click one to delete it.");
    }

    void AddFromPc()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a wallpaper",
            Filter = "Videos and pictures|*.mp4;*.webm;*.mkv;*.mov;*.wmv;*.jpg;*.jpeg;*.png;*.bmp|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Directory.CreateDirectory(Wallpapers.Folder);
            var dest = Path.Combine(Wallpapers.Folder, "my-" + Path.GetFileName(dlg.FileName));
            if (!File.Exists(dest)) File.Copy(dlg.FileName, dest);
            Apply(new WallItem { Id = Path.GetFileNameWithoutExtension(dest), Ext = Path.GetExtension(dest), LocalPath = dest, IsVideo = IsVideoFile(dest) }, null);
            if (tab == "Downloaded") Reload();
        }
        catch (Exception ex) { Log.Error("add wallpaper", ex); Status("Couldn't copy that file."); }
    }

    static bool IsVideoFile(string p) => Path.GetExtension(p).ToLowerInvariant() is ".mp4" or ".webm" or ".mkv" or ".mov" or ".wmv" or ".avi";

    void ShowKeyPanel(string? error)
    {
        grid.Children.Clear();
        var box = new TextBox { Width = 380, Text = S.PixabayKey };
        box.SetResourceReference(StyleProperty, "Field");
        var panel = new StackPanel { Width = 640, Margin = new Thickness(0, 30, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Video wallpapers come from Pixabay", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Pixabay has thousands of free video clips. To browse them, Brim needs a free Pixabay key: make a free account, open pixabay.com/api/docs, and your key is shown under \"Parameters\" (key). Paste it below; it stays on this PC.\n\nDon't want an account? The Animated tab has live wallpapers that need nothing at all.",
            FontSize = 13, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        var save = Pill("Save key", () =>
        {
            S.PixabayKey = box.Text.Trim();
            SettingsStore.Save();
            Reload();
        });
        save.Margin = new Thickness(8, 0, 0, 0);
        save.SetResourceReference(BackgroundProperty, "Brim.Accent");
        save.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
        row.Children.Add(save);
        panel.Children.Add(row);
        if (error != null) panel.Children.Add(new TextBlock { Text = error, FontSize = 12, Foreground = Brushes.IndianRed, Margin = new Thickness(0, 8, 0, 0) });
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        links.Children.Add(Pill("Get a free key (pixabay.com/api/docs)", () => Launcher.Open("https://pixabay.com/api/docs/")));
        links.Children.Add(Pill("Use a video from this PC instead…", AddFromPc));
        panel.Children.Add(links);
        grid.Children.Add(panel);
        Status("");
    }

    FrameworkElement Card(WallItem item, bool local = false)
    {
        const double w = 262, h = 147;
        var image = new Border { CornerRadius = new CornerRadius(12) };
        image.SetResourceReference(Border.BackgroundProperty, "Set.Card");

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = item.Label, FontSize = 11, Foreground = Brushes.White },
        };
        var badges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) };
        if (item.IsVideo) badges.Children.Add(Badge("LIVE"));
        var saved = Badge("✓ Saved");
        saved.Visibility = item.LocalPath != null && !local ? Visibility.Visible : Visibility.Collapsed;
        item.PropertyChanged += (_, _) => saved.Visibility = item.LocalPath != null && !local ? Visibility.Visible : Visibility.Collapsed;
        badges.Children.Add(saved);

        var hover = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            Opacity = 0,
            Child = new TextBlock
            {
                Text = item.IsVideo ? "Play as wallpaper" : "Set as wallpaper",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var progressText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var progress = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), Visibility = Visibility.Collapsed, Child = progressText };

        var cell = new Grid { Width = w, Height = h, Margin = new Thickness(6), Cursor = Cursors.Hand, ToolTip = string.IsNullOrEmpty(item.Credit) ? null : item.Credit };
        cell.Children.Add(image);
        if (item.ThumbUrl == null)
            cell.Children.Add(new TextBlock { Text = item.IsVideo ? "" : "", FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 28, Foreground = Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        cell.Children.Add(label);
        cell.Children.Add(badges);
        cell.Children.Add(hover);
        cell.Children.Add(progress);
        cell.MouseEnter += (_, _) => hover.Opacity = 1;
        cell.MouseLeave += (_, _) => hover.Opacity = 0;
        cell.MouseLeftButtonUp += (_, _) => Apply(item, (progress, progressText));
        if (local)
            cell.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var menu = new ContextMenu();
                var del = new MenuItem { Header = "Delete from this PC" };
                del.Click += (_, _) => Delete(item);
                menu.Items.Add(del);
                menu.IsOpen = true;
            };

        if (item.ThumbUrl != null) LoadThumb(item.ThumbUrl, image);
        return cell;
    }

    static Border Badge(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 2, 6, 2),
        Margin = new Thickness(4, 0, 0, 0),
        Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
    };

    static async void LoadThumb(string url, Border target)
    {
        var img = await Wallpapers.Thumb(url, 280);
        if (img != null) target.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
    }

    async void Apply(WallItem item, (Border box, TextBlock text)? progress)
    {
        if (busy) return;
        busy = true;
        try
        {
            string path = item.LocalPath ?? "";
            if (item.LocalPath == null)
            {
                if (progress is { } p) { p.box.Visibility = Visibility.Visible; p.text.Text = "Downloading…"; }
                Status(item.IsVideo ? "Downloading the video…" : "Downloading the picture…");
                path = await Wallpapers.Download(item, new Progress<double>(v =>
                {
                    if (progress is { } pp) pp.text.Text = $"Downloading {v:P0}";
                }));
            }
            if (item.IsVideo)
            {
                AppRef.SetLiveWallpaper(path);
                Status("Live wallpaper on. It pauses by itself while a game or maximised window covers the screen.");
            }
            else
            {
                // Stop any live wallpaper without repainting the old picture, which would race the new one
                if (S.LiveWallpaper != null) AppRef.SetLiveWallpaper(null, refresh: false);
                Status("Setting your wallpaper…");
                S.BrimPicture = path;
                await WallpaperEngine.TakeOverAsync();
                Wallpapers.ApplyPicture(path);
                Status("Wallpaper set.");
            }
            UpdateStop();
        }
        catch (Exception ex)
        {
            Log.Error("apply wallpaper", ex);
            Status("Download failed. Try again, or pick another one.");
        }
        finally
        {
            if (progress is { } p) p.box.Visibility = Visibility.Collapsed;
            busy = false;
        }
    }

    void Delete(WallItem item)
    {
        try
        {
            if (item.LocalPath == null) return;
            if (string.Equals(S.LiveWallpaper, item.LocalPath, StringComparison.OrdinalIgnoreCase)) AppRef.SetLiveWallpaper(null);
            File.Delete(item.LocalPath);
            var preview = Path.ChangeExtension(item.LocalPath, ".jpg");
            if (item.IsVideo && File.Exists(preview)) File.Delete(preview);
            UpdateStop();
            Reload();
        }
        catch (Exception ex) { Log.Error("delete wallpaper", ex); Status("Couldn't delete it (it may be the current wallpaper)."); }
    }

    static Button Pill(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Foreground = Brushes.White };
        b.SetResourceReference(StyleProperty, "Pill");
        b.SetResourceReference(BackgroundProperty, "Set.Field");
        b.Click += (_, _) => click();
        return b;
    }
}
