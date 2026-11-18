using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Brim.Core;

namespace Brim.Views;

/// <summary>
/// Brim's storage helper. On the left, Clean up: caches, temp files, crash dumps, old installers and Windows' own
/// leftovers, each with what it is and whether it's safe. On the right, everything on a drive sorted by size, with a
/// word of advice on what each thing is and whether it can go; also the largest files on the drive and big files
/// nobody has touched for a year.
/// </summary>
public sealed class StorageWindow : Window
{
    static StorageWindow? open;

    static readonly Color Green = Color.FromRgb(0x7B, 0xE0, 0xA0), Amber = Color.FromRgb(0xFF, 0xC8, 0x5C), Grey = Color.FromRgb(0x8E, 0x8E, 0x96), Blue = Color.FromRgb(0x6C, 0xA8, 0xFF);

    readonly WrapPanel driveChips = new();
    readonly StackPanel cleanList = new(), systemList = new();
    readonly Button cleanBtn, systemBtn;
    readonly TextBlock cleanNote = Dim(""), systemNote = Dim("");
    readonly WrapPanel crumbs = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel rows = new();
    readonly TextBlock summary = Dim("");
    readonly ScrollViewer listScroll;
    readonly DockPanel crumbRow;
    readonly Dictionary<string, RadioButton> sortChips = new(), viewChips = new();

    string drive;
    string view = "folders";
    string sort = "size";
    bool descending = true;
    ScanResult? scan;
    DirNode? at;
    List<FileEntry>? atFiles;
    CancellationTokenSource? scanStop;
    List<InstalledProgram>? programs;
    List<CleanItem> items = new();
    bool cleaning;

