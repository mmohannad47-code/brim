using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Views;

/// <summary>Settings, built in code so every page shares the same few building blocks.</summary>
public sealed class SettingsWindow : Window
{
    static AppSettings S => SettingsStore.Current;
    static App AppRef => (App)Application.Current;

    readonly StackPanel nav = new() { Margin = new Thickness(14, 0, 14, 14) };
    readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly DispatcherTimer commitTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    StackPanel page = new();
    string current = "Notch";

    static readonly (string key, string glyph)[] Pages =
    {
        ("Notch", ""), ("Look", ""), ("Messages", "\uE8BD"), ("Shortcuts", ""), ("Games", ""), ("Widgets", ""),
        ("Taskbar", ""), ("Desktop", ""), ("Uninstall", "\uE74D"), ("General", ""),
    };

    public SettingsWindow()
    {
        Title = "Brim settings";
        Width = 900;
        Height = 660;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("Brim.Font");
        SetResourceReference(BackgroundProperty, "Set.Bg");
        Foreground = Brushes.White;
        UseLayoutRounding = true;

        commitTimer.Tick += (_, _) => { commitTimer.Stop(); SettingsStore.Save(); AppRef.ApplyAll(); };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var side = new DockPanel();
        side.SetResourceReference(Panel.BackgroundProperty, "Set.Side");
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 26, 0, 22) };
        var logo = new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/brim-64.png")),
            Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        brand.Children.Add(logo);
        brand.Children.Add(new TextBlock { Text = "Brim", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), Foreground = Brushes.White });
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);
        foreach (var (key, glyph) in Pages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 14, Width = 28, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton { Content = row, GroupName = "nav", Tag = key, IsChecked = key == current };
            rb.SetResourceReference(StyleProperty, "NavItem");
            rb.Checked += (_, _) => { current = key; Rebuild(); };
            nav.Children.Add(rb);
        }
        side.Children.Add(nav);
        root.Children.Add(side);

        Grid.SetColumn(scroller, 1);
        root.Children.Add(scroller);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            Native.DwmSetWindowAttribute(hwnd, 20 /* USE_IMMERSIVE_DARK_MODE */, ref dark, 4);
        };
        Rebuild();
    }

    public void Show(string page)
    {
        foreach (var rb in nav.Children.OfType<RadioButton>())
            if ((string)rb.Tag == page) rb.IsChecked = true;
    }

    void Commit(bool now = true)
    {
        commitTimer.Stop();
        if (now) { SettingsStore.Save(); AppRef.ApplyAll(); }
        else commitTimer.Start();
    }

    public void Rebuild()
    {
        double offset = scroller.VerticalOffset;
        page = new StackPanel { Margin = new Thickness(36, 30, 36, 40), MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Stretch };
        switch (current)
        {
            case "Notch": NotchPage(); break;
            case "Messages": MessagesPage(); break;
            case "Uninstall": UninstallPage(); break;
            case "Look": LookPage(); break;
            case "Shortcuts": ShortcutsPage(); break;
            case "Games": GamesPage(); break;
            case "Widgets": WidgetsPage(); break;
            case "Taskbar": TaskbarPage(); break;
            case "Desktop": DesktopPage(); break;
            default: GeneralPage(); break;
        }
        scroller.Content = page;
        scroller.ScrollToVerticalOffset(offset);
    }

    // ================================================================== pages

    void NotchPage()
    {
        H1("Notch", "A small bar at the top of your screen. Point at it to open music, shortcuts, your shelf and clipboard.");

        Section("Shape");
        Card(
            Row("Style", "Notch hangs from the top edge like a MacBook. Island floats just below it.",
                Chips(new[] { "Notch", "Island" }, S.Shape.ToString(), v => { S.Shape = Enum.Parse<NotchShape>(v); Commit(); Rebuild(); })),
            Row("Position", "Along the top of the screen", Chips(new[] { "Left", "Center", "Right" }, S.NotchPosition, v => { S.NotchPosition = v; Commit(); })),
            S.Shape == NotchShape.Island
                ? Row("Distance from the top", null, SliderCtl(0, 40, S.IslandTop, v => { S.IslandTop = Math.Round(v); Commit(false); }))
                : null,
            Row("Surface", "Glass blurs what's behind it. Clear is invisible until you open it.",
                Chips(new[] { "Solid", "Glass", "Clear" }, S.NotchSurface, v => { S.NotchSurface = v; Commit(); })),
            Row("When closed, show", null,
                Chips(new[] { "Clock", "Date", "Battery", "Weather", "Cpu", "Nothing" }, S.Face.ToString(), v => { S.Face = Enum.Parse<RestingFace>(v); Commit(); })),
            Row("Live activities", "Pops out for volume changes, charging, switching to headphones, mic mute and the focus timer",
                Switch(S.LiveActivities, v => { S.LiveActivities = v; Commit(); })),
            ScreenRow("Screen", S.NotchScreen, v => { S.NotchScreen = v; Commit(); }, everyScreen: true),
            Row("Song in the closed notch", "Cover, title and live bars while music plays", Switch(S.ShowSongWhenClosed, v => { S.ShowSongWhenClosed = v; Commit(); })),
            Row("Pop out on new song", "Shows the next track for a few seconds", Switch(S.SongPopup, v => { S.SongPopup = v; Commit(); }))
        );

        Section("Behaviour");
        Card(
            Row("Hide while an app is in front", "Shows on the desktop; while Chrome or any other app is focused it tucks away until you point at the top-centre",
                Switch(S.HideOverApps, v => { S.HideOverApps = v; Commit(); })),
            Row("Always hide until I point at it", "Never drawn until your pointer touches the top-centre of the screen, even on the desktop", Switch(S.HideUntilHover, v => { S.HideUntilHover = v; Commit(); })),
            Row("Open delay", $"{S.HoverDelayMs} ms", SliderCtl(0, 600, S.HoverDelayMs, v => { S.HoverDelayMs = (int)Math.Round(v / 10) * 10; Commit(false); }, rebuildLabel: true)),
            Row("Animation", null, Chips(new[] { "Quick", "Smooth", "Relaxed" }, S.Speed, v => { S.Speed = v; Commit(); })),
            Row("Reopen on the last tab", "Otherwise it always opens on Home", Switch(S.ReopenLastTab, v => { S.ReopenLastTab = v; Commit(); })),
            Row("Hide in fullscreen apps and games", null, Switch(S.HideInFullscreen, v => { S.HideInFullscreen = v; Commit(); })),
            Row("Hide from screenshots and recordings", null, Switch(S.HideFromCapture, v => { S.HideFromCapture = v; Commit(); }))
        );

        Section("Tabs");
        page.Children.Add(Spaced(Dim("Switch tabs on or off and use the arrows to put them in your order. Home is where the notch opens."), 0));
        page.Children.Add(Spaced(new Border(), 8));
        Card(OrderRows(S.NotchTabs, NotchCatalog.Tabs.Select(t => (t.id, t.name, (string?)t.glyph)).ToArray(), minimum: 1));

        Section("Home tab");
        page.Children.Add(Spaced(Dim("What Home shows. Turn off \"Now playing\" to give the rest the whole width."), 0));
        page.Children.Add(Spaced(new Border(), 8));
        Card(OrderRows(S.HomeItems, NotchCatalog.Home.Select(h => (h.id, h.name, (string?)null)).ToArray(), minimum: 0));

        Section("Clipboard");
        Card(
            Row("Remember after a restart", "Text and file copies are kept, encrypted so only your Windows account can read them. Pictures are never saved.",
                Switch(S.ClipRemember, v => { S.ClipRemember = v; Commit(); ClipboardHistory.Save(); })),
            Info("Pin and search", "Right-click an item in the Clipboard tab to pin it to the top; type in the box above the list to search")
        );

        Section("Keyboard");
        Card(
            Info("Ctrl + Space", "Search apps, games, settings and files"),
            Info("Ctrl + Alt + N", "Open or close the notch"),
            Info("Ctrl + Alt + V", "Paste as plain text (no fonts or colours)"),
            Info("Ctrl + Alt + 1 … 9", "Launch shortcut 1 to 9"),
            Info("Ctrl + Alt + P", "Play / pause"),
            Info("Ctrl + Alt + H", "Show or hide desktop icons"),
            Info("Ctrl + Alt + M", "Mute or unmute your microphone"),
            Info("Ctrl + Alt + C", "Copy the colour under the pointer")
        );
    }

    /// <summary>One row per item: a switch to show it, and arrows to move the shown ones up and down.</summary>
    UIElement[] OrderRows(List<string> enabled, (string id, string name, string? glyph)[] catalog, int minimum)
    {
        var rows = new List<UIElement>();
        var ordered = enabled.Where(id => catalog.Any(c => c.id == id))
                             .Concat(catalog.Select(c => c.id).Where(id => !enabled.Contains(id))).ToList();
        foreach (var id in ordered)
        {
            var item = catalog.First(c => c.id == id);
            bool on = enabled.Contains(id);
            int at = enabled.IndexOf(id);
            var g = new Grid { Margin = new Thickness(18, 9, 14, 9) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (item.glyph != null)
                g.Children.Add(new TextBlock { Text = item.glyph, FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 15, Foreground = on ? Brushes.White : Gray, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = item.name, FontSize = 13.5, Foreground = on ? Brushes.White : Gray, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, item.glyph != null ? 1 : 0);
            if (item.glyph == null) Grid.SetColumnSpan(label, 2);
            g.Children.Add(label);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (on)
            {
                var up = IconBtn("", "Move up", () => { (enabled[at - 1], enabled[at]) = (enabled[at], enabled[at - 1]); Commit(); Rebuild(); });
                up.IsEnabled = at > 0;
                up.Opacity = at > 0 ? 1 : 0.3;
                var down = IconBtn("", "Move down", () => { (enabled[at + 1], enabled[at]) = (enabled[at], enabled[at + 1]); Commit(); Rebuild(); });
                down.IsEnabled = at < enabled.Count - 1;
                down.Opacity = at < enabled.Count - 1 ? 1 : 0.3;
                actions.Children.Add(up);
                actions.Children.Add(down);
            }
            var sw = Switch(on, v =>
            {
                if (v) { if (!enabled.Contains(id)) enabled.Add(id); }
                else if (enabled.Count > minimum) enabled.Remove(id);
                Commit();
                Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Background);
            });
            sw.Margin = new Thickness(12, 0, 0, 0);
            if (on && enabled.Count <= minimum) { sw.IsEnabled = false; sw.ToolTip = "The notch needs at least one tab"; }
            actions.Children.Add(sw);
            Grid.SetColumn(actions, 2);
            g.Children.Add(actions);
            rows.Add(g);
        }
        return rows.ToArray();
    }

    void MessagesPage()
    {
        H1("Messages", "Notifications from Teams, Discord, Outlook and other apps, in the notch's Messages tab. Brim reads them from Windows' notification centre; nothing leaves this PC.");

        if (MessageCenter.Status is { } problem)
        {
            Section("Access");
            Card(Row("Windows isn't sharing notifications with Brim", problem, PillBtn("Open settings", () => Launcher.Open("ms-settings:privacy-notifications"))));
        }

        if (MessageCenter.HeldBecause() is { } held)
        {
            Section("Right now");
            Card(Row("Windows is holding app notifications", $"Because {held}. Discord, Chrome and others don't send notifications until it's closed, so nothing can show here either.",
                PillBtn("Check again", Rebuild)));
        }

        Section("Behaviour");
        Card(
            Row("Pop out for new messages", "The notch shows who wrote and the first line, for messages you'd see in the tab",
                Switch(S.MessagePopups, v => { S.MessagePopups = v; Commit(); WindowsBanners.Sync(); Rebuild(); })),
            S.MessagePopups ? Row("Only pop up in the notch", "Turns off Windows' own corner pop-up for the apps shown here, so you don't get both. They still reach Windows' notification centre. Windows' pop-ups come back if you turn this off or quit Brim.",
                Switch(S.NotchOnlyPopups, v => { S.NotchOnlyPopups = v; Commit(); WindowsBanners.Sync(); })) : null,
            Row("Mirror every app's notifications", "Show all of Windows' notifications in the notch, not only chat and mail apps. Each app can still be turned off below.",
                Switch(S.MessagesAllApps, v => { MessageCenter.SetAllApps(v); Rebuild(); })),
            Row("Messages tab", S.NotchTabs.Contains("Messages") ? "Shown in the notch" : "Hidden (turn it on in Notch › Tabs)",
                Switch(S.NotchTabs.Contains("Messages"), v =>
                {
                    if (v && !S.NotchTabs.Contains("Messages")) S.NotchTabs.Insert(Math.Min(3, S.NotchTabs.Count), "Messages");
                    if (!v) S.NotchTabs.Remove("Messages");
                    Commit(); Rebuild();
                })));

        MessageCenter.Discover();
        Section("Apps");
        page.Children.Add(Spaced(Dim("All shows everything from the app. Only these people shows messages whose sender matches a name you add (part of a name is enough). Apps appear here once they've sent a notification. Tip: right-click a message in the notch to follow or mute that person."), 0));
        page.Children.Add(Spaced(new Border(), 8));
        var apps = MessageCenter.KnownApps();
        if (apps.Count == 0)
        {
            Card(Pad(Dim("No notifications yet. When Teams, Discord, Outlook or any other app sends one, it shows up here.")));
            return;
        }
        foreach (var app in apps)
        {
            var rule = MessageCenter.Rule(app);
            var rows = new List<UIElement?>
            {
                Row(app, MessageCenter.Describe(app), Chips(new[] { "All", "Only these people", "Off" },
                    rule.Mode == "People" ? "Only these people" : rule.Mode, v =>
                    {
                        rule.Mode = v == "Only these people" ? "People" : v;
                        S.MessageApps[app] = rule;
                        Commit(); MessageCenter.Refilter(); Rebuild();
                    })),
            };
            if (rule.Mode == "People") rows.Add(NamesRow("People", "Add a name as it appears in the notification", rule.People, app));
            if (rule.Mode != "Off" && (rule.Muted.Count > 0 || rule.Mode == "All")) rows.Add(NamesRow("Muted", "Never show messages from these names", rule.Muted, app));
            Card(rows.ToArray());
            page.Children.Add(new Border { Height = 10 });
        }
    }

    // ------------------------------------------------------------------ uninstall

    List<InstalledProgram>? programs;
    InstalledProgram? removing;
    readonly HashSet<string> removeAlso = new();
    string uninstallQuery = "";
    string? uninstallStatus;
    bool uninstallBusy;
    System.Threading.CancellationTokenSource? uninstallStop;
    DateTime programsAt;

    /// <summary>Open the Uninstall page with this program ready to remove (from a game's right-click menu).</summary>
    public void ShowUninstall(InstalledProgram p)
    {
        StartRemoving(p);
        Show("Uninstall");
        Rebuild();
    }

    /// <summary>Open the Uninstall page searching for a name (a game Windows doesn't list as installed).</summary>
    public void ShowUninstallSearch(string query)
    {
        removing = null;
        uninstallQuery = query;
        Show("Uninstall");
        Rebuild();
    }

    void StartRemoving(InstalledProgram p)
    {
        removing = p;
        uninstallStatus = null;
        removeAlso.Clear();
        programs ??= Uninstaller.Everything();
        // Tick what clearly came with it and nothing else needs (e.g. Ubisoft Connect with your only Ubisoft game)
        foreach (var r in Uninstaller.Related(p, programs).Where(Uninstaller.Recommended)) removeAlso.Add(r.Program.Key);
    }

    void UninstallPage()
    {
        H1("Uninstall", "Remove apps and games, and what came with them: the launcher a game brought along, or the helper tools an app installed. Shared pieces that other programs need (DirectX, Visual C++, .NET, Vulkan, anti-cheat…) are never removed.");
        // Read fresh whenever the page is opened (new installs, removals elsewhere), not while a removal is being set up
        if (programs == null || removing == null && !uninstallBusy && (DateTime.Now - programsAt).TotalSeconds > 5) { programs = Uninstaller.Everything(); programsAt = DateTime.Now; }

        if (removing != null)
        {
            var target = removing;
            var related = Uninstaller.Related(target, programs);
            Section("Remove " + target.Name);
            var rows = new List<UIElement?>
            {
                Row(target.Name, target.Kind == "GameFolder"
                        ? $"Windows doesn't list this game, so its folder goes to the Recycle Bin (Windows asks first): {target.Location}"
                        : string.Join(" · ", new[] { target.KindText, target.Publisher, target.Version, target.SizeText }.Where(x => x.Length > 0)),
                    new TextBlock { Text = "Will be removed", Foreground = Gray, FontSize = 12 }),
            };
            foreach (var r in related)
            {
                var p = r.Program;
                bool safe = r.StillUsedBy == null;
                var desc = r.Reason + (safe ? "" : $". Still needed by {r.StillUsedBy}, so it's off unless you turn it on.");
                rows.Add(Row("Also remove " + p.Name, desc, Switch(removeAlso.Contains(p.Key), v => { if (v) removeAlso.Add(p.Key); else removeAlso.Remove(p.Key); Rebuild(); })));
            }
            if (related.Count == 0) rows.Add(Pad(Dim("Nothing else came with it, as far as Windows' list shows.")));
            var kept = programs.Where(p => Uninstaller.IsFramework(p) && p.Installed != null && target.Installed != null &&
                                           Math.Abs((p.Installed.Value - target.Installed.Value).TotalDays) <= 1).Select(p => p.Name).Distinct().Take(5).ToList();
            if (kept.Count > 0) rows.Add(Pad(Dim("Kept on purpose (shared by other programs): " + string.Join(", ", kept))));

            var buttons = new WrapPanel { Margin = new Thickness(18, 12, 18, 14) };
            if (uninstallBusy)
            {
                buttons.Children.Add(PillBtn("Stop after this one", () => uninstallStop?.Cancel()));
            }
            else
            {
                var just = PillBtn("Remove just " + target.Name, () => RunUninstall(new[] { target }));
                buttons.Children.Add(just);
                var picked = related.Where(r => removeAlso.Contains(r.Program.Key)).Select(r => r.Program).ToList();
                if (picked.Count > 0)
                {
                    var all = PillBtn($"Remove {target.Name} and {picked.Count} more", () => RunUninstall(new[] { target }.Concat(picked).ToArray()));
                    all.SetResourceReference(BackgroundProperty, "Brim.Accent");
                    all.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
                    buttons.Children.Add(all);
                }
                buttons.Children.Add(PillBtn("Cancel", () => { removing = null; uninstallStatus = null; Rebuild(); }));
            }
            rows.Add(buttons);
            if (uninstallStatus != null) rows.Add(Pad(new TextBlock { Text = uninstallStatus, FontSize = 12.5, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap }));
            Card(rows.ToArray());
        }

        Section("Installed");
        var search = new TextBox { Width = 300, Text = uninstallQuery, HorizontalAlignment = HorizontalAlignment.Left };
        search.SetResourceReference(StyleProperty, "Field");
        var list = new StackPanel();
        void Fill()
        {
            list.Children.Clear();
            var q = uninstallQuery.Trim();
            var shown = programs.Where(p => !Uninstaller.IsFramework(p) &&
                                            (q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)))
                                .Take(80).ToList();
            bool first = true;
            foreach (var p in shown)
            {
                if (!first) { var line = new Border { Height = 1 }; line.SetResourceReference(Border.BackgroundProperty, "Set.Line"); list.Children.Add(line); }
                first = false;
                var prog = p;
                var info = string.Join(" · ", new[] { p.KindText, p.Publisher, p.SizeText, p.Installed?.ToString("d MMM yyyy") ?? "" }.Where(x => x.Length > 0));
                var btn = PillBtn("Uninstall…", () => { StartRemoving(prog); Rebuild(); scroller.ScrollToTop(); });
                list.Children.Add(Row(p.Name, info, btn));
            }
            if (shown.Count == 0) list.Children.Add(Pad(Dim("Nothing matches.")));
        }
        var hint = new TextBlock { Text = "Search apps and games", FontSize = 13, Foreground = Gray, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        hint.Visibility = uninstallQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        search.TextChanged += (_, _) => { uninstallQuery = search.Text; hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); };
        Fill();
        var searchRow = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        searchRow.Children.Add(search);
        searchRow.Children.Add(hint);
        page.Children.Add(searchRow);
        page.Children.Add(new Border { Height = 10 });
        var card = new Border { CornerRadius = new CornerRadius(12), Child = list };
        card.SetResourceReference(Border.BackgroundProperty, "Set.Card");
        page.Children.Add(card);
        page.Children.Add(Spaced(Dim("Frameworks and drivers aren't listed here; remove those from Windows' Settings › Apps if you really mean to."), 10));
    }

    async void RunUninstall(InstalledProgram[] list)
    {
        var names = string.Join("\n• ", list.Select(p => p.Name));
        if (MessageBox.Show(this, $"Remove these from this PC?\n\n• {names}\n\nEach one's own uninstaller will run, one after another.", "Uninstall",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        uninstallBusy = true;
        uninstallStop = new System.Threading.CancellationTokenSource();
        uninstallStatus = "Starting…";
        Rebuild();
        var results = await Uninstaller.Run(list, new Progress<string>(s => { uninstallStatus = s; Rebuild(); }), uninstallStop.Token);
        uninstallBusy = false;
        uninstallStatus = string.Join("\n", results.Select(r => (r.removed ? "✓ " : "• ") + r.p.Name + ": " + r.note));
        programs = Uninstaller.Everything();
        if (results.All(r => r.removed)) removing = null;
        _ = GameLibrary.Refresh();
        Rebuild();
        if (removing == null && uninstallStatus != null)
            page.Children.Insert(2, Spaced(new TextBlock { Text = uninstallStatus, FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap }, 14));
    }

    /// <summary>A list of names as removable chips, with a box to add one.</summary>
    FrameworkElement NamesRow(string label, string desc, List<string> names, string app)
    {
        var stack = new StackPanel { Margin = new Thickness(18, 10, 18, 12) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Brushes.White });
        stack.Children.Add(new TextBlock { Text = desc, FontSize = 11.5, Foreground = Gray, Margin = new Thickness(0, 2, 0, 8) });
        var wrap = new WrapPanel();
        foreach (var n in names.ToList())
        {
            var name = n;
            var chip = PillBtn(name + "   ✕", () => { names.Remove(name); S.MessageApps[app] = MessageCenter.Rule(app); Commit(); MessageCenter.Refilter(); Rebuild(); });
            chip.ToolTip = "Remove " + name;
            chip.Margin = new Thickness(0, 0, 6, 6);
            wrap.Children.Add(chip);
        }
        var box = new TextBox { Width = 180, Margin = new Thickness(0, 0, 6, 6) };
        box.SetResourceReference(StyleProperty, "Field");
        void Add()
        {
            var t = box.Text.Trim();
            if (t.Length == 0 || names.Contains(t, StringComparer.OrdinalIgnoreCase)) return;
            names.Add(t);
            S.MessageApps[app] = MessageCenter.Rule(app);
            Commit(); MessageCenter.Refilter(); Rebuild();
        }
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Add(); };
        wrap.Children.Add(box);
        var add = PillBtn("Add", Add);
        add.Margin = new Thickness(0, 0, 6, 6);
        wrap.Children.Add(add);
        // Suggest the senders seen recently in this app
        foreach (var who in MessageCenter.RecentSenders(app).Where(w => !names.Contains(w, StringComparer.OrdinalIgnoreCase)).Take(6))
        {
            var w = who;
            var sug = PillBtn("+ " + w, () => { names.Add(w); S.MessageApps[app] = MessageCenter.Rule(app); Commit(); MessageCenter.Refilter(); Rebuild(); });
            sug.Opacity = 0.7;
            sug.Margin = new Thickness(0, 0, 6, 6);
            sug.ToolTip = "Seen recently";
            wrap.Children.Add(sug);
        }
        stack.Children.Add(wrap);
        return stack;
    }

    void LookPage()
    {
        H1("Look", "Colours for the notch, dock and widgets.");

        Section("Theme");
        var themes = new WrapPanel();
        foreach (var p in ThemeManager.Palettes)
        {
            var name = p.Name;
            var accent = p.DefaultAccent;
            themes.Children.Add(ThemeCard(p, S.Theme == p.Name, () =>
            {
                S.Theme = name;
                // With day/night switching on, picking a theme sets it for the part of the day you're in
                if (S.AutoTheme) { if (AutoLook.IsDaytime()) S.DayTheme = name; else S.NightTheme = name; }
                if (p.OwnAccent && !S.AccentFromWallpaper) S.Accent = accent;
                Commit();
                Rebuild();
            }));
        }
        var names = ThemeManager.Palettes.Select(p => p.Name).ToArray();
        var sun = WeatherService.Current?.Sunrise != null
            ? $"Day theme from sunrise ({WeatherService.Current.Sunrise:HH:mm}) to sunset ({WeatherService.Current.Sunset:HH:mm}) in {S.WeatherCity}"
            : "Day theme from 07:00 to 19:00. Set your city in Widgets › Weather to follow real sunrise and sunset.";
        Card(Pad(themes),
            Row("Change with the time of day", sun, Switch(S.AutoTheme, v => { S.AutoTheme = v; Commit(); AutoLook.Check(); Rebuild(); })),
            S.AutoTheme ? Row("Day theme", null, Dropdown(names, S.DayTheme, v => { S.DayTheme = v; Commit(); AutoLook.Check(); Rebuild(); })) : null,
            S.AutoTheme ? Row("Night theme", null, Dropdown(names, S.NightTheme, v => { S.NightTheme = v; Commit(); AutoLook.Check(); Rebuild(); })) : null);

        Section("Accent");
        var accents = new WrapPanel();
        foreach (var a in ThemeManager.Accents)
        {
            var c = ThemeManager.Parse(a);
            bool sel = string.Equals(S.Accent, a, StringComparison.OrdinalIgnoreCase);
            var b = new Button { Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand };
            b.Template = SwatchTemplate(c, sel);
            var hex = a;
            b.Click += (_, _) => { S.Accent = hex; S.AccentFromWallpaper = false; Commit(); Rebuild(); };
            if (S.AccentFromWallpaper) b.Opacity = sel ? 1 : 0.45;
            accents.Children.Add(b);
        }
        Card(
            Row("Match my wallpaper", "Picks the accent from your wallpaper, including Wallpaper Engine scenes and videos, and follows it when it changes",
                Switch(S.AccentFromWallpaper, v => { S.AccentFromWallpaper = v; Commit(); AutoLook.Check(force: true); Rebuild(); })),
            Pad(accents),
            Row("Album colours", "Tint the notch with the colours of the cover that's playing", Switch(S.AlbumColors, v => { S.AlbumColors = v; Commit(); })));

        Section("Widgets");
        Card(Row("Default widget look", "Glass and Frost blur the wallpaper behind them. Each widget can also have its own look (Widgets page).",
            Chips(WidgetStyles.Looks.Select(l => l.Name).ToArray(), S.WidgetStyle, v => { S.WidgetStyle = v; Commit(); })));
    }

    void ShortcutsPage()
    {
        H1("Shortcuts", "Apps, folders and websites tucked into the notch. Up to 12; the first 9 also get Ctrl+Alt+number.");

        Section("In the notch");
        var rows = new List<UIElement?>();
        for (int i = 0; i < S.Shortcuts.Count; i++)
        {
            var item = S.Shortcuts[i];
            int index = i;
            rows.Add(ListRow(item, i < 9 ? $"Ctrl+Alt+{i + 1}" : null,
                index > 0 ? () => { S.Shortcuts.RemoveAt(index); S.Shortcuts.Insert(index - 1, item); Commit(); Rebuild(); } : null,
                () => { S.Shortcuts.RemoveAt(index); Commit(); Rebuild(); }));
        }
        if (rows.Count == 0) rows.Add(Pad(Dim("No shortcuts yet.")));
        Card(rows.ToArray());

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var addApps = PillBtn("Add apps, files or folders…", () =>
        {
            var picked = AppPicker.Pick(this, "Add shortcuts", "Add", S.Shortcuts.Select(s => s.Path), 12 - S.Shortcuts.Count);
            if (picked.Count == 0) return;
            foreach (var p in picked) if (S.Shortcuts.Count < 12) S.Shortcuts.Add(p);
            Commit();
            Rebuild();
        });
        addApps.SetResourceReference(BackgroundProperty, "Brim.Accent");
        addApps.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
        addApps.IsEnabled = S.Shortcuts.Count < 12;
        buttons.Children.Add(addApps);
        page.Children.Add(buttons);

        Section("Add a website");
        var url = new TextBox { Width = 320, Text = "https://" };
        url.SetResourceReference(StyleProperty, "Field");
        var add = PillBtn("Add", () =>
        {
            var u = url.Text.Trim();
            if (!IconHelper.IsUrl(u) || u.Length < 10) return;
            AddShortcuts(new[] { u });
        });
        add.Margin = new Thickness(8, 0, 0, 0);
        var urlRow = new StackPanel { Orientation = Orientation.Horizontal };
        urlRow.Children.Add(url);
        urlRow.Children.Add(add);
        page.Children.Add(urlRow);
        page.Children.Add(Dim("Tip: you can also drop apps straight onto the Shortcuts tab of the notch, and right-click a tile to remove it."));
    }

    string? editingGame;

    void GamesPage()
    {
        H1("Games", "Everything Brim finds shows in the notch's Games tab, newest played first. New installs appear on their own.");
        if (!GameLibrary.Scanned)
        {
            page.Children.Add(Spaced(Dim("Looking for your games…"), 16));
            _ = RescanThenRebuild();
            return;
        }

        var tools = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        tools.Children.Add(PillBtn("Add a game…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the game's exe (or its launcher)", Filter = "Programs|*.exe;*.lnk;*.bat|All files|*.*", DereferenceLinks = false };
            if (dlg.ShowDialog(this) != true) return;
            S.ManualGames.Add(new ShortcutItem { Name = IconHelper.NiceName(dlg.FileName), Path = dlg.FileName });
            SettingsStore.Save();
            _ = RescanThenRebuild();
        }));
        tools.Children.Add(PillBtn("Scan again", () => { _ = RescanThenRebuild(); }));
        page.Children.Add(tools);

        Section($"Your games ({GameLibrary.Current.Count})");
        var rows = new List<UIElement?>();
        foreach (var g in GameLibrary.Current)
        {
            var game = g;
            bool open = editingGame == g.Key;
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(PillBtn(open ? "Done" : "Edit", () => { editingGame = open ? null : game.Key; Rebuild(); }));
            actions.Children.Add(Switch(!g.Hidden, v =>
            {
                GameLibrary.Override(game.Key).Hidden = !v;
                SettingsStore.Save();
                _ = GameLibrary.Refresh();
            }));
            var how = g.LaunchUri != null && !S.GameOverrides.Any(o => o.Key == g.Key && !string.IsNullOrWhiteSpace(o.Exe))
                ? $"{g.Source} · starts through {(g.Source == "Steam" ? "Steam" : "its launcher")}"
                : $"{g.Source} · {g.Exe}{(string.IsNullOrWhiteSpace(g.Args) ? "" : " " + g.Args)}";
            rows.Add(Row(g.Name, how, actions));
            if (!open) continue;

            var o = GameLibrary.Override(g.Key);
            var name = new TextBox { Text = o.Name ?? g.Name, Width = 360 };
            name.SetResourceReference(StyleProperty, "Field");
            var exe = new TextBox { Text = o.Exe ?? g.Exe ?? "", Width = 360 };
            exe.SetResourceReference(StyleProperty, "Field");
            var args = new TextBox { Text = o.Args ?? g.Args ?? "", Width = 360 };
            args.SetResourceReference(StyleProperty, "Field");
            var browse = PillBtn("Browse…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the exe or launcher to start", Filter = "Programs|*.exe;*.lnk;*.bat|All files|*.*", InitialDirectory = g.Folder ?? "" };
                if (dlg.ShowDialog(this) == true) exe.Text = dlg.FileName;
            });
            browse.Margin = new Thickness(8, 0, 0, 0);
            var editor = new StackPanel { Margin = new Thickness(18, 4, 18, 14) };
            editor.Children.Add(Mini("Name", name));
            editor.Children.Add(Mini("Start with", new StackPanel { Orientation = Orientation.Horizontal, Children = { exe, browse } }));
            editor.Children.Add(Mini("Launch options", args));
            if (g.Candidates.Count > 1)
                editor.Children.Add(Mini("Found in its folder", new TextBlock
                {
                    Text = string.Join("\n", g.Candidates.Select(System.IO.Path.GetFileName)),
                    Foreground = Gray, FontSize = 11.5,
                }));
            if (g.Source == "Steam") editor.Children.Add(Spaced(Dim("Steam games start through Steam by default (needed for Steam's DRM, overlay and cloud saves). Fill in “Start with” only if you really want to run an exe directly."), 4));
            var save = PillBtn("Save", () =>
            {
                o.Name = name.Text.Trim() == g.Name && o.Name == null ? null : name.Text.Trim();
                o.Exe = exe.Text.Trim().Length == 0 || (exe.Text.Trim() == g.Exe && o.Exe == null) ? null : exe.Text.Trim();
                o.Args = args.Text.Trim().Length == 0 && o.Args == null ? null : args.Text.Trim();
                SettingsStore.Save();
                editingGame = null;
                _ = RescanThenRebuild();
            });
            save.SetResourceReference(BackgroundProperty, "Brim.Accent");
            save.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
            var reset = PillBtn("Reset", () =>
            {
                S.GameOverrides.RemoveAll(x => x.Key == game.Key);
                SettingsStore.Save();
                editingGame = null;
                _ = RescanThenRebuild();
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(150, 8, 0, 0), Children = { save, reset } };
            if (g.Source == "Added by you")
                buttons.Children.Add(PillBtn("Remove", () =>
                {
                    S.ManualGames.RemoveAll(m => string.Equals(m.Path, game.Exe, StringComparison.OrdinalIgnoreCase));
                    SettingsStore.Save();
                    _ = RescanThenRebuild();
                }));
            editor.Children.Add(buttons);
            rows.Add(editor);
        }
        if (rows.Count == 0) rows.Add(Pad(Dim("No games found yet.")));
        Card(rows.ToArray());

        Section("Where Brim looks");
        var roots = GameLibrary.GameRoots();
        var folderRows = new List<UIElement?>
        {
            Pad(Dim("Found automatically: Steam (and non-Steam games added to it), Epic, GOG, Riot (League, VALORANT, TFT), EA app, Ubisoft Connect, Battle.net and Xbox PC games. Folders below are scanned too: every folder inside is treated as a game, and its launcher (if it has one) or main exe is used.")),
            Row("Look for games anywhere on my drives", "Like the NVIDIA app: checks Program Files and the top of every drive for folders with files only games ship with (game engines, Steam/Epic/GOG SDKs, DLSS/FSR/XeSS)",
                Switch(S.GameFingerprints, v => { S.GameFingerprints = v; SettingsStore.Save(); _ = RescanThenRebuild(); })),
        };
        foreach (var r in roots)
        {
            var root = r;
            folderRows.Add(Row(root, S.GameFolders.Count == 0 ? "Found automatically" : null,
                S.GameFolders.Count == 0 ? new TextBlock() : IconBtn("", "Stop scanning this folder", () =>
                {
                    S.GameFolders.RemoveAll(f => string.Equals(f, root, StringComparison.OrdinalIgnoreCase));
                    SettingsStore.Save();
                    GameLibrary.Rewatch();
                    Rebuild();
                })));
        }
        Card(folderRows.ToArray());
        var addFolder = PillBtn("Add a games folder…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a folder that contains your games" };
            if (dlg.ShowDialog(this) != true) return;
            if (S.GameFolders.Count == 0) S.GameFolders.AddRange(GameLibrary.GameRoots());
            if (!S.GameFolders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) S.GameFolders.Add(dlg.FolderName);
            SettingsStore.Save();
            GameLibrary.Rewatch();
            _ = RescanThenRebuild();
        });
        addFolder.HorizontalAlignment = HorizontalAlignment.Left;
        addFolder.Margin = new Thickness(0, 12, 0, 0);
        page.Children.Add(addFolder);

        Section("Order");
        Card(Row("Order in the Games tab", S.GameOrder.Count > 0 ? "Your own order (drag covers in the notch to change it)" : "Most recently played first. Drag covers in the notch to set your own order.",
            PillBtn("Back to recently played", () => { GameLibrary.ResetOrder(); Rebuild(); })));

        Section("Cover art");
        var gridKey = new TextBox { Width = 320, Text = S.SteamGridKey };
        gridKey.SetResourceReference(StyleProperty, "Field");
        var saveKey = PillBtn("Save", () =>
        {
            S.SteamGridKey = gridKey.Text.Trim();
            SettingsStore.Save();
            GameLibrary.RetryCovers();
        });
        saveKey.Margin = new Thickness(8, 0, 0, 0);
        Card(
            Row("Get covers for games outside Steam", "Looks the game's name up in the public Steam store to borrow its cover art",
                Switch(S.GameCovers, v => { S.GameCovers = v; SettingsStore.Save(); })),
            ApiKeys.SteamGridBuiltIn && string.IsNullOrWhiteSpace(S.SteamGridKey)
            ? Pad(Dim("Games that aren't on Steam (League of Legends, Bloodborne…) get community covers from SteamGridDB automatically."))
            : Pad(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "SteamGridDB key (optional)", FontSize = 13.5, Foreground = Brushes.White },
                    Dim("For games that aren't on Steam, like League of Legends or Bloodborne. SteamGridDB has community covers for almost every game; make a free account at steamgriddb.com, then Preferences › API and paste the key here."),
                    Spaced(new StackPanel { Orientation = Orientation.Horizontal, Children = { gridKey, saveKey } }, 10),
                    Spaced(PillBtn("Open steamgriddb.com", () => Launcher.Open("https://www.steamgriddb.com/profile/preferences/api")), 8),
                },
            }),
            Pad(Dim("Or right-click any game in the notch › Choose cover image… to use a picture of your own."))
        );
    }

    async System.Threading.Tasks.Task RescanThenRebuild()
    {
        await GameLibrary.Refresh();
        if (current == "Games") Rebuild();
    }

    void AddShortcuts(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (S.Shortcuts.Count >= 12) break;
            S.Shortcuts.Add(new ShortcutItem { Name = IconHelper.NiceName(p), Path = p });
        }
        Commit();
        Rebuild();
    }

    void WidgetsPage()
    {
        H1("Widgets", "Calm cards that sit on your wallpaper, underneath every window. Drag to move. Right-click one to customise it or combine it with another.");

        Section("Add a widget");
        var add = new WrapPanel();
        foreach (var (kind, name, _) in WidgetFactory.Catalog)
        {
            var k = kind;
            var b = PillBtn("+  " + name, () =>
            {
                var item = new WidgetItem { Kind = k };
                S.Widgets.Add(item);
                expandedWidget = item.Id;
                Commit();
                Rebuild();
            });
            b.Margin = new Thickness(0, 0, 8, 8);
            add.Children.Add(b);
        }
        page.Children.Add(add);

        Section("Combine widgets into one card");
        var picks = new WrapPanel();
        var chosen = new List<WidgetKind>();
        foreach (var (kind, name, _) in WidgetFactory.Catalog)
        {
            var k = kind;
            var cb = new CheckBox { Content = name };
            cb.SetResourceReference(StyleProperty, "ChipToggle");
            cb.Checked += (_, _) => chosen.Add(k);
            cb.Unchecked += (_, _) => chosen.Remove(k);
            picks.Children.Add(cb);
        }
        var make = PillBtn("Create combined widget", () =>
        {
            if (chosen.Count < 2) return;
            var item = new WidgetItem { Kind = chosen[0], Parts = chosen.ToList() };
            S.Widgets.Add(item);
            expandedWidget = item.Id;
            Commit();
            Rebuild();
        });
        make.SetResourceReference(BackgroundProperty, "Brim.Accent");
        make.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
        make.HorizontalAlignment = HorizontalAlignment.Left;
        Card(Pad(new StackPanel { Children = { Dim("Pick two or more, in the order you want them:"), Spaced(picks, 10), Spaced(make, 6) } }));

        Section("Design your own");
        var presets = new WrapPanel();
        foreach (var (name, make2) in TextTokens.Presets)
        {
            var b = PillBtn("+  " + name, () =>
            {
                var item = new WidgetItem { Kind = WidgetKind.Custom, Lines = make2(), Align = name == "Terminal" ? "Left" : "Center" };
                S.Widgets.Add(item);
                expandedWidget = item.Id;
                Commit();
                Rebuild();
            });
            b.Margin = new Thickness(0, 0, 8, 8);
            presets.Children.Add(b);
        }
        Card(Pad(new StackPanel
        {
            Children =
            {
                Dim("Text widgets like the Headline, made of lines you build: pick live info like the weekday, time, song or weather from a menu and add your own words around it. Start from one of these and change everything:"),
                Spaced(presets, 10),
            },
        }));

        Section("On your desktop");
        if (S.Widgets.Count == 0) Card(Pad(Dim("No widgets yet. Add one above.")));
        foreach (var item in S.Widgets.ToList()) WidgetCard(item);

        Section("Options");
        Card(
            Row("Default look", "Used by widgets that haven't been given their own",
                Chips(WidgetStyles.Looks.Select(l => l.Name).ToArray(), S.WidgetStyle, v => { S.WidgetStyle = v; Commit(); })),
            Row("Lock in place", "Stops accidental dragging", Switch(S.WidgetsLocked, v => { S.WidgetsLocked = v; Commit(); })),
            Row("24-hour clock", null, Switch(S.Clock24, v => { S.Clock24 = v; Commit(); })),
            Row("Put widgets back in a tidy column", null, PillBtn("Reset positions", () => AppRef.ResetWidgetPositions()))
        );

        Section("Weather");
        var city = new TextBox { Width = 240, Text = S.WeatherCity };
        city.SetResourceReference(StyleProperty, "Field");
        var status = Dim(S.WeatherLat != null ? $"Using {S.WeatherCity}" : "Weather comes from Open-Meteo and is only fetched once you set a city.");
        status.Margin = new Thickness(0, 8, 0, 0);
        var find = PillBtn("Set city", async () =>
        {
            var q = city.Text.Trim();
            if (q.Length == 0)
            {
                S.WeatherCity = ""; S.WeatherLat = null; S.WeatherLon = null;
                Commit(); WeatherService.Refresh(true);
                status.Text = "Weather is off.";
                return;
            }
            status.Text = "Looking it up…";
            var r = await WeatherService.Geocode(q);
            if (r == null) { status.Text = "Couldn't find that place. Try “City, Country”."; return; }
            S.WeatherCity = r.Value.name;
            S.WeatherLat = r.Value.lat;
            S.WeatherLon = r.Value.lon;
            Commit();
            WeatherService.Refresh(true);
            status.Text = $"Using {r.Value.name}";
            city.Text = r.Value.name;
        });
        find.Margin = new Thickness(8, 0, 0, 0);
        var cityRow = new StackPanel { Orientation = Orientation.Horizontal };
        cityRow.Children.Add(city);
        cityRow.Children.Add(find);
        Card(Pad(new StackPanel { Children = { cityRow, status } }),
            Row("Fahrenheit", null, Switch(S.Fahrenheit, v => { S.Fahrenheit = v; Commit(); WeatherService.Refresh(true); })));
    }

    string? expandedWidget;

    /// <summary>World clock: the cities it shows (up to four), each removable, and a box to add another.</summary>
    void CityRows(WidgetItem item, List<UIElement?> rows)
    {
        item.Cities ??= new List<CityItem>();
        foreach (var c in item.Cities.ToList())
        {
            var city = c;
            string zone = city.TimeZone.Length > 0 ? city.TimeZone.Replace('_', ' ') : "time zone from the weather service";
            rows.Add(Row(city.Name, zone, IconBtn("", "Remove", () => { item.Cities.Remove(city); Commit(); Rebuild(); })));
        }
        if (item.Cities.Count >= 4) { rows.Add(Pad(Dim("Four cities is the most that fits."))); return; }

        var box = new TextBox { Width = 220 };
        box.SetResourceReference(StyleProperty, "Field");
        var status = Dim(item.Cities.Count == 0 ? "Type a city, like “London” or “Tokyo, Japan”." : "Add another city:");
        async void Add()
        {
            var q = box.Text.Trim();
            if (q.Length == 0) return;
            status.Text = "Looking it up…";
            var found = await WeatherService.FindCity(q);
            if (found == null) { status.Text = "Couldn't find that place. Try “City, Country”."; return; }
            item.Cities!.Add(found);
            Commit();
            Rebuild();
        }
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Add(); };
        var add = PillBtn("Add city", Add);
        add.Margin = new Thickness(8, 0, 0, 0);
        add.SetResourceReference(BackgroundProperty, "Brim.Accent");
        add.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        line.Children.Add(box);
        line.Children.Add(add);
        // Quick picks: your own city first (if set), so "here + one more" is a click away
        var quick = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var suggestions = new List<string>();
        if (S.WeatherCity.Length > 0) suggestions.Add(S.WeatherCity);
        suggestions.AddRange(new[] { "London", "New York", "Dubai", "Tokyo", "Los Angeles", "Paris" });
        foreach (var s in suggestions.Distinct().Where(s => !item.Cities.Any(c => c.Name.StartsWith(s.Split(',')[0], StringComparison.OrdinalIgnoreCase))).Take(6))
        {
            var name = s;
            var chip = PillBtn("+ " + name.Split(',')[0], () => { box.Text = name; Add(); });
            chip.Margin = new Thickness(0, 0, 6, 6);
            quick.Children.Add(chip);
        }
        rows.Add(Pad(new StackPanel { Children = { status, line, quick } }));
    }

    internal void ScrollTo(double y) { scroller.UpdateLayout(); scroller.ScrollToVerticalOffset(y); }

    public void ShowWidget(string id)
    {
        expandedWidget = id;
        Show("Widgets");
        Rebuild();
    }

    /// <summary>One widget on the desktop: its name, and when expanded every way to restyle it.</summary>
    void WidgetCard(WidgetItem item)
    {
        bool open = expandedWidget == item.Id;
        var name = WidgetStyles.NameOf(item);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(PillBtn(open ? "Done" : "Customise", () => { expandedWidget = open ? null : item.Id; Rebuild(); }));
        buttons.Children.Add(IconBtn("", "Remove", () => { S.Widgets.Remove(item); Commit(); Rebuild(); }));
        var rows = new List<UIElement?> { Row(name, item.AllKinds.Count > 1 ? "Combined widget" : null, buttons) };

        // Built-in text widgets can become designed ones (same look) so their lines can be edited
        if (open && item.AllKinds.Count == 1 && item.Kind is WidgetKind.Headline or WidgetKind.Day or WidgetKind.Clock)
        {
            var convert = PillBtn("Edit lines", () =>
            {
                item.Lines = item.Kind switch
                {
                    WidgetKind.Headline => TextTokens.Presets["Headline"](),
                    WidgetKind.Clock => TextTokens.Presets["Big clock"](),
                    _ => new List<TextLine>
                    {
                        new() { Text = "{WEEKDAY}", Size = 76, Weight = "Light", Spacing = "Wide" },
                        new() { Text = "{day} {month} {year}  ·  {time} {ampm}", Size = 16, Opacity = 0.8 },
                    },
                };
                item.Kind = WidgetKind.Custom;
                Commit();
                Rebuild();
            });
            convert.SetResourceReference(BackgroundProperty, "Brim.Accent");
            convert.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
            rows.Add(Row("Change what it says", "Opens the line editor: add, remove and restyle each line", convert));
        }
        if (open && item.Kind == WidgetKind.Custom && item.AllKinds.Count == 1) DesignerRows(item, rows);
        if (open && item.AllKinds.Contains(WidgetKind.WorldClock)) CityRows(item, rows);
        if (open)
        {
            void Set(Action change) { change(); Commit(); Rebuild(); }

            rows.Add(Row("Look", item.Look == null ? $"Default ({S.WidgetStyle})" : null,
                Chips(WidgetStyles.Looks.Select(l => l.Name).Prepend("Default").ToArray(), item.Look ?? "Default",
                    v => Set(() => item.Look = v == "Default" ? null : v))));
            rows.Add(Row("Font", null,
                Chips(WidgetStyles.Fonts.Select(f => f.name).ToArray(), item.Font, v => Set(() => item.Font = v))));
            rows.Add(Row("Text colour", null, ColourPicker(item.TextColor, v => Set(() => item.TextColor = v))));
            rows.Add(Row("Size", null,
                Chips(WidgetStyles.Sizes.Select(s => s.name).ToArray(),
                    WidgetStyles.Sizes.OrderBy(s => Math.Abs(s.scale - item.Scale)).First().name,
                    v => Set(() => item.Scale = WidgetStyles.Sizes.First(s => s.name == v).scale))));
            rows.Add(Row("Corners", null,
                Chips(WidgetStyles.Corners.Select(c => c.name).ToArray(),
                    WidgetStyles.Corners.OrderBy(c => Math.Abs(c.radius - item.Radius)).First().name,
                    v => Set(() => item.Radius = WidgetStyles.Corners.First(c => c.name == v).radius))));

            if (item.AllKinds.Count > 1)
            {
                rows.Add(Row("Arrange", null,
                    Chips(new[] { "Stacked", "Side by side" }, item.Layout == "Row" ? "Side by side" : "Stacked",
                        v => Set(() => item.Layout = v == "Side by side" ? "Row" : "Column"))));
                var partsPanel = new WrapPanel();
                var kinds = item.AllKinds.ToList();
                for (int i = 0; i < kinds.Count; i++)
                {
                    int idx = i;
                    var label = WidgetFactory.NameOf(kinds[i]);
                    var chip = PillBtn(label + "  ✕", () => Set(() =>
                    {
                        kinds.RemoveAt(idx);
                        item.Kind = kinds[0];
                        item.Parts = kinds.Count > 1 ? kinds : null;
                    }));
                    chip.Margin = new Thickness(0, 0, 6, 6);
                    chip.ToolTip = "Take this part out";
                    partsPanel.Children.Add(chip);
                }
                rows.Add(Row("Parts", "Click one to take it out", partsPanel));
            }

            var addPart = new WrapPanel();
            foreach (var (kind, label, _) in WidgetFactory.Catalog)
            {
                var k = kind;
                var chip = PillBtn("+ " + label, () => Set(() => { item.Parts = item.AllKinds.Append(k).ToList(); }));
                chip.Margin = new Thickness(0, 0, 6, 6);
                addPart.Children.Add(chip);
            }
            rows.Add(Row("Add to this card", null, addPart));
        }
        var spacer = new Border { Height = 10 };
        Card(rows.ToArray());
        page.Children.Add(spacer);
    }

    /// <summary>The line-by-line designer for a Custom text widget, with a live preview on your own wallpaper.</summary>
    void DesignerRows(WidgetItem item, List<UIElement?> rows)
    {
        item.Lines ??= new List<TextLine>();
        var preview = new CustomTextWidget(item) { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var previewHost = new ContentControl { Content = preview };
        previewHost.Resources["Brim.WidgetText"] = new SolidColorBrush(item.TextColor != null ? ThemeManager.Parse(item.TextColor) : Colors.White);
        previewHost.Resources["Brim.WidgetDim"] = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255));
        var font = WidgetStyles.Fonts.FirstOrDefault(f => f.name == item.Font).family ?? WidgetStyles.Fonts[0].family;
        previewHost.FontFamily = new FontFamily(font);
        previewHost.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.5 };
        var stage = new Border
        {
            Height = 230,
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = new Viewbox { Child = previewHost, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(16) },
            Background = new LinearGradientBrush(Color.FromRgb(40, 44, 60), Color.FromRgb(12, 12, 18), 90),
        };
        try
        {
            var wall = Wallpapers.Current();
            if (wall != null && System.IO.File.Exists(wall))
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 900;
                bmp.UriSource = new Uri(wall);
                bmp.EndInit();
                stage.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
        }
        catch { }
        rows.Add(new Border { Padding = new Thickness(18, 4, 18, 14), Child = stage });

        void Live() { preview.Build(); Commit(false); }
        void Set(Action change) { change(); Commit(); Rebuild(); }

        rows.Add(Row("Line up", null, Chips(new[] { "Left", "Center", "Right" }, item.Align, v => Set(() => item.Align = v))));

        for (int i = 0; i < item.Lines.Count; i++)
        {
            var line = item.Lines[i];
            int idx = i;
            var box = new StackPanel { Margin = new Thickness(18, 10, 18, 12) };

            var head = new Grid();
            head.Children.Add(new TextBlock { Text = $"Line {i + 1}", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
            var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            if (idx > 0) tools.Children.Add(IconBtn("", "Move up", () => Set(() => { item.Lines.RemoveAt(idx); item.Lines.Insert(idx - 1, line); })));
            tools.Children.Add(IconBtn("", "Remove line", () => Set(() => item.Lines.RemoveAt(idx))));
            head.Children.Add(tools);
            box.Children.Add(head);

            box.Children.Add(LinePieces(line, item, Live, Set));

            var fonts = WidgetStyles.Fonts.Select(f => f.name).ToList();
            bool customFont = !fonts.Contains(line.Font);
            box.Children.Add(Mini("Font", Chips(fonts.ToArray(), customFont ? "" : line.Font, v => Set(() => line.Font = v))));
            var other = new TextBox { Width = 220, Text = customFont ? line.Font : "", ToolTip = "Any font installed on this PC, e.g. Impact or Garamond" };
            other.SetResourceReference(StyleProperty, "Field");
            other.LostKeyboardFocus += (_, _) => { if (other.Text.Trim().Length > 0 && other.Text.Trim() != line.Font) Set(() => line.Font = other.Text.Trim()); };
            box.Children.Add(Mini("Other font", other));

            var size = new Slider { Minimum = 8, Maximum = 160, Value = line.Size, Width = 260 };
            size.SetResourceReference(StyleProperty, "BrimSlider");
            var sizeLabel = new TextBlock { Text = $"{line.Size:0}", Foreground = Gray, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            size.ValueChanged += (_, e) => { line.Size = Math.Round(e.NewValue); sizeLabel.Text = $"{line.Size:0}"; Live(); };
            box.Children.Add(Mini("Size", new StackPanel { Orientation = Orientation.Horizontal, Children = { size, sizeLabel } }));

            box.Children.Add(Mini("Weight", Chips(new[] { "Thin", "Light", "Regular", "SemiBold", "Bold" }, line.Weight, v => Set(() => line.Weight = v))));
            box.Children.Add(Mini("Letters", Chips(new[] { "None", "Wide", "Wider" }, line.Spacing, v => Set(() => line.Spacing = v))));
            box.Children.Add(Mini("Case", Chips(new[] { "As typed", "UPPER", "lower" }, line.Case, v => Set(() => line.Case = v))));
            box.Children.Add(Mini("Colour", ColourPicker(line.Color, v => Set(() => line.Color = v))));

            var opacity = new Slider { Minimum = 0.1, Maximum = 1, Value = line.Opacity, Width = 260 };
            opacity.SetResourceReference(StyleProperty, "BrimSlider");
            opacity.ValueChanged += (_, e) => { line.Opacity = Math.Round(e.NewValue, 2); Live(); };
            box.Children.Add(Mini("Opacity", opacity));
            box.Children.Add(Mini("Only while music plays", Switch(line.MusicOnly, v => Set(() => line.MusicOnly = v))));
            rows.Add(box);
        }

        Button? addLine = null;
        addLine = PillBtn("+ Add line  ▾", () => AddLineMenu(addLine!, line => Set(() => item.Lines.Add(line))));
        addLine.HorizontalAlignment = HorizontalAlignment.Left;
        rows.Add(new Border { Padding = new Thickness(18, 10, 18, 10), Child = addLine });
        rows.Add(Pad(new TextBlock
        {
            Text = "Tokens: " + string.Join("   ", TextTokens.Help.Select(t => $"{t.token} {t.meaning}")) + "\nWrite a token in CAPITALS (like {WEEKDAY}) for capital letters.",
            FontSize = 11.5, Foreground = Gray, TextWrapping = TextWrapping.Wrap,
        }));
    }

    // ---------- building a line from friendly pieces instead of {tokens} ----------

    static readonly Dictionary<string, string> TokenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weekday"] = "Weekday", ["wd"] = "Weekday (short)", ["day"] = "Day", ["month"] = "Month", ["mon"] = "Month (short)",
        ["monthnum"] = "Month number", ["year"] = "Year", ["date"] = "Date", ["time"] = "Time", ["hour"] = "Hour",
        ["minute"] = "Minutes", ["second"] = "Seconds", ["ampm"] = "AM/PM", ["greeting"] = "Greeting",
        ["song"] = "Song", ["artist"] = "Artist", ["player"] = "Music app",
        ["temp"] = "Temperature", ["weather"] = "Sky", ["high"] = "High", ["low"] = "Low", ["city"] = "City",
        ["battery"] = "Battery", ["cpu"] = "CPU", ["ram"] = "Memory", ["uptime"] = "Uptime",
    };

    static readonly (string group, string[] tokens)[] InsertGroups =
    {
        ("Date & time", new[] { "weekday", "wd", "day", "month", "mon", "year", "time", "second", "ampm", "greeting" }),
        ("Music", new[] { "song", "artist", "player" }),
        ("Weather", new[] { "temp", "weather", "high", "low", "city" }),
        ("System", new[] { "battery", "cpu", "ram", "uptime" }),
    };

    static readonly (string name, string text)[] Symbols =
    {
        ("Space", " "), ("Dot  ·", " · "), ("Bar  |", " | "), ("Dash  —", " — "), ("Slash  /", " / "),
        ("Comma  ,", ", "), ("Music note  ♪", "♪ "), ("Star  ✦", " ✦ "),
    };

    readonly HashSet<TextLine> rawEditing = new();

    /// <summary>Friendly name of a piece like "Time"; null if the text isn't a known piece.</summary>
    static string? PieceName(string token, WidgetItem item) => TokenNames.TryGetValue(token, out var n) ? n : null;

    /// <summary>One line as a row of pieces: live info as labelled chips, your own words as small text boxes.</summary>
    FrameworkElement LinePieces(TextLine line, WidgetItem item, Action live, Action<Action> set)
    {
        var host = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        if (rawEditing.Contains(line))
        {
            var raw = new TextBox { Text = line.Text };
            raw.SetResourceReference(StyleProperty, "Field");
            raw.TextChanged += (_, _) => { line.Text = raw.Text; live(); };
            host.Children.Add(raw);
            host.Children.Add(LinkBtn("Back to the easy editor", () => set(() => rawEditing.Remove(line))));
            return host;
        }

        var pieces = System.Text.RegularExpressions.Regex.Split(line.Text, @"(\{\w+(?:@\d)?\})")
            .Where(p => p.Length > 0)
            .Select(p => p.StartsWith("{") && p.EndsWith("}") && PieceName(p[1..^1], item) != null ? (token: true, value: p[1..^1]) : (token: false, value: p))
            .ToList();
        void Save() => line.Text = string.Concat(pieces.Select(p => p.token ? "{" + p.value + "}" : p.value));

        var preview = new TextBlock { FontSize = 11.5, Foreground = Gray, Margin = new Thickness(2, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        void ShowPreview() => preview.Text = "Looks like:  " + TextTokens.Format(line, item.Cities);
        ShowPreview();
        var wrap = new WrapPanel();
        for (int i = 0; i < pieces.Count; i++)
        {
            int idx = i;
            var (isToken, value) = pieces[i];
            if (isToken)
            {
                var name = PieceName(value, item)!;
                bool caps = value.Length > 1 && value.ToUpperInvariant() == value;
                var chip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 4, 4), Margin = new Thickness(0, 0, 6, 6), VerticalAlignment = VerticalAlignment.Center };
                chip.SetResourceReference(Border.BackgroundProperty, "Brim.Accent");
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var label = new TextBlock { Text = caps ? name.ToUpperInvariant() : name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Shows: " + TextTokens.Expand("{" + value + "}", item.Cities) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Brim.OnAccent");
                row.Children.Add(label);
                var x = new Button { Content = "", Width = 20, Height = 20, FontSize = 8, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove" };
                x.SetResourceReference(StyleProperty, "IconButton");
                x.SetResourceReference(Control.ForegroundProperty, "Brim.OnAccent");
                x.Click += (_, _) => set(() => { pieces.RemoveAt(idx); Save(); });
                row.Children.Add(x);
                chip.Child = row;
                wrap.Children.Add(chip);
            }
            else
            {
                if (value.Trim().Length == 0)
                {
                    // A plain space: a small grey chip instead of an empty box
                    var gap = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 4, 4), Margin = new Thickness(0, 0, 6, 6), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
                    var gapRow = new StackPanel { Orientation = Orientation.Horizontal };
                    gapRow.Children.Add(new TextBlock { Text = value.Length > 1 ? "spaces" : "space", FontSize = 11.5, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center });
                    var gx = new Button { Content = "", Width = 20, Height = 20, FontSize = 8, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove", Foreground = Gray };
                    gx.SetResourceReference(StyleProperty, "IconButton");
                    gx.Click += (_, _) => set(() => { pieces.RemoveAt(idx); Save(); });
                    gapRow.Children.Add(gx);
                    gap.Child = gapRow;
                    wrap.Children.Add(gap);
                    continue;
                }
                var tb = new TextBox { Text = value, MinWidth = 34, Margin = new Thickness(0, 0, 6, 6), ToolTip = "Your own text (spaces count)", Padding = new Thickness(6, 3, 6, 3), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
                tb.SetResourceReference(StyleProperty, "Field");
                tb.TextChanged += (_, _) => { pieces[idx] = (false, tb.Text); Save(); live(); ShowPreview(); };
                wrap.Children.Add(tb);
            }
        }
        Button? insert = null;
        insert = PillBtn("+ Insert  ▾", () => InsertMenu(insert!, item, piece => set(() => { pieces.Add(piece); Save(); })));
        insert.Margin = new Thickness(0, 0, 6, 6);
        insert.SetResourceReference(BackgroundProperty, "Set.Card");
        wrap.Children.Add(insert);
        host.Children.Add(wrap);

        host.Children.Add(preview);
        host.Children.Add(LinkBtn("Edit as text", () => set(() => rawEditing.Add(line))));
        return host;
    }

    void InsertMenu(Button anchor, WidgetItem item, Action<(bool token, string value)> add)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Header(string text, bool first) => menu.Items.Add(new TextBlock
        {
            Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Gray,
            Margin = new Thickness(12, first ? 6 : 12, 12, 4),
        });
        MenuItem Choice(string name, string example, Action pick)
        {
            var header = new Grid { MinWidth = 320 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White });
            var ex = new TextBlock { Text = example, Foreground = Gray, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 };
            Grid.SetColumn(ex, 1);
            header.Children.Add(ex);
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => pick();
            return mi;
        }

        bool first = true;
        foreach (var (group, tokens) in InsertGroups)
        {
            Header(group, first);
            first = false;
            foreach (var t in tokens)
            {
                var token = t;
                var now = TextTokens.Expand("{" + token + "}");
                if (string.IsNullOrWhiteSpace(now)) now = group == "Music" ? "shows what's playing" : group == "Weather" ? "today's weather" : "";
                menu.Items.Add(Choice(TokenNames[token], now, () => add((true, token))));
            }
        }
        Header("Text and symbols", false);
        menu.Items.Add(Choice("Your own words", "type anything", () => add((false, " text "))));
        foreach (var (name, text) in Symbols)
        {
            var t = text;
            menu.Items.Add(Choice(name, "", () => add((false, t))));
        }
        menu.IsOpen = true;
    }

    static Button LinkBtn(string text, Action click)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), Cursor = System.Windows.Input.Cursors.Hand, FontSize = 11.5 };
        b.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        b.SetResourceReference(Control.ForegroundProperty, "Brim.Accent");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>What "+ Add line" offers: ready-made lines, grouped, each showing what it looks like right now.</summary>
    static readonly (string group, (string name, Func<TextLine> make)[] items)[] LineChoices =
    {
        ("Date & time", new (string, Func<TextLine>)[]
        {
            ("Weekday", () => new TextLine { Text = "{weekday}", Size = 28, Weight = "Light" }),
            ("Weekday in spaced capitals", () => new TextLine { Text = "|  {WEEKDAY}  |", Size = 36, Weight = "Light", Spacing = "Wide", Font = "Modern" }),
            ("Date", () => new TextLine { Text = "{day} {month} {year}", Size = 16 }),
            ("Short date", () => new TextLine { Text = "{day}  {MON}  {year}", Size = 15, Font = "Modern" }),
            ("Time", () => new TextLine { Text = "{time} {ampm}", Size = 56, Weight = "Light" }),
            ("Time with seconds", () => new TextLine { Text = "{time}:{second}", Size = 40, Weight = "Light" }),
            ("Greeting", () => new TextLine { Text = "{greeting}.", Size = 30, Weight = "Light" }),
        }),
        ("Music (only shows while something plays)", new (string, Func<TextLine>)[]
        {
            ("Song title", () => new TextLine { Text = "♪ {song}", Size = 18, Weight = "SemiBold", MusicOnly = true }),
            ("Artist", () => new TextLine { Text = "{artist}", Size = 14, Opacity = 0.8, MusicOnly = true }),
            ("Song and artist", () => new TextLine { Text = "{song}  —  {artist}", Size = 14, MusicOnly = true }),
            ("Music app", () => new TextLine { Text = "{player}", Size = 12, Opacity = 0.7, MusicOnly = true }),
        }),
        ("Weather", new (string, Func<TextLine>)[]
        {
            ("Temperature", () => new TextLine { Text = "{temp}", Size = 44, Weight = "Light" }),
            ("Temperature and sky", () => new TextLine { Text = "{temp}  {weather}", Size = 18 }),
            ("High and low", () => new TextLine { Text = "H {high}   L {low}", Size = 13, Opacity = 0.8 }),
            ("City", () => new TextLine { Text = "{city}", Size = 13, Opacity = 0.8 }),
        }),
        ("System", new (string, Func<TextLine>)[]
        {
            ("Battery", () => new TextLine { Text = "Battery {battery}", Size = 13 }),
            ("CPU and memory", () => new TextLine { Text = "CPU {cpu}  ·  RAM {ram}", Size = 13 }),
            ("Uptime", () => new TextLine { Text = "Up {uptime}", Size = 13, Opacity = 0.8 }),
        }),
        ("Text", new (string, Func<TextLine>)[]
        {
            ("Your own text", () => new TextLine { Text = "Your text here", Size = 18 }),
            ("Divider line", () => new TextLine { Text = "──────────", Size = 12, Opacity = 0.4 }),
        }),
    };

    internal ContextMenu AddLineMenu(Button anchor, Action<TextLine> add, bool open = true)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        bool first = true;
        foreach (var (group, items) in LineChoices)
        {
            menu.Items.Add(new TextBlock
            {
                Text = group.ToUpperInvariant(),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Gray,
                Margin = new Thickness(12, first ? 6 : 12, 12, 4),
            });
            first = false;
            foreach (var (name, make) in items)
            {
                var sample = make();
                var header = new Grid { MinWidth = 380 };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                header.ColumnDefinitions.Add(new ColumnDefinition());
                header.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
                // What it would show right now
                var example = TextTokens.Format(sample);
                bool playing = MediaService.Instance.IsPlaying && !string.IsNullOrEmpty(MediaService.Instance.Title);
                if (sample.MusicOnly && !playing) example = "shows what's playing";
                else if (System.Text.RegularExpressions.Regex.IsMatch(sample.Text, @"\{(temp|weather|high|low)\}") && WeatherService.Current == null)
                    example = S.WeatherLat == null ? "set your city in Weather first" : "today's weather";
                var preview = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(example) || example.Contains('{') ? "(when available)" : example,
                    Foreground = Gray, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260, VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(preview, 1);
                header.Children.Add(preview);
                var mi = new MenuItem { Header = header };
                var factory = make;
                mi.Click += (_, _) => add(factory());
                menu.Items.Add(mi);
            }
        }
        if (open) menu.IsOpen = true;
        return menu;
    }

    /// <summary>Compact label + control used inside the designer.</summary>
    static FrameworkElement Mini(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        if (control is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Left; fe.VerticalAlignment = VerticalAlignment.Center; }
        if (control is WrapPanel wp) wp.Margin = new Thickness(0, 0, 0, -6);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    FrameworkElement ColourPicker(string? current, Action<string?> set)
    {
        var wp = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var auto = PillBtn("Auto", () => set(null));
        if (current == null) { auto.SetResourceReference(BackgroundProperty, "Brim.Accent"); auto.SetResourceReference(ForegroundProperty, "Brim.OnAccent"); }
        auto.Margin = new Thickness(0, 0, 8, 0);
        wp.Children.Add(auto);
        foreach (var hex in WidgetStyles.TextColors)
        {
            var b = new Button { Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 0), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = hex };
            b.Template = SwatchTemplate(ThemeManager.Parse(hex), string.Equals(current, hex, StringComparison.OrdinalIgnoreCase));
            var h = hex;
            b.Click += (_, _) => set(h);
            wp.Children.Add(b);
        }
        return wp;
    }

    static string DockPresetName() => (S.DockIconPx, S.DockPadding) switch
    {
        (24, 4) => "Small", (30, 6) => "Medium", (36, 7) => "Large", (46, 9) => "Huge", _ => "",
    };

    void TaskbarPage()
    {
        H1("Taskbar", "Tidy up Windows' taskbar, or swap it for Brim's floating dock.");

        Section("Style");
        var label = S.Taskbar switch { TaskbarMode.AutoHide => "Auto-hide", TaskbarMode.Dock => "Brim dock", _ => "Windows taskbar" };
        Card(
            Row("Use", S.Taskbar switch
                {
                    TaskbarMode.Dock => "Windows' taskbar is hidden and a floating dock sits at the bottom centre.",
                    TaskbarMode.AutoHide => "Windows' taskbar slides away until you point at the bottom edge.",
                    _ => "Windows' own taskbar, with the clean-up options below.",
                },
                Chips(new[] { "Windows taskbar", "Auto-hide", "Brim dock" }, label, v =>
                {
                    S.Taskbar = v switch { "Auto-hide" => TaskbarMode.AutoHide, "Brim dock" => TaskbarMode.Dock, _ => TaskbarMode.Windows };
                    Commit();
                    Rebuild();
                }))
        );

        if (S.Taskbar == TaskbarMode.Dock)
        {
            Section("Dock placement");
            Card(
                Row("Position", "Along the bottom of the screen", Chips(new[] { "Left", "Center", "Right" }, S.DockPosition, v => { S.DockPosition = v; Commit(); })),
                ScreenRow("Screen", S.DockScreen, v => { S.DockScreen = v; Commit(); }),
                Row("Hide until I point at the bottom edge", null, Switch(S.DockAutoHide, v => { S.DockAutoHide = v; Commit(); Rebuild(); })),
                S.DockAutoHide ? null : Row("Keep windows above the dock", "Maximised windows stop where the dock begins", Switch(S.DockReserve, v => { S.DockReserve = v; Commit(); Rebuild(); })),
                S.DockAutoHide || !S.DockReserve ? null : Row("With a maximised window",
                    "Full-width bar: the dock stretches into a solid bar, so no wallpaper shows beside the window. Floating: it stays a pill with your wallpaper either side.",
                    Chips(new[] { "Full-width bar", "Floating" }, S.DockWhenMaximized == "Bar" ? "Full-width bar" : "Floating",
                        v => { S.DockWhenMaximized = v == "Full-width bar" ? "Bar" : "Floating"; Commit(); }))
            );

            Section("Dock size");
            Card(
                Row("Quick size", null, Chips(new[] { "Small", "Medium", "Large", "Huge" }, DockPresetName(), v =>
                {
                    (S.DockIconPx, S.DockPadding) = v switch { "Small" => (24, 4), "Large" => (36, 7), "Huge" => (46, 9), _ => (30, 6) };
                    Commit(); Rebuild();
                })),
                Row("Icon size", $"{S.DockIconPx} px", SliderCtl(18, 64, S.DockIconPx, v => { S.DockIconPx = (int)Math.Round(v); Commit(false); }, rebuildLabel: true)),
                Row("Height", $"Space above and below the icons: {S.DockPadding} px", SliderCtl(0, 20, S.DockPadding, v => { S.DockPadding = (int)Math.Round(v); Commit(false); }, rebuildLabel: true)),
                Row("Space between icons", $"{S.DockSpacing} px", SliderCtl(0, 16, S.DockSpacing, v => { S.DockSpacing = (int)Math.Round(v); Commit(false); }, rebuildLabel: true)),
                Row("Distance from the bottom", $"{S.DockGap} px", SliderCtl(0, 40, S.DockGap, v => { S.DockGap = (int)Math.Round(v); Commit(false); }, rebuildLabel: true)),
                Row("Roundness", $"{S.DockRadius} px", SliderCtl(0, 30, S.DockRadius, v => { S.DockRadius = (int)Math.Round(v); Commit(false); }, rebuildLabel: true))
            );

            Section("Dock look and extras");
            Card(
                Row("Surface", "Glass blurs what's behind the dock. Clear drops the bar so only the icons float.",
                    Chips(new[] { "Solid", "Glass", "Clear" }, S.DockSurface, v => { S.DockSurface = v; Commit(); })),
                Row("Show the clock", "Click it for notifications and the calendar", Switch(S.DockClock, v => { S.DockClock = v; Commit(); })),
                Row("Show the temperature", S.WeatherLat == null ? "Set your city in Widgets › Weather first" : $"Weather for {S.WeatherCity}", Switch(S.DockWeather, v => { S.DockWeather = v; Commit(); })),
                Row("Badges on app icons", "A red dot or count when an app has something for you (a notification, unread messages, or it asked for attention)",
                    Switch(S.DockBadges, v => { S.DockBadges = v; Commit(); })),
                Row("Task Manager button", "A button next to the tray that opens Task Manager", Switch(S.DockTaskManager, v => { S.DockTaskManager = v; Commit(); })),
                Info("Tray icons", "Click ⌃ on the dock to bring Windows' taskbar up for a moment")
            );

            Section("Pinned apps");
            var rows = new List<UIElement?>();
            for (int i = 0; i < S.DockPins.Count; i++)
            {
                var item = S.DockPins[i];
                int index = i;
                rows.Add(ListRow(item, null,
                    index > 0 ? () => { S.DockPins.RemoveAt(index); S.DockPins.Insert(index - 1, item); Commit(); Rebuild(); } : null,
                    () => { S.DockPins.RemoveAt(index); Commit(); Rebuild(); }));
            }
            if (rows.Count == 0) rows.Add(Pad(Dim("Nothing pinned. Right-click a running app on the dock and choose “Pin to dock”, or add one here.")));
            Card(rows.ToArray());
            var addPin = PillBtn("Pin apps…", () =>
            {
                var picked = AppPicker.Pick(this, "Pin apps to the dock", "Pin", S.DockPins.Select(p => p.Path));
                if (picked.Count == 0) return;
                S.DockPins.AddRange(picked);
                Commit();
                Rebuild();
            });
            addPin.SetResourceReference(BackgroundProperty, "Brim.Accent");
            addPin.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
            addPin.Margin = new Thickness(0, 12, 0, 0);
            addPin.HorizontalAlignment = HorizontalAlignment.Left;
            page.Children.Add(addPin);
        }
        else
        {
            Section("Clean up Windows' taskbar");
            var widgetsNote = Dim("");
            var notePad = Pad(widgetsNote);
            notePad.Visibility = Visibility.Collapsed;
            var search = TaskbarManager.SearchMode;
            Card(
                Row("Centre the icons", "Off puts Start and your apps on the left", Switch(TaskbarManager.CenterIcons, v => TaskbarManager.CenterIcons = v)),
                Row("Search", null, Chips(new[] { "Hidden", "Icon", "Box" }, search == 0 ? "Hidden" : search == 1 ? "Icon" : "Box",
                    v => TaskbarManager.SearchMode = v == "Hidden" ? 0 : v == "Icon" ? 1 : 2)),
                Row("Task View button", null, Switch(TaskbarManager.ShowTaskView, v => TaskbarManager.ShowTaskView = v)),
                Row("Widgets button", null, Switch(TaskbarManager.ShowWidgetsButton, v =>
                {
                    if (!TaskbarManager.SetWidgetsButton(v))
                    {
                        widgetsNote.Text = "Windows blocks apps from changing the Widgets button on this PC. Turn it off in Settings › Personalisation › Taskbar.";
                        notePad.Visibility = Visibility.Visible;
                    }
                }))
            );
            page.Children.Add(notePad);
            var open = PillBtn("More in Windows' taskbar settings…", () => Launcher.Open("ms-settings:taskbar"));
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Margin = new Thickness(0, 12, 0, 0);
            page.Children.Add(open);
        }
    }

    void DesktopPage()
    {
        H1("Desktop", "Make the desktop calm: hide the icon clutter and let the wallpaper breathe.");

        Section("Clean desktop");
        Card(
            Row("Hide desktop icons", "Your files stay where they are. Ctrl+Alt+H toggles this.", Switch(S.HideDesktopIcons, v => { S.HideDesktopIcons = v; Commit(); })),
            Row("Change wallpaper", null, PillBtn("Open wallpaper settings", () => Launcher.Open("ms-settings:personalization-background")))
        );

        Section("Wallpapers");
        var browse = PillBtn("Browse wallpapers", () => AppRef.OpenWallpapers());
        browse.SetResourceReference(BackgroundProperty, "Brim.Accent");
        browse.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
        Card(
            Row("Pictures and live wallpapers", "Browse thousands online. Only the one you pick is downloaded.", browse),
            S.LiveWallpaper != null
                ? Row("Live wallpaper", Scenes.DisplayName(S.LiveWallpaper), PillBtn("Stop", () => { AppRef.SetLiveWallpaper(null); Rebuild(); }))
                : null,
            Row("When live wallpapers play", "Also applies to Wallpaper Engine. “Only on the desktop” plays the moment you click the desktop on any screen, and pauses behind any app.",
                Chips(new[] { "Only on the desktop", "When not covered", "Always" },
                    S.LivePlayMode switch { "Uncovered" => "When not covered", "Always" => "Always", _ => "Only on the desktop" },
                    v =>
                    {
                        S.LivePlayMode = v switch { "When not covered" => "Uncovered", "Always" => "Always", _ => "Desktop" };
                        Commit();
                        if (S.LiveWallpaper != null) LiveWallpaperHost.Start(S.LiveWallpaper);
                        LivePolicy.ApplyToWallpaperEngine();
                    })),
            Row("Pause on battery", "Stops while your laptop is unplugged and starts again when you plug in",
                Switch(S.LivePauseOnBattery, v => { S.LivePauseOnBattery = v; Commit(); LivePolicy.ApplyToWallpaperEngine(); })),
            Row("Pause in Battery saver", "Stops while Windows' Battery saver (Energy saver) is on",
                Switch(S.LivePauseOnSaver, v => { S.LivePauseOnSaver = v; Commit(); LivePolicy.ApplyToWallpaperEngine(); }))
        );

        Section("OLED care");
        string Minutes(int m) => m == 0 ? "Off" : $"{m} min";
        int FromMinutes(string v) => v == "Off" ? 0 : int.Parse(v.Split(' ')[0]);
        Card(
            Row("Protect against burn-in", "For OLED screens: stops Brim's notch, dock and widgets sitting on the same pixels for hours",
                Switch(S.OledCare, v => { S.OledCare = v; Commit(); Rebuild(); })),
            S.OledCare ? Row("Pixel shift", "Every few minutes they drift a couple of pixels, too little to notice",
                Switch(S.OledShift, v => { S.OledShift = v; Commit(); })) : null,
            S.OledCare ? Row("Dim when I'm away", "Fade the notch, dock and widgets after this long without mouse or keyboard",
                Chips(new[] { "Off", "1 min", "2 min", "5 min", "10 min" }, Minutes(S.OledDimMinutes), v => { S.OledDimMinutes = FromMinutes(v); Commit(); })) : null,
            S.OledCare ? Row("Black screen when I'm away", "Turns every pixel off until you move the mouse or press a key. Never interrupts a playing video.",
                Chips(new[] { "Off", "5 min", "10 min", "15 min", "30 min" }, Minutes(S.OledBlackMinutes), v => { S.OledBlackMinutes = FromMinutes(v); Commit(); })) : null,
            S.OledCare ? Pad(Dim("Also kind to OLED: the Black theme (true black is switched off), the OLED wallpaper tab and OLED animated wallpapers, and hiding the dock until you point at it.")) : null
        );

        Section("One click");
        var preset = PillBtn("Make it clean", () =>
        {
            S.HideDesktopIcons = true;
            S.Taskbar = TaskbarMode.Dock;
            S.DockAutoHide = false;
            if (!S.Widgets.Any(w => w.Kind == WidgetKind.Day)) S.Widgets.Add(new WidgetItem { Kind = WidgetKind.Day });
            if (!S.Widgets.Any(w => w.Kind == WidgetKind.Music)) S.Widgets.Add(new WidgetItem { Kind = WidgetKind.Music });
            Commit();
            Rebuild();
        });
        preset.SetResourceReference(BackgroundProperty, "Brim.Accent");
        preset.SetResourceReference(ForegroundProperty, "Brim.OnAccent");
        Card(Pad(new StackPanel
        {
            Children =
            {
                Dim("Hides desktop icons, swaps the taskbar for the Brim dock, and adds the Day and Now playing widgets. Everything can be undone here."),
                Spaced(preset, 12),
            },
        }));
    }

    void GeneralPage()
    {
        H1("General", $"Brim {Installer.Version}");

        Section("Startup");
        Card(
            Row("Start Brim with Windows", null, Switch(S.StartWithWindows, v => { S.StartWithWindows = v; Commit(); })),
            Installer.RunningInstalled
                ? Row("Installed", "In Start and in Settings › Apps › Installed apps, where you can uninstall it", PillBtn("Uninstall…", () =>
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Installer.InstallDir, "Brim.exe"), "--uninstall") { UseShellExecute = true })))
                : Row(Installer.IsInstalled ? "Switch to the installed Brim" : "Install Brim",
                    Installer.IsInstalled
                        ? "This Brim is running from another folder. Switching updates the installed copy with this version and runs that one from now on."
                        : "Adds Brim to Start and to Settings › Apps (with an uninstaller), then restarts it from there. No admin rights needed.",
                    PillBtn(Installer.IsInstalled ? "Switch" : "Install", () =>
                    {
                        try
                        {
                            Installer.Install();
                            // Run the installed copy from now on; this one steps aside
                            Installer.SwitchToInstalled();
                            AppRef.Quit();
                        }
                        catch (Exception ex) { MessageBox.Show(this, "Couldn't install: " + ex.Message, "Brim"); }
                    }))
        );

        Section("Updates");
        string status = Updater.Now switch
        {
            Updater.State.Checking => "Checking…",
            Updater.State.Downloading => $"Downloading Brim {Updater.NewVersion}… {Updater.Progress:P0}",
            Updater.State.Ready => $"Brim {Updater.NewVersion} is downloaded and ready",
            Updater.State.UpToDate => $"Up to date (checked {Updater.LastChecked:HH:mm})",
            Updater.State.Failed => Updater.Problem ?? "Couldn't check",
            _ => Installer.RunningInstalled ? "Brim checks a little after it starts, then every few hours" : "Only the installed Brim updates itself",
        };
        Card(
            Row("Get updates automatically", "Downloads new versions in the background. Installing always asks first (it restarts Brim).",
                Switch(S.CheckUpdates, v => { S.CheckUpdates = v; Commit(); })),
            Updater.Now == Updater.State.Ready
                ? Row($"Brim {Updater.NewVersion}", status, PillBtn("Restart to update", () => AppRef.InstallUpdate()))
                : Row($"Brim {Installer.Version}", status, PillBtn("Check now", async () =>
                {
                    if (!Installer.RunningInstalled) { Launcher.Open(Updater.ReleasesPage); return; }
                    var check = Updater.Check();
                    Rebuild();
                    await check;
                    Rebuild();
                })),
            Row("What's new", "Release notes for every version", PillBtn("Open", () => Launcher.Open($"https://github.com/{Updater.Repo}/releases")))
        );

        Section("Backup");
        Card(
            Row("Save my settings", "Your notch, dock, widgets, shortcuts, games and look, in one file", PillBtn("Save to file…", () =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save Brim settings", FileName = $"Brim settings {DateTime.Now:yyyy-MM-dd}", DefaultExt = ".json", Filter = "Brim settings|*.json" };
                if (dlg.ShowDialog(this) != true) return;
                try { Installer.Export(dlg.FileName); }
                catch (Exception ex) { MessageBox.Show(this, "Couldn't save: " + ex.Message, "Brim"); }
            })),
            Row("Load settings", "Replaces your current settings (they're kept as a copy in the settings folder, just in case)", PillBtn("Load from file…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Load Brim settings", Filter = "Brim settings|*.json|All files|*.*" };
                if (dlg.ShowDialog(this) != true) return;
                try
                {
                    if (!Installer.Import(dlg.FileName)) { MessageBox.Show(this, "That file isn't a Brim settings file.", "Brim"); return; }
                    ThemeManager.Apply();
                    AppRef.ApplyAll();
                    Rebuild();
                }
                catch (Exception ex) { MessageBox.Show(this, "Couldn't load: " + ex.Message, "Brim"); }
            })),
            Row("Settings and log files", SettingsStore.Folder, PillBtn("Open folder", () => Launcher.Open(SettingsStore.Folder)))
        );

        Section("Brim");
        Card(
            Row("Quit Brim", "Puts the Windows taskbar and desktop icons back the way they were", PillBtn("Quit", () => AppRef.Quit()))
        );
        page.Children.Add(Spaced(Dim("Brim keeps everything on this PC. The clipboard history stays in memory unless you turn on “Remember after a restart” (Notch page). Copies made by password managers are always skipped."), 14));
    }

    // ================================================================== building blocks

    void H1(string title, string? sub)
    {
        page.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        if (sub != null)
            page.Children.Add(new TextBlock { Text = sub, FontSize = 13, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
    }

    static readonly Brush Gray = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA3));

    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    void Section(string title)
    {
        page.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Gray,
            Margin = new Thickness(2, 28, 0, 10),
        });
    }

    void Card(params UIElement?[] rows)
    {
        var stack = new StackPanel();
        bool first = true;
        foreach (var r in rows)
        {
            if (r == null) continue;
            if (!first)
            {
                var line = new Border { Height = 1 };
                line.SetResourceReference(Border.BackgroundProperty, "Set.Line");
                stack.Children.Add(line);
            }
            stack.Children.Add(r);
            first = false;
        }
        var card = new Border { CornerRadius = new CornerRadius(12), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "Set.Card");
        page.Children.Add(card);
    }

    static FrameworkElement Pad(UIElement el) => new Border { Padding = new Thickness(18, 14, 18, 14), Child = el };

    static TextBlock Dim(string text) => new() { Text = text, FontSize = 12, Foreground = Gray, TextWrapping = TextWrapping.Wrap };

    static FrameworkElement Spaced(FrameworkElement el, double top) { el.Margin = new Thickness(0, top, 0, 0); return el; }

    static FrameworkElement Row(string label, string? desc, UIElement control)
    {
        // Long sets of choices go under the label instead of squeezing beside it
        if (control is WrapPanel wide && (wide.Children.Count > 4 || wide.Children.OfType<ContentControl>().Sum(x => (x.Content as string)?.Length ?? 6) > 30))
        {
            var stack = new StackPanel { Margin = new Thickness(18, 13, 18, 13) };
            stack.Children.Add(new TextBlock { Text = label, FontSize = 13.5, Foreground = Brushes.White });
            if (!string.IsNullOrEmpty(desc))
                stack.Children.Add(new TextBlock { Text = desc, FontSize = 12, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            wide.HorizontalAlignment = HorizontalAlignment.Left;
            wide.Margin = new Thickness(0, 10, 0, -6);
            stack.Children.Add(wide);
            return stack;
        }
        var g = new Grid { Margin = new Thickness(18, 13, 18, 13) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        left.Children.Add(new TextBlock { Text = label, FontSize = 13.5, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(desc))
            left.Children.Add(new TextBlock { Text = desc, FontSize = 12, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(left);
        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.HorizontalAlignment = HorizontalAlignment.Right; }
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    static FrameworkElement Info(string key, string text)
    {
        var g = new Grid { Margin = new Thickness(18, 11, 18, 11) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = key, FontSize = 13, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
        var t = new TextBlock { Text = text, FontSize = 13, Foreground = Gray, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    CheckBox Switch(bool value, Action<bool> set)
    {
        var cb = new CheckBox { IsChecked = value };
        cb.SetResourceReference(StyleProperty, "Switch");
        cb.Checked += (_, _) => set(true);
        cb.Unchecked += (_, _) => set(false);
        return cb;
    }

    static int chipGroup;

    FrameworkElement Chips(string[] options, string selected, Action<string> set)
    {
        var wp = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -6, -6) };
        string group = "chips" + chipGroup++;
        foreach (var o in options)
        {
            var rb = new RadioButton { Content = o, GroupName = group, IsChecked = o == selected };
            rb.SetResourceReference(StyleProperty, "Chip");
            var opt = o;
            rb.Checked += (_, _) => set(opt);
            wp.Children.Add(rb);
        }
        return wp;
    }

    /// <summary>A compact "Value ▾" button that opens a menu of choices (for long lists that would crowd a row).</summary>
    FrameworkElement Dropdown(string[] options, string selected, Action<string> set)
    {
        var b = PillBtn(selected + "   ▾", () => { });
        b.Margin = new Thickness(0);
        b.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            foreach (var o in options)
            {
                var opt = o;
                var mi = new MenuItem { Header = o, IsCheckable = true, IsChecked = o == selected };
                mi.Click += (_, _) => set(opt);
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        };
        return b;
    }

    /// <summary>Pick a screen, shown only when there's more than one.</summary>
    FrameworkElement? ScreenRow(string label, string current, Action<string> set, bool everyScreen = false)
    {
        var screens = Native.Screens();
        // "Every screen" stays visible while it's chosen, even with one screen plugged in
        if (screens.Count < 2 && !(everyScreen && current == "*")) return null;
        var names = screens.Select((s, i) => s.Primary ? "Main screen" : $"Screen {i + 1}").ToList();
        if (everyScreen) names.Add("Every screen");
        int idx = screens.FindIndex(s => string.Equals(s.Device, current, StringComparison.OrdinalIgnoreCase));
        var sel = current == "*" && everyScreen ? "Every screen" : idx >= 0 ? names[idx] : "Main screen";
        return Row(label, everyScreen ? "Every screen puts a notch at the top of each monitor; pop-ups show on all of them" : null, Chips(names.ToArray(), sel, v =>
        {
            if (v == "Every screen") { set("*"); return; }
            int k = names.IndexOf(v);
            set(k <= 0 || screens[k].Primary ? "" : screens[k].Device);
        }));
    }

    FrameworkElement SliderCtl(double min, double max, double value, Action<double> set, bool rebuildLabel = false)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Width = 200 };
        s.SetResourceReference(StyleProperty, "BrimSlider");
        s.ValueChanged += (_, e) => set(e.NewValue);
        if (rebuildLabel) s.PreviewMouseUp += (_, _) => Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Background);
        return s;
    }

    static Button PillBtn(string text, Action action)
    {
        var b = new Button { Content = text };
        b.SetResourceReference(StyleProperty, "Pill");
        b.SetResourceReference(BackgroundProperty, "Set.Field");
        b.Foreground = Brushes.White;
        b.Margin = new Thickness(0, 0, 8, 0);
        b.Click += (_, _) => action();
        return b;
    }

    FrameworkElement ListRow(ShortcutItem item, string? badge, Action? moveUp, Action remove)
    {
        var g = new Grid { Margin = new Thickness(14, 8, 10, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = IconHelper.IsUrl(item.Path) ? Favicons.Cached(item.Path) : IconHelper.ForPath(item.Path);
        if (icon != null) g.Children.Add(new Image { Source = icon, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left });
        else g.Children.Add(new TextBlock { Text = IconHelper.IsUrl(item.Path) ? "" : "", FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 18, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock { Text = item.Name, FontSize = 13.5, Foreground = Brushes.White });
        if (badge != null) nameRow.Children.Add(new TextBlock { Text = badge, FontSize = 11, Foreground = Gray, Margin = new Thickness(10, 1, 0, 0) });
        text.Children.Add(nameRow);
        text.Children.Add(new TextBlock { Text = item.Path, FontSize = 11, Foreground = Gray, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        if (moveUp != null) actions.Children.Add(IconBtn("", "Move up", moveUp));
        actions.Children.Add(IconBtn("", "Remove", remove));
        Grid.SetColumn(actions, 2);
        g.Children.Add(actions);
        return g;
    }

    static Button IconBtn(string glyph, string tip, Action a)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Foreground = Brushes.White };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.Click += (_, _) => a();
        return b;
    }

    /// <summary>A small picture of the theme: its surface, a raised bar, the accent, its name and one line about it.</summary>
    static FrameworkElement ThemeCard(Palette p, bool selected, Action pick)
    {
        var surface = ThemeManager.Parse(p.Surface);
        if (surface.A < 255) surface = Color.FromRgb((byte)(surface.R * surface.A / 255), (byte)(surface.G * surface.A / 255), (byte)(surface.B * surface.A / 255));
        var accent = ThemeManager.Parse(p.OwnAccent ? p.DefaultAccent : S.Accent);
        var text = ThemeManager.Parse(p.Text);
        var dim = ThemeManager.Parse(p.Dim);
        var raised = ThemeManager.Parse(p.Raised);

        var preview = new Grid { Height = 54 };
        // a mini notch hanging from the top, with an accent bar inside it
        var notch = new Border
        {
            Width = 62, Height = 16, CornerRadius = new CornerRadius(0, 0, 7, 7), Background = Frozen(raised.A < 255 ? Color.FromRgb(60, 60, 66) : raised),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        };
        notch.Child = new Border { Width = 22, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Frozen(accent), VerticalAlignment = VerticalAlignment.Center };
        preview.Children.Add(notch);
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var c in new[] { accent, p.Swatch2 != null ? ThemeManager.Parse(p.Swatch2) : dim, text })
            dots.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = Frozen(c), Margin = new Thickness(3, 0, 3, 0) });
        preview.Children.Add(dots);

        var body = new StackPanel();
        body.Children.Add(preview);
        body.Children.Add(new TextBlock { Text = p.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Frozen(text), Margin = new Thickness(10, 2, 10, 0) });
        body.Children.Add(new TextBlock
        {
            Text = p.Tagline ?? "Calm and simple", FontSize = 10.5, Foreground = Frozen(dim), Margin = new Thickness(10, 1, 10, 9),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var card = new Border
        {
            Width = 148, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(12), Background = Frozen(surface),
            BorderThickness = new Thickness(selected ? 2 : 1), Cursor = System.Windows.Input.Cursors.Hand, Child = body,
            BorderBrush = selected ? Frozen(accent) : Frozen(p.Stroke != null ? ThemeManager.Parse(p.Stroke) : Color.FromArgb(50, 255, 255, 255)),
            ToolTip = $"Use the {p.Name} theme",
        };
        card.MouseLeftButtonUp += (_, _) => pick();
        return card;
    }

    static ControlTemplate SwatchTemplate(Color c, bool selected)
    {
        var t = new ControlTemplate(typeof(Button));
        var outer = new FrameworkElementFactory(typeof(Border));
        outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        outer.SetValue(Border.BorderThicknessProperty, new Thickness(selected ? 2 : 0));
        outer.SetValue(Border.BorderBrushProperty, Brushes.White);
        outer.SetValue(Border.PaddingProperty, new Thickness(selected ? 3 : 0));
        var inner = new FrameworkElementFactory(typeof(Border));
        inner.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        inner.SetValue(Border.BackgroundProperty, new SolidColorBrush(c));
        outer.AppendChild(inner);
        t.VisualTree = outer;
        return t;
    }
}
