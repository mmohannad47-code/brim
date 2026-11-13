using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brim.Core;

namespace Brim.Views;

/// <summary>The notch's Messages tab: notifications from chat and mail apps, filtered your way.</summary>
sealed class MessagesView : Grid
{
    static AppSettings S => SettingsStore.Current;
    readonly Action<ContextMenu, UIElement> showMenu;
    readonly Action close;
    readonly WrapPanel apps = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel list = new();
    readonly TextBlock empty = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };
    string filter = "All";

    public MessagesView(Action<ContextMenu, UIElement> showMenu, Action close)
    {
        this.showMenu = showMenu;
        this.close = close;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var top = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(apps);
        var gear = new Button { Content = "", ToolTip = "Choose apps and people (Settings › Messages)", Width = 28, Height = 28, FontSize = 12 };
        gear.SetResourceReference(StyleProperty, "IconButton");
        gear.Click += (_, _) => { close(); ((App)Application.Current).OpenSettings("Messages"); };
        Grid.SetColumn(gear, 1);
        top.Children.Add(gear);
        Children.Add(top);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = list };
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);
        empty.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        Grid.SetRow(empty, 1);
        Children.Add(empty);

        Loaded += (_, _) => { MessageCenter.Changed += Fill; Fill(); _ = MessageCenter.Refresh(); };
        Unloaded += (_, _) => MessageCenter.Changed -= Fill;
    }

    void Fill()
    {
        var all = MessageCenter.Visible;
        if (filter != "All" && !all.Any(m => m.App == filter)) filter = "All";

        apps.Children.Clear();
        foreach (var (name, count) in new[] { ("All", all.Count) }.Concat(all.GroupBy(m => m.App).Select(g => (g.Key, g.Count()))))
        {
            var rb = new RadioButton { Content = $"{name}  {count}", GroupName = "msgapps", IsChecked = name == filter, Margin = new Thickness(0, 0, 4, 0) };
            rb.SetResourceReference(StyleProperty, "Chip");
            var n = name;
            rb.Checked += (_, _) => { filter = n; Fill(); };
            apps.Children.Add(rb);
            if (apps.Children.Count >= 5) break;
        }

        list.Children.Clear();
        var shown = all.Where(m => filter == "All" || m.App == filter).Take(40).ToList();
        foreach (var m in shown) list.Children.Add(Row(m));
        empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var held = MessageCenter.HeldBecause();
        empty.Text = MessageCenter.Status
                     ?? (held != null
                         ? $"Windows is holding notifications right now: {held}. Apps like Discord stay quiet until it's closed."
                         : "No messages. When Teams, Discord, Outlook or another app sends Windows a notification, it shows up here.\nApps skip that while you're active in them (Discord sends to your phone if you're on it there).");
    }

    FrameworkElement Row(MessageItem m)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (m.Icon != null) g.Children.Add(new Image { Source = m.Icon, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
        else
        {
            var letter = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            letter.SetResourceReference(Border.BackgroundProperty, "Brim.Accent");
            var t = new TextBlock { Text = m.App.Length > 0 ? m.App[..1].ToUpperInvariant() : "?", FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Brim.OnAccent");
            letter.Child = t;
            g.Children.Add(letter);
        }
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = m.Sender, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (m.Text.Length > 0)
        {
            var body = new TextBlock { Text = m.Text, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 32, Margin = new Thickness(0, 1, 0, 0) };
            body.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
            text.Children.Add(body);
        }
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var right = new TextBlock { Text = m.Ago, FontSize = 10.5, Margin = new Thickness(8, 2, 0, 0), ToolTip = m.App };
        right.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        Grid.SetColumn(right, 2);
        g.Children.Add(right);

        var b = new Button { Content = g, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6, 8, 6), ToolTip = $"{m.App} · click to open" };
        b.SetResourceReference(StyleProperty, "Tile");
        b.Click += (_, _) => { MessageCenter.Open(m); close(); };
        b.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            var rule = MessageCenter.Rule(m.App);
            menu.Items.Add(Item($"Only show {m.App} messages from {Short(m.Sender)}", () => MessageCenter.Follow(m)));
            menu.Items.Add(Item($"Mute {Short(m.Sender)}", () => MessageCenter.Mute(m)));
            if (rule.Mode != "All") menu.Items.Add(Item($"Show everything from {m.App}", () => MessageCenter.SetMode(m.App, "All")));
            menu.Items.Add(Item($"Stop showing {m.App}", () => MessageCenter.SetMode(m.App, "Off")));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Dismiss", () => MessageCenter.Dismiss(m)));
            menu.Items.Add(Item($"Dismiss all from {m.App}", () => MessageCenter.DismissApp(m.App)));
            showMenu(menu, b);
        };
        return b;
    }

    internal string Describe() => $"{list.Children.Count} rows, empty text {(empty.Visibility == Visibility.Visible ? "shown" : "hidden")}, chips {apps.Children.Count}";

    static string Short(string s) => s.Length > 28 ? s[..27] + "…" : s;

    static MenuItem Item(string header, Action a)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => a();
        return mi;
    }
}