    public static void Open(string? drive = null)
    {
        if (open != null)
        {
            App.BringToFront(open);
            if (drive != null && !string.Equals(drive, open.drive, StringComparison.OrdinalIgnoreCase)) open.Pick(drive);
            return;
        }
        open = new StorageWindow(drive ?? Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\");
        open.Show();
        App.BringToFront(open);
    }

    StorageWindow(string firstDrive)
    {
        drive = firstDrive;
        Title = "Brim · Storage";
        Width = 1180; Height = 780; MinWidth = 920; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("Brim.Font");
        SetResourceReference(BackgroundProperty, "Set.Bg");
        SetResourceReference(ForegroundProperty, "Brim.Text");
        UseLayoutRounding = true;
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/brim-64.png")); } catch { }
        SourceInitialized += (_, _) => { int dark = 1; Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4); };
        Closed += (_, _) =>
        {
            scanStop?.Cancel();
            scan = null; at = null; atFiles = null;
            open = null;
            ((App)Application.Current).ScheduleTrim();
        };

        var root = new Grid { Margin = new Thickness(22, 16, 22, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(370) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // ---- Header: title and drives
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(new TextBlock { Text = "Storage", FontSize = 22, FontWeight = FontWeights.SemiBold });
        head.Children.Add(Spaced(Dim("See what fills your drives, and clean up what's safe to. Click a drive to look through it."), 2, 12));
        head.Children.Add(driveChips);
        Grid.SetColumnSpan(head, 3);
        root.Children.Add(head);

        // ---- Left: Clean up
        var left = new StackPanel();
        cleanBtn = Pill("Clean selected", accent: true, glyph: "\uE74D");
        cleanBtn.Click += async (_, _) => await CleanPicked();
        systemBtn = Pill("Clean system files…", glyph: "\uEA18");
        systemBtn.Click += async (_, _) => await CleanSystem();
        var c1 = new StackPanel();
        c1.Children.Add(Heading("Clean up"));
        c1.Children.Add(Spaced(Dim("Caches and temp files are deleted (apps make them again). Your own files, like old installers, go to the Recycle Bin."), 2, 10));
        c1.Children.Add(cleanList);
        c1.Children.Add(Spaced(cleanBtn, 12, 0));
        c1.Children.Add(Spaced(cleanNote, 6, 0));
        left.Children.Add(Card(c1));
        var c2 = new StackPanel();
        c2.Children.Add(Heading("Windows system files"));
        c2.Children.Add(Spaced(Dim("Windows' own folders need administrator rights: Windows asks you once when you clean them."), 2, 10));
        c2.Children.Add(systemList);
        c2.Children.Add(Spaced(systemBtn, 12, 0));
        c2.Children.Add(Spaced(systemNote, 6, 0));
        left.Children.Add(Spaced(Card(c2), 12, 0));
        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
        Grid.SetRow(leftScroll, 1);
        root.Children.Add(leftScroll);

        // ---- Right: what's on the drive
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        var bar = new DockPanel { LastChildFill = false };
        var views = new WrapPanel();
        foreach (var (id, label) in new[] { ("folders", "Folders"), ("largest", "Largest files"), ("old", "Old big files") })
        {
            var rb = Chip(label, "view", id == view);
            rb.Checked += (_, _) => { view = id; ShowList(); listScroll?.ScrollToTop(); };
            viewChips[id] = rb;
            views.Children.Add(rb);
        }
        bar.Children.Add(views);
        var sorts = new WrapPanel();
        DockPanel.SetDock(sorts, Dock.Right);
        sorts.Children.Add(Spaced(Dim("Sort by"), 0, 6, 8));
        foreach (var (id, label) in new[] { ("size", "Size"), ("name", "Name"), ("date", "Changed") })
        {
            var rb = Chip(label, "sort", id == sort);
            rb.PreviewMouseLeftButtonDown += (_, e) =>
            {
                // Clicking the sort that's on turns it around
                if (sort == id) { descending = !descending; e.Handled = true; ShowSortArrows(); ShowList(); }
            };
            rb.Checked += (_, _) => { sort = id; descending = id != "name"; ShowSortArrows(); ShowList(); };
            sortChips[id] = rb;
            sorts.Children.Add(rb);
        }
        bar.Children.Add(sorts);
        right.Children.Add(bar);
        crumbRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var up = new Button { Content = "\uE74A", ToolTip = "Up one folder (Backspace)", Margin = new Thickness(0, 0, 6, 0) };
        up.SetResourceReference(StyleProperty, "IconButton");
        up.Click += (_, _) => Up();
        crumbRow.Children.Add(up);
        crumbRow.Children.Add(crumbs);
        Grid.SetRow(crumbRow, 1);
        right.Children.Add(crumbRow);
        summary.TextWrapping = TextWrapping.Wrap;
        summary.TextTrimming = TextTrimming.None;
        summary.Margin = new Thickness(2, 0, 0, 8);
        Grid.SetRow(summary, 2);
        right.Children.Add(summary);
        listScroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var listCard = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(6), Child = listScroll };
        listCard.SetResourceReference(Border.BackgroundProperty, "Set.Card");
        Grid.SetRow(listCard, 3);
        right.Children.Add(listCard);
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        Content = root;
        ShowSortArrows();

        KeyDown += (_, e) => { if (e.Key == Key.Back) Up(); };
        // Back from closing Chrome (or opening it): the clean up list follows
        Activated += (_, _) => { if (items.Count > 0 && !cleaning) FillItems(); };
        Loaded += async (_, _) =>
        {
            ShowDrives();
            _ = Scan();
            _ = Task.Run(() => { try { return Uninstaller.Everything(); } catch { return new List<InstalledProgram>(); } })
                    .ContinueWith(t => Dispatcher.BeginInvoke(() => { programs = t.Result; if (scan != null) ShowList(); }));
            await Measure();
        };
    }

    // ================================================================ drives

    void ShowDrives()
    {
        driveChips.Children.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            try
            {
                if (!d.IsReady || d.TotalSize <= 0) continue;
                double used = 1 - (double)d.AvailableFreeSpace / d.TotalSize;
                string label = d.VolumeLabel.Length > 0 ? d.VolumeLabel : d.DriveType == DriveType.Removable ? "USB drive" : "Local Disk";
                var name = d.Name;
                var rb = Chip(null, "drive", string.Equals(name, drive, StringComparison.OrdinalIgnoreCase));
                // Drawn in the chip's own text colour, so it reads on the picked (accent) chip too
                var ink = new System.Windows.Data.Binding("Foreground") { Source = rb };
                var body = new StackPanel { Width = 200 };
                body.Children.Add(new TextBlock { Text = $"{label} ({d.Name.TrimEnd('\\')})", FontSize = 13, FontWeight = FontWeights.SemiBold });
                var track = new Grid { Height = 5, Margin = new Thickness(0, 6, 0, 4) };
                var back = new Border { CornerRadius = new CornerRadius(2.5), Opacity = 0.22 };
                back.SetBinding(Border.BackgroundProperty, ink);
                var fill = new Border { CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left };
                if (d.AvailableFreeSpace < d.TotalSize * 0.12) fill.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
                else fill.SetBinding(Border.BackgroundProperty, ink);
                track.Children.Add(back);
                track.Children.Add(fill);
                track.SizeChanged += (_, _) => fill.Width = Math.Max(5, track.ActualWidth * used);
                body.Children.Add(track);
                body.Children.Add(new TextBlock { Text = $"{StorageScan.Size(d.AvailableFreeSpace)} free of {StorageScan.Size(d.TotalSize)}", FontSize = 11.5, Opacity = 0.75 });
                rb.Content = body;
                rb.Checked += (_, _) => { if (!string.Equals(drive, name, StringComparison.OrdinalIgnoreCase)) Pick(name); };
                driveChips.Children.Add(rb);
            }
            catch { }
        }
    }

