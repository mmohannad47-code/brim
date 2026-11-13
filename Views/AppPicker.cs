using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Views;

/// <summary>
/// "Add shortcuts" / "Pin apps": every app you have, with icons and a search box; tick the ones you want.
/// Browse files… and Add folder… cover anything that isn't an app.
/// </summary>
public sealed class AppPicker : Window
{
    sealed class Row
    {
        public string Name = "";
        public string Path = "";
        public string? Detail;
        public AppEntry? App;
        public bool Selected, Added;
        public Border View = null!;
        public Image Icon = null!;
        public Border Check = null!;
        public bool IconLoaded;
    }

    readonly List<Row> rows = new();
    readonly StackPanel list = new();
    readonly TextBox search = new() { FontSize = 14, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock hint = new() { Text = "Search your apps", FontSize = 14, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Button addBtn;
    readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly HashSet<string> existing;
    readonly int maxNew;
    readonly string verb;
    List<ShortcutItem>? result;

    static readonly Brush Gray = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA3));
    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Show the picker; returns what you chose (empty if you cancelled).</summary>
    public static List<ShortcutItem> Pick(Window? owner, string title, string verb, IEnumerable<string> alreadyAdded, int maxNew = int.MaxValue)
    {
        var picker = new AppPicker(title, verb, alreadyAdded, maxNew);
        if (owner != null && owner.IsVisible) { picker.Owner = owner; picker.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else { picker.Topmost = true; picker.WindowStartupLocation = WindowStartupLocation.CenterScreen; }
        picker.ShowDialog();
        return picker.result ?? new List<ShortcutItem>();
    }

    /// <summary>For the snapshot test: an off-screen picker with some text typed.</summary>
    internal static AppPicker Preview(string query)
    {
        var p = new AppPicker("Add shortcuts", "Add", new[] { "C:\\Windows\\explorer.exe" }, 12) { Left = -4000, Top = 0, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        p.Loaded += (_, _) => p.search.Text = query;
        return p;
    }

    AppPicker(string title, string verb, IEnumerable<string> alreadyAdded, int maxNew)
    {
        this.verb = verb;
        this.maxNew = maxNew;
        existing = new HashSet<string>(alreadyAdded, StringComparer.OrdinalIgnoreCase);
        Title = title;
        Width = 520;
        Height = 640;
        MinWidth = 420;
        MinHeight = 400;
        ShowInTaskbar = false;
        FontFamily = (FontFamily)FindResource("Brim.Font");
        SetResourceReference(BackgroundProperty, "Set.Bg");
        Foreground = Brushes.White;
        UseLayoutRounding = true;
        hint.Foreground = Gray;
        status.Foreground = Gray;

        var root = new DockPanel { Margin = new Thickness(18) };

        // Title
        var head = new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 12) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // Search
        var field = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10) };
        field.SetResourceReference(Border.BackgroundProperty, "Set.Field");
        var fieldGrid = new Grid();
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition());
        fieldGrid.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 13, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var stack = new Grid();
        stack.Children.Add(hint);
        stack.Children.Add(search);
        Grid.SetColumn(stack, 1);
        fieldGrid.Children.Add(stack);
        field.Child = fieldGrid;
        DockPanel.SetDock(field, Dock.Top);
        root.Children.Add(field);

        // Bottom bar: other things to add, and the main button
        var bar = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var extra = new StackPanel { Orientation = Orientation.Horizontal };
        extra.Children.Add(Pill("Browse files…", BrowseFiles));
        extra.Children.Add(Pill("Add folder…", BrowseFolder));
        bar.Children.Add(extra);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Pill("Cancel", () => Close()));
        addBtn = Pill(verb, Finish);
        addBtn.SetResourceReference(BackgroundProperty, "Brim.Accent");
        addBtn.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
        addBtn.Margin = new Thickness(0);
        right.Children.Add(addBtn);
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        var statusRow = new Border { Margin = new Thickness(2, 8, 0, 0), Child = status };
        DockPanel.SetDock(statusRow, Dock.Bottom);
        root.Children.Add(statusRow);

        // The list
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(6), Child = scroller };
        card.SetResourceReference(Border.BackgroundProperty, "Set.Card");
        scroller.Content = list;
        root.Children.Add(card);
        Content = root;

        list.Children.Add(new TextBlock { Text = "Finding your apps…", Foreground = Gray, Margin = new Thickness(12) });

        search.TextChanged += (_, _) => { hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Filter(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                // Enter with nothing ticked picks the first match
                if (!rows.Any(r => r.Selected)) { var first = rows.FirstOrDefault(r => r.View.Visibility == Visibility.Visible && !r.Added); if (first != null) Toggle(first); }
                Finish();
                e.Handled = true;
            }
        };
        scroller.ScrollChanged += (_, _) => LoadVisibleIcons();
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4);
        };
        Loaded += async (_, _) =>
        {
            Activate();
            search.Focus();
            var apps = await AppCatalog.AllAsync();
            list.Children.Clear();
            foreach (var a in apps)
                AddRow(new Row { Name = a.Name, Path = a.LaunchPath, App = a, Added = existing.Contains(a.LaunchPath) });
            if (apps.Count == 0) list.Children.Add(new TextBlock { Text = "Couldn't read your app list. Use Browse files… instead.", Foreground = Gray, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap });
            Filter();
            UpdateStatus();
        };
        UpdateStatus();
    }

    Button Pill(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Foreground = Brushes.White };
        b.SetResourceReference(StyleProperty, "Pill");
        b.SetResourceReference(BackgroundProperty, "Set.Field");
        b.Click += (_, _) => click();
        return b;
    }

    void AddRow(Row r, bool atTop = false)
    {
        var g = new Grid { Margin = new Thickness(10, 7, 12, 7) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Round tick box
        r.Check = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        r.Check.BorderBrush = Gray;
        r.Check.Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        g.Children.Add(r.Check);

        r.Icon = new Image { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(r.Icon, BitmapScalingMode.HighQuality);
        Grid.SetColumn(r.Icon, 1);
        g.Children.Add(r.Icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = r.Name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
        if (r.Detail != null) text.Children.Add(new TextBlock { Text = r.Detail, FontSize = 11, Foreground = Gray, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 2);
        g.Children.Add(text);

        if (r.Added)
        {
            var added = new TextBlock { Text = "Already added", FontSize = 11.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(added, 3);
            g.Children.Add(added);
            r.Check.Opacity = 0.3;
            text.Opacity = 0.55;
        }

        r.View = new Border { CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Child = g, Cursor = r.Added ? Cursors.Arrow : Cursors.Hand };
        r.View.MouseEnter += (_, _) => { if (!r.Selected && !r.Added) r.View.SetResourceReference(Border.BackgroundProperty, "Brim.Hover"); };
        r.View.MouseLeave += (_, _) => { if (!r.Selected) r.View.Background = Brushes.Transparent; };
        r.View.MouseLeftButtonUp += (_, _) => { if (!r.Added) Toggle(r); };

        if (atTop) { rows.Insert(0, r); list.Children.Insert(0, r.View); }
        else { rows.Add(r); list.Children.Add(r.View); }
    }

    void Toggle(Row r)
    {
        if (!r.Selected && rows.Count(x => x.Selected) >= maxNew) { status.Text = $"You can add {maxNew} more."; return; }
        r.Selected = !r.Selected;
        var tick = (TextBlock)r.Check.Child;
        if (r.Selected)
        {
            r.Check.SetResourceReference(Border.BackgroundProperty, "Brim.Accent");
            r.Check.SetResourceReference(Border.BorderBrushProperty, "Brim.Accent");
            tick.SetResourceReference(TextBlock.ForegroundProperty, "Brim.OnAccent");
            tick.Visibility = Visibility.Visible;
            r.View.SetResourceReference(Border.BackgroundProperty, "Set.Field");
        }
        else
        {
            r.Check.Background = Brushes.Transparent;
            r.Check.BorderBrush = Gray;
            tick.Visibility = Visibility.Collapsed;
            r.View.Background = Brushes.Transparent;
        }
        UpdateStatus();
    }

    void UpdateStatus()
    {
        int n = rows.Count(r => r.Selected);
        addBtn.Content = n == 0 ? verb : $"{verb} ({n})";
        addBtn.IsEnabled = n > 0;
        addBtn.Opacity = n > 0 ? 1 : 0.5;
        status.Text = n == 0 ? "Tick the apps you want. Not listed? Use Browse files…" : string.Join(", ", rows.Where(r => r.Selected).Select(r => r.Name));
    }

    void Filter()
    {
        var q = search.Text.Trim();
        foreach (var r in rows)
        {
            bool show = q.Length == 0 || r.Selected || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                        string.Concat(r.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0])).StartsWith(q, StringComparison.CurrentCultureIgnoreCase);
            r.View.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        scroller.ScrollToTop();
        Dispatcher.BeginInvoke(new Action(LoadVisibleIcons), DispatcherPriority.Background);
    }

    /// <summary>Icons load for the rows you can see (a few hundred apps would otherwise take a moment).</summary>
    void LoadVisibleIcons()
    {
        double top = scroller.VerticalOffset - 200, bottom = scroller.VerticalOffset + scroller.ViewportHeight + 400;
        foreach (var r in rows)
        {
            if (r.IconLoaded || r.View.Visibility != Visibility.Visible || !r.View.IsLoaded) continue;
            var y = r.View.TranslatePoint(new Point(0, 0), list).Y;
            if (y < top || y > bottom) continue;
            r.IconLoaded = true;
            r.Icon.Source = r.App != null ? AppCatalog.Icon(r.App) : IconHelper.ForPath(r.Path);
        }
    }

    void BrowseFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose files or programs",
            Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd;*.ps1|All files|*.*",
            DereferenceLinks = false,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) AddPicked(dlg.FileNames);
    }

    void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose folders", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddPicked(dlg.FolderNames);
    }

    /// <summary>Browsed files and folders join the top of the list, already ticked.</summary>
    void AddPicked(IEnumerable<string> paths)
    {
        foreach (var p in paths.Reverse())
        {
            if (rows.Any(r => string.Equals(r.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            var row = new Row { Name = IconHelper.NiceName(p), Path = p, Detail = p, Added = existing.Contains(p) };
            AddRow(row, atTop: true);
            row.IconLoaded = true;
            row.Icon.Source = IconHelper.ForPath(p);
            if (!row.Added) Toggle(row);
        }
        scroller.ScrollToTop();
    }

    void Finish()
    {
        var chosen = rows.Where(r => r.Selected).ToList();
        if (chosen.Count == 0) return;
        result = chosen.Select(r => new ShortcutItem { Name = r.Name, Path = r.Path }).ToList();
        Close();
    }
}