/// <summary>
/// The notch's Notes tab: one page to type whatever you like. It saves as you type (to notes.txt next to Brim's
/// settings, so it's also easy to open or back up), and is kept when Brim updates.
/// </summary>
sealed class NotesView : Grid
{
    public static readonly string File = System.IO.Path.Combine(SettingsStore.Folder, "notes.txt");
    readonly TextBox box = new()
    {
        AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, FontSize = 13.5,
        VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(10, 8, 10, 8),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, SpellCheck = { IsEnabled = true },
    };
    readonly TextBlock hint = new() { Text = "Type anything. It saves by itself.", FontSize = 13.5, Margin = new Thickness(13, 9, 0, 0), IsHitTestVisible = false };
    readonly TextBlock status = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    readonly System.Windows.Threading.DispatcherTimer saveSoon = new() { Interval = TimeSpan.FromMilliseconds(600) };
    bool dirty;

    public NotesView(Action<TextBox> startTyping)
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        box.SetResourceReference(StyleProperty, "Field");
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        status.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        Children.Add(box);
        Children.Add(hint);

        var bar = new DockPanel { Margin = new Thickness(2, 6, 0, 0), LastChildFill = false };
        bar.Children.Add(status);
        foreach (var (glyph, tip, act) in new (string, string, Action)[]
        {
            ("", "Clear the page (it can be undone with Ctrl+Z while you're typing)", Clear),
            ("", "Copy everything", () => { try { Clipboard.SetText(box.Text); Status("Copied ✓"); } catch { } }),
            ("", "Open as a text file", () => { SaveNow(); Launcher.Open(File); }),
        })
        {
            var b = new Button { Content = glyph, ToolTip = tip, Width = 26, Height = 26, FontSize = 11.5, Margin = new Thickness(2, 0, 0, 0) };
            b.SetResourceReference(StyleProperty, "IconButton");
            b.Click += (_, _) => act();
            DockPanel.SetDock(b, Dock.Right);
            bar.Children.Add(b);
        }
        Grid.SetRow(bar, 1);
        Children.Add(bar);

        try { if (System.IO.File.Exists(File)) box.Text = System.IO.File.ReadAllText(File); } catch { }
        UpdateHint();
        // The notch never takes the keyboard on its own (it would steal it from your game or app): only when you click in here
        box.PreviewMouseLeftButtonDown += (_, _) => startTyping(box);
        box.TextChanged += (_, _) => { dirty = true; UpdateHint(); saveSoon.Stop(); saveSoon.Start(); };
        saveSoon.Tick += (_, _) => { saveSoon.Stop(); SaveNow(); };
        Unloaded += (_, _) => SaveNow();
        IsVisibleChanged += (_, _) => { if (!IsVisible) SaveNow(); };
    }

    public void Focus(Action<TextBox> startTyping) { startTyping(box); box.CaretIndex = box.Text.Length; }

    void UpdateHint() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void Clear()
    {
        if (box.Text.Length == 0) return;
        // Keep what was there, in case it was cleared by mistake
        try { System.IO.File.WriteAllText(File + ".bak", box.Text); } catch { }
        box.SelectAll();
        box.SelectedText = ""; // (selection replace keeps Ctrl+Z working)
        SaveNow();
        Status("Cleared (the old page is in notes.txt.bak)");
    }

    void SaveNow()
    {
        if (!dirty) return;
        try
        {
            System.IO.Directory.CreateDirectory(SettingsStore.Folder);
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, box.Text);
            System.IO.File.Move(tmp, File, true);
            dirty = false;
            Status("Saved");
        }
        catch (Exception ex) { Log.Error("notes save", ex); Status("Couldn't save"); }
    }

    void Status(string s) => status.Text = s;
}

/// <summary>For `--messages-test`: the real Messages tab, and how many rows it drew.</summary>
public sealed class MessagesViewProbe
{
    readonly MessagesView view = new((m, t) => { }, () => { });
    public FrameworkElement Element => view;
    public string Rows => view.Describe();
}