    void Pick(string d)
    {
        drive = d;
        foreach (var rb in driveChips.Children.OfType<RadioButton>()) rb.IsChecked = rb.Content is StackPanel s && s.Children[0] is TextBlock t && t.Text.EndsWith($"({d.TrimEnd('\\')})");
        _ = Scan();
    }

    // ================================================================ scanning

    async Task Scan()
    {
        scanStop?.Cancel();
        var stop = scanStop = new CancellationTokenSource();
        scan = null; at = null; atFiles = null;
        rows.Children.Clear();
        crumbs.Children.Clear();
        var status = new TextBlock { FontSize = 14, Margin = new Thickness(16, 18, 16, 6) };
        var sub = Dim("Brim reads the folder list only; nothing is opened or changed.");
        sub.Margin = new Thickness(16, 0, 16, 12);
        var bar = new ProgressBar { IsIndeterminate = true, Margin = new Thickness(16, 0, 16, 16) };
        bar.SetResourceReference(StyleProperty, "ThinBar");
        rows.Children.Add(status);
        rows.Children.Add(sub);
        rows.Children.Add(bar);
        status.Text = $"Looking through {drive.TrimEnd('\\')}…";
        summary.Text = "";
        try
        {
            var result = await StorageScan.Run(drive, new Progress<(long bytes, long files)>(p =>
            {
                if (!stop.IsCancellationRequested) status.Text = $"Looking through {drive.TrimEnd('\\')}…  {StorageScan.Size(p.bytes)} in {p.files:N0} files so far";
            }), stop.Token);
            if (stop.IsCancellationRequested) return;
            scan = result;
            at = result.Root;
            Log.Note("storage", $"scanned {drive}: {StorageScan.Size(result.Root.Size)} in {result.Root.Files:N0} files, {result.Root.Folders:N0} folders, {result.Denied} closed folders, {result.Took.TotalSeconds:0.0} s");
            await Navigate(result.Root);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("storage scan", ex);
            status.Text = "Couldn't read this drive: " + ex.Message;
            bar.Visibility = Visibility.Collapsed;
        }
    }

    async Task Navigate(DirNode node)
    {
        at = node;
        atFiles = null;
        if (view != "folders") viewChips["folders"].IsChecked = true;
        ShowCrumbs();
        var path = node.Path;
        atFiles = await Task.Run(() => StorageScan.Files(path));
        if (at != node) return;
        ShowList();
        listScroll.ScrollToTop();
    }

    void Up()
    {
        if (at?.Parent != null) _ = Navigate(at.Parent);
    }

    void ShowCrumbs()
    {
        crumbs.Children.Clear();
        if (at == null) return;
        var chain = new List<DirNode>();
        for (var n = at; n != null; n = n.Parent) chain.Insert(0, n);
        for (int i = 0; i < chain.Count; i++)
        {
            var n = chain[i];
            if (i > 0) crumbs.Children.Add(new TextBlock { Text = "\uE76C", FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 9, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
            var b = new Button { Content = i == 0 ? n.Name.TrimEnd('\\') : n.Name, Padding = new Thickness(8, 4, 8, 4), FontSize = 12.5 };
            b.SetResourceReference(StyleProperty, "Pill");
            if (n == at) b.FontWeight = FontWeights.SemiBold;
            b.Click += (_, _) => _ = Navigate(n);
            crumbs.Children.Add(b);
        }
    }

    // ================================================================ the list

    sealed record Entry(string Name, string Path, bool Dir, long Size, DateTime Modified, long Files, DirNode? Node, bool Denied);

    void ShowList()
    {
        if (scan == null || at == null) return;
        crumbRow.Visibility = view == "folders" ? Visibility.Visible : Visibility.Collapsed;
        rows.Children.Clear();
        List<Entry> list;
        long total;
        if (view == "folders")
        {
            list = (at.Kids ?? new List<DirNode>()).Select(k => new Entry(k.Name, k.Path, true, k.Size, k.Modified, k.Files, k, k.Denied && k.Size == 0)).ToList();
            if (atFiles != null) list.AddRange(atFiles.Select(f => new Entry(f.Name, Path.Combine(at.Path, f.Name), false, f.Size, f.Modified, 0, null, false)));
            total = Math.Max(1, at.Size);
            var di = TryDrive();
            if (at.Parent == null && di != null)
            {
                long used = di.TotalSize - di.AvailableFreeSpace, hidden = used - at.Size;
                summary.Text = $"{StorageScan.Size(used)} used on {drive.TrimEnd('\\')}. Brim looked through {StorageScan.Size(at.Size)} in {at.Files:N0} files ({scan.Took.TotalSeconds:0} s)." +
                               (hidden > 1L << 30 ? $" The other {StorageScan.Size(hidden)} is in places only Windows can look into (restore points, system files, other accounts)." : "");
            }
            else summary.Text = $"{StorageScan.Size(at.Size)} in {at.Files:N0} files and {at.Folders:N0} folders" + (at.Modified > DateTime.MinValue ? $" · last changed {at.Modified:d MMM yyyy}" : "");
        }
        else
        {
            var cutoff = DateTime.Now.AddYears(-1);
            var files = view == "old" ? scan.Largest.Where(f => f.Modified < cutoff && f.Size >= 50L << 20) : scan.Largest;
            list = files.Select(f => new Entry(Path.GetFileName(f.Path), f.Path, false, f.Size, f.Modified, 0, null, false)).ToList();
            total = Math.Max(1, list.Count > 0 ? list.Max(e => e.Size) : 1);
            summary.Text = view == "old"
                ? $"Big files on {drive.TrimEnd('\\')} that haven't changed for over a year: {StorageScan.Size(list.Sum(e => e.Size))} in {list.Count} files. Old photos and videos are worth keeping somewhere; old installers and archives often aren't."
                : $"The {list.Count} largest files on {drive.TrimEnd('\\')}: {StorageScan.Size(list.Sum(e => e.Size))} together.";
        }

        IEnumerable<Entry> sorted = sort switch
        {
            "name" => list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            "date" => list.OrderBy(e => e.Modified),
            _ => list.OrderBy(e => e.Size),
        };
        if (descending) sorted = sorted.Reverse();
        var shown = sorted.ToList();
        const int Max = 300;
        foreach (var e in shown.Take(Max)) rows.Children.Add(Row(e, total));
        if (shown.Count > Max)
        {
            var rest = shown.Skip(Max).ToList();
            rows.Children.Add(Spaced(Dim($"…and {rest.Count:N0} more ({StorageScan.Size(rest.Sum(x => x.Size))}). Sort differently or open a folder to see them."), 10, 10, 14));
        }
        if (shown.Count == 0) rows.Children.Add(Spaced(Dim(view == "folders" ? "This folder is empty." : "Nothing here."), 14, 14, 14));
    }

    DriveInfo? TryDrive() { try { return new DriveInfo(drive); } catch { return null; } }

    FrameworkElement Row(Entry e, long total)
    {
        var advice = e.Denied ? new Advice(Safety.Keep, "Windows keeps this to itself (needs administrator rights or belongs to another account).")
                              : StorageAdvice.For(e.Path, e.Dir, e.Size, e.Modified, programs);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        var icon = new TextBlock { Text = e.Dir ? "\uE8B7" : "\uE7C3", FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Opacity = e.Dir ? 0.95 : 0.6 };
        if (e.Dir) icon.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C));
        g.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = e.Name, FontSize = 13, FontWeight = e.Dir ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        if (view != "folders")
        {
            var where = Dim(Path.GetDirectoryName(e.Path) ?? "");
            where.TextWrapping = TextWrapping.NoWrap;
            where.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(where);
        }
        if (advice != null)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            line.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(LevelColor(advice.Level)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
            var t = Dim(advice.Text);
            t.TextWrapping = TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.ToolTip = advice.Text;
            line.Children.Add(t);
            text.Children.Add(line);
        }
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        track.SetResourceReference(Border.BackgroundProperty, "Brim.Track");
        double frac = Math.Clamp((double)e.Size / total, 0, 1);
        var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(advice != null ? LevelColor(advice.Level) : Blue) };
        track.Child = fill;
        track.SizeChanged += (_, _) => fill.Width = frac <= 0 ? 0 : Math.Max(3, track.ActualWidth * frac);
        Grid.SetColumn(track, 2);
        g.Children.Add(track);

        var size = new TextBlock { Text = e.Denied ? "—" : StorageScan.Size(e.Size), FontSize = 13, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(size, 3);
        g.Children.Add(size);
        var date = Dim(e.Modified > DateTime.MinValue ? e.Modified.ToString("d MMM yyyy") : "");
        date.TextAlignment = TextAlignment.Right;
        date.ToolTip = e.Dir ? "Newest change inside" : "Last changed";
        Grid.SetColumn(date, 4);
        g.Children.Add(date);

        var row = new Border { Padding = new Thickness(10, 7, 12, 7), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Child = g, Cursor = e.Node != null ? Cursors.Hand : null };
        row.ToolTip = e.Path;
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Brim.Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => { if (e.Node != null) _ = Navigate(e.Node); };
        // Double-click a file: show it in Explorer
        row.MouseLeftButtonDown += (_, ev) => { if (ev.ClickCount == 2 && e.Node == null) Launcher.ShowInFolder(e.Path); };
        row.ContextMenu = Menu(e, advice);
        return row;
    }

    static Color LevelColor(Safety s) => s switch { Safety.Safe => Green, Safety.Review => Amber, _ => Grey };

    ContextMenu Menu(Entry e, Advice? advice)
    {
        var m = new ContextMenu();
        void Add(string header, Action a, bool enabled = true, string? tip = null)
        {
            var i = new MenuItem { Header = header, IsEnabled = enabled };
            if (tip != null) { i.ToolTip = tip; ToolTipService.SetShowOnDisabled(i, true); }
            i.Click += (_, _) => a();
            m.Items.Add(i);
        }
        if (e.Node != null) Add("Look inside", () => _ = Navigate(e.Node));
        Add(e.Dir ? "Open in Explorer" : "Open", () => Launcher.Open(e.Path));
        Add("Show in folder", () => Launcher.ShowInFolder(e.Path));
        Add("Copy path", () => { try { Clipboard.SetText(e.Path); } catch { } });
        if (advice?.Program is { } p)
        {
            m.Items.Add(new Separator());
            Add($"Uninstall {p.Name}…", () => ((App)Application.Current).OpenUninstall(p));
        }
        m.Items.Add(new Separator());
        var why = WhyNotRecycle(e, advice);
        Add("Move to the Recycle Bin…", () => Recycle(e), why == null, why != null ? "Brim won't remove this: " + why : null);
        return m;
    }

    /// <summary>
    /// What Brim refuses to move to the Recycle Bin from here: drives, Windows, your account's own folders (Users,
    /// your profile, AppData, Documents…), folders that hold other important folders, files Windows marks as its
    /// own, and installed programs (those get uninstalled instead, or they'd break and couldn't be removed).
    /// </summary>
    static string? WhyNotRecycle(Entry e, Advice? advice)
    {
        var path = e.Path.TrimEnd('\\');
        if (path.Length <= 3) return "it's a whole drive.";
        if (advice?.Level == Safety.Keep) return advice.Text;
        if (advice?.Program is { } p) return $"it's part of {p.Name}. Uninstall it instead.";
        if (e.Dir && !Uninstaller.SafeToRecycle(path, new List<string>())) return "it's one of Windows' own folders, or holds one.";
        if (!e.Dir)
        {
            try { if ((File.GetAttributes(path) & FileAttributes.System) != 0) return "Windows marks it as a system file."; }
            catch { }
        }
        return null;
    }

    void Recycle(Entry e)
    {
        var what = e.Dir ? $"the folder \"{e.Name}\" and everything in it ({StorageScan.Size(e.Size)})" : $"\"{e.Name}\" ({StorageScan.Size(e.Size)})";
        if (MessageBox.Show(this, $"Move {what} to the Recycle Bin?\n\n{e.Path}\n\nYou can put it back from the Recycle Bin until you empty it.", "Storage",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        bool ok = RecycleBin.Send(new[] { e.Path }, new WindowInteropHelper(this).Handle);
        bool gone = !(e.Dir ? Directory.Exists(e.Path) : File.Exists(e.Path));
        Log.Note("storage", $"recycle {e.Path}: {(gone ? "done" : "kept")}");
        if (!gone) { if (!ok) MessageBox.Show(this, "Windows didn't move it (it may be in use, or you cancelled).", "Storage"); return; }
        // Take it off the numbers without scanning again
        if (e.Node != null)
        {
            e.Node.Parent?.Kids?.Remove(e.Node);
            StorageScan.Subtract(e.Node.Parent, e.Size, e.Files);
        }
        else
        {
            var parentPath = Path.GetDirectoryName(e.Path);
            var owner = FindNode(parentPath);
            if (owner != null) { owner.Own -= e.Size; StorageScan.Subtract(owner, e.Size, 1); }
            atFiles?.RemoveAll(f => string.Equals(Path.Combine(at!.Path, f.Name), e.Path, StringComparison.OrdinalIgnoreCase));
            scan?.Largest.RemoveAll(f => string.Equals(f.Path, e.Path, StringComparison.OrdinalIgnoreCase));
        }
        ShowList();
        ShowDrives();
    }

    DirNode? FindNode(string? path)
    {
        if (scan == null || path == null) return null;
        var n = scan.Root;
        var rest = path.Length > n.Name.Length ? path[n.Name.Length..] : "";
        foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            n = n.Kids?.FirstOrDefault(k => string.Equals(k.Name, part, StringComparison.OrdinalIgnoreCase));
            if (n == null) return null;
        }
        return n;
    }

    // ================================================================ clean up

    async Task Measure()
    {
        items = await Task.Run(() => Cleanup.Items());
        FillItems();
        await Cleanup.MeasureAll(items, _ => Dispatcher.BeginInvoke(FillItems));
        FillItems();
    }

    void FillItems()
    {
        cleanList.Children.Clear();
        systemList.Children.Clear();
        foreach (var i in items)
        {
            // Only what's there: an item Brim measured as empty isn't worth a row
            if (i.Size == 0 && i.Note == null) continue;
            // An app that's open (Chrome, Discord…) keeps its cache: untick it until it's closed, then tick it again
            var why = i.Size == -1 ? null : i.BlockedNow;
            if (why != null && i.Picked) { i.Picked = false; heldBack.Add(i.Id); }
            else if (why == null && heldBack.Remove(i.Id)) i.Picked = true;
            (i.Admin ? systemList : cleanList).Children.Add(ItemRow(i, why));
        }
        if (cleanList.Children.Count == 0 && items.Where(i => !i.Admin).All(i => i.Size >= 0)) cleanList.Children.Add(Dim("Nothing to clean right now."));
        if (systemList.Children.Count == 0 && items.Where(i => i.Admin).All(i => i.Size >= 0)) systemList.Children.Add(Dim("Nothing to clean right now."));
        long user = items.Where(i => i.Picked && !i.Admin && i.Size > 0).Sum(i => i.Size);
        int userCount = items.Count(i => i.Picked && !i.Admin && i.Size > 0);
        SetPill(cleanBtn, user > 0 ? $"Clean {userCount} selected · {StorageScan.Size(user)}" : "Clean selected");
        cleanBtn.IsEnabled = !cleaning && userCount > 0;
        long sys = items.Where(i => i.Picked && i.Admin && i.Size > 0).Sum(i => i.Size);
        int sysCount = items.Count(i => i.Picked && i.Admin);
        SetPill(systemBtn, sysCount > 0 ? $"Clean system files · {sysCount} selected{(sys > 0 ? " · " + StorageScan.Size(sys) + "+" : "")}…" : "Clean system files…");
        systemBtn.IsEnabled = !cleaning && sysCount > 0;
    }

    readonly HashSet<string> heldBack = new();

    FrameworkElement ItemRow(CleanItem i, string? blocked)
    {
        var body = new StackPanel();
        var top = new DockPanel();
        var size = new TextBlock
        {
            Text = i.Size == -1 ? "…" : i.Size == -2 ? "size unknown" : StorageScan.Size(i.Size),
            FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0),
        };
        if (i.Size == -2) { size.FontWeight = FontWeights.Normal; size.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim"); size.ToolTip = "Only an administrator can look inside; Windows tells Brim the size once you clean it."; }
        DockPanel.SetDock(size, Dock.Right);
        top.Children.Add(size);
        top.Children.Add(new TextBlock { Text = i.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(top);
        var tag = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        tag.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(LevelColor(i.Safety)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
        tag.Children.Add(new TextBlock { Text = i.Safety == Safety.Safe ? "Safe to clean" : "Check first", FontSize = 11.5, Foreground = new SolidColorBrush(LevelColor(i.Safety)) });
        if (i.Recycle) tag.Children.Add(Spaced(Dim("· goes to the Recycle Bin"), 0, 0, 0, 6));
        body.Children.Add(tag);
        var adv = Dim(i.Advice);
        adv.TextWrapping = TextWrapping.Wrap;
        adv.TextTrimming = TextTrimming.None;
        adv.Margin = new Thickness(0, 3, 0, 0);
        body.Children.Add(adv);
        // Your own files: say which ones, so nothing you want goes by surprise
        if (i.Names is { Count: > 0 } names)
        {
            var shown = string.Join("\n", names.Take(8).Select(n => "• " + n)) + (names.Count > 8 ? $"\n…and {names.Count - 8} more" : "");
            var list = new TextBlock { Text = shown, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            list.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Text");
            list.ToolTip = string.Join("\n", names);
            body.Children.Add(list);
        }
        foreach (var text in new[] { blocked, i.Note }.Where(t => t != null).Distinct())
        {
            var note = new TextBlock { Text = text, FontSize = 11.5, Foreground = new SolidColorBrush(Amber), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            body.Children.Add(note);
        }
        var check = new CheckBox { Content = body, IsChecked = i.Picked, IsEnabled = !cleaning && i.Size != 0 && blocked == null, Margin = new Thickness(0, 6, 0, 6) };
        check.SetResourceReference(StyleProperty, "Switch");
        check.Checked += (_, _) => { i.Picked = true; FillItems(); };
        check.Unchecked += (_, _) => { i.Picked = false; FillItems(); };
        return check;
    }

    async Task CleanPicked()
    {
        var picked = items.Where(i => i.Picked && !i.Admin && i.Size > 0).ToList();
        if (picked.Count == 0) return;
        var names = string.Join("\n• ", picked.Select(i => $"{i.Title} ({StorageScan.Size(i.Size)})"));
        if (MessageBox.Show(this, $"Clean these?\n\n• {names}\n\nCaches and temp files are deleted; old installers go to the Recycle Bin.", "Clean up",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        cleaning = true;
        cleanNote.Text = "Cleaning…";
        FillItems();
        long freed = await Cleanup.Clean(picked, _ => { });
        cleaning = false;
        cleanNote.Text = $"Freed {StorageScan.Size(freed)}." + (scan != null ? " Folder sizes on the right update when you look through the drive again." : "");
        cleanNote.Foreground = new SolidColorBrush(Green);
        var notes = picked.Where(i => i.Note != null).ToDictionary(i => i.Id, i => i.Note);
        ShowDrives();
        await Measure();
        foreach (var i in items) if (notes.TryGetValue(i.Id, out var n)) i.Note = n;
        FillItems();
    }

    async Task CleanSystem()
    {
        var picked = items.Where(i => i.Picked && i.Admin).ToList();
        if (picked.Count == 0) return;
        if (picked.Any(i => i.Id == "sys:old") &&
            MessageBox.Show(this, "Removing the previous Windows installation means you can't go back to the Windows version you had before. Go ahead?", "Clean system files",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        cleaning = true;
        systemNote.Text = "Waiting for Windows to ask for permission…";
        FillItems();
        var result = await Cleanup.CleanSystem(picked);
        cleaning = false;
        if (result == null) systemNote.Text = "Not cleaned (permission wasn't given).";
        else
        {
            long freed = result.Values.Sum(r => r.freed);
            systemNote.Text = $"Freed {StorageScan.Size(freed)}.";
            systemNote.Foreground = new SolidColorBrush(Green);
            ShowDrives();
        }
        await Measure();
        if (result != null) foreach (var i in items) if (result.TryGetValue(i.Id, out var r) && r.note != null) i.Note = r.note;
        FillItems();
    }

    // ================================================================ pieces

    void ShowSortArrows()
    {
        foreach (var (id, rb) in sortChips)
        {
            var label = id switch { "size" => "Size", "name" => "Name", _ => "Changed" };
            rb.Content = id == sort ? label + (descending ? "  ↓" : "  ↑") : label;
        }
    }

    static Border Card(UIElement child)
    {
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), Child = child };
        b.SetResourceReference(Border.BackgroundProperty, "Set.Card");
        return b;
    }

    static TextBlock Heading(string t) => new() { Text = t, FontSize = 15, FontWeight = FontWeights.SemiBold };

    static TextBlock Dim(string t)
    {
        var tb = new TextBlock { Text = t, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        if (t.Length > 60) { tb.TextWrapping = TextWrapping.Wrap; tb.TextTrimming = TextTrimming.None; }
        return tb;
    }

    static T Spaced<T>(T e, double top, double bottom, double right = 0, double left = 0) where T : FrameworkElement
    {
        e.Margin = new Thickness(left, top, right, bottom);
        return e;
    }

    RadioButton Chip(string? label, string group, bool on)
    {
        var rb = new RadioButton { Content = label, GroupName = group + GetHashCode(), IsChecked = on };
        rb.SetResourceReference(StyleProperty, "Chip");
        return rb;
    }

    Button Pill(string text, bool accent = false, string? glyph = null)
    {
        var b = new Button { Padding = new Thickness(14, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Left };
        b.SetResourceReference(StyleProperty, "Pill");
        b.Tag = glyph;
        SetPill(b, text);
        if (accent)
        {
            b.SetResourceReference(BackgroundProperty, "Brim.Accent");
            b.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
        }
        return b;
    }

    void SetPill(Button b, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (b.Tag is string g) row.Children.Add(new TextBlock { Text = g, FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        b.Content = row;
    }

    // ================================================================ developer check (--storage-test)

    /// <summary>Open off-screen, scan, measure, take pictures of each view. Removes nothing.</summary>
    internal static async Task<string> SelfTest(string folder)
    {
        var sb = new System.Text.StringBuilder();
        long memBefore = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        var w = new StorageWindow(@"C:\") { Left = -4000, Top = 0, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        open = w;
        bool closed = false;
        w.Closed += (_, _) => closed = true;
        w.Show();
        sb.AppendLine($"after Show: visible {w.IsVisible}, closed {closed}, handle {new WindowInteropHelper(w).Handle}, visibility {w.Visibility}, open==w {open == w}");
        await Task.Delay(500);
        sb.AppendLine($"after 0.5 s: visible {w.IsVisible}, loaded {w.IsLoaded}, closed {closed}, scan {(w.scan != null)}");
        File.WriteAllText(Path.Combine(folder, "partial.txt"), sb.ToString());
        if (closed) return sb.ToString();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((w.scan == null || w.atFiles == null || w.items.Count == 0 || w.items.Any(i => i.Size == -1) || w.programs == null) && sw.Elapsed.TotalMinutes < 5)
            await Task.Delay(250);
        sb.AppendLine($"ready after {sw.Elapsed.TotalSeconds:0.0} s; scan took {w.scan?.Took.TotalSeconds:0.0} s");
        if (w.scan != null)
        {
            var r = w.scan.Root;
            sb.AppendLine($"C: {StorageScan.Size(r.Size)} in {r.Files:N0} files, {r.Folders:N0} folders, {w.scan.Denied} closed folders; largest kept {w.scan.Largest.Count}");
            foreach (var k in (r.Kids ?? new()).OrderByDescending(k => k.Size).Take(10))
                sb.AppendLine($"  {StorageScan.Size(k.Size),10}  {k.Name}  -> {StorageAdvice.For(k.Path, true, k.Size, k.Modified, w.programs)?.Text}");
            sb.AppendLine("largest files:");
            foreach (var f in w.scan.Largest.Take(12))
                sb.AppendLine($"  {StorageScan.Size(f.Size),10}  {f.Path}  -> {StorageAdvice.For(f.Path, false, f.Size, f.Modified, w.programs)?.Text}");
        }
        sb.AppendLine("clean up:");
        foreach (var i in w.items) sb.AppendLine($"  [{(i.Picked ? "x" : " ")}] {i.Title}: {(i.Size < 0 ? i.Size == -2 ? "unknown" : "?" : StorageScan.Size(i.Size))} ({i.Count} files){(i.Admin ? " admin" : "")}");
        long memAfter = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        sb.AppendLine($"memory: {memBefore >> 20} MB before, {memAfter >> 20} MB with the scan");
        sb.AppendLine($"window: visible {w.IsVisible}, loaded {w.IsLoaded}, {w.ActualWidth}x{w.ActualHeight}, state {w.WindowState}");
        File.WriteAllText(Path.Combine(folder, "partial.txt"), sb.ToString());

        void Shot(string name, Window? other = null)
        {
            Window win = other ?? w;
            win.UpdateLayout();
            var shot = new System.Windows.Media.Imaging.RenderTargetBitmap((int)win.ActualWidth, (int)win.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var v = new DrawingVisual();
            using (var dc = v.RenderOpen())
            {
                dc.DrawRectangle(win.Background, null, new Rect(0, 0, win.ActualWidth, win.ActualHeight));
                dc.DrawRectangle(new VisualBrush((Visual)win.Content), null, new Rect(0, 0, win.ActualWidth, win.ActualHeight));
            }
            shot.Render(v);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
            using var f = File.Create(Path.Combine(folder, name + ".png"));
            enc.Save(f);
        }
        Shot("storage-root");
        var users = w.scan?.Root.Kids?.FirstOrDefault(k => k.Name.Equals("Users", StringComparison.OrdinalIgnoreCase));
        var me = users?.Kids?.FirstOrDefault(k => k.Name.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase));
        if (me != null) { await w.Navigate(me); await Task.Delay(300); Shot("storage-home"); }
        w.viewChips["largest"].IsChecked = true;
        await Task.Delay(300);
        Shot("storage-largest");

        // The Uninstall page in Settings, with its new Free up space entry and sorting
        var set = new SettingsWindow { Left = -4000, Top = 0, Height = 1000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        set.Show();
        set.Show("Uninstall");
        await Task.Delay(1200);
        Shot("settings-uninstall", set);
        set.Close();

        // Leftovers: pretend each installed program was removed and see what Brim would offer (nothing is removed)
        sb.AppendLine("leftover check (as if each program had been removed):");
        var all = w.programs ?? new();
        foreach (var p in all.Where(p => !Uninstaller.IsFramework(p)))
        {
            var others = all.Where(o => o.Key != p.Key).ToList();
            var found = Leftovers.Find(p, others);
            if (found.Count > 0) sb.AppendLine($"  {p.Name}: " + string.Join(" | ", found.Select(f => $"{f.Path} ({StorageScan.Size(f.Size)})")));
        }
        w.Close();
        return sb.ToString();
    }
}
