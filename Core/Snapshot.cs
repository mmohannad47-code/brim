using System.Linq;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brim.Views;

namespace Brim.Core;

/// <summary>
/// Developer aid: `Brim.exe --snapshot <folder>` renders each surface to PNG off-screen and exits,
/// without touching the taskbar, the desktop or saved settings.
/// </summary>
public static class Snapshot
{
    public static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder);
        SettingsStore.ReadOnly = true;
        // BRIM_FRESH=1: everything as a brand-new install sees it
        if (Environment.GetEnvironmentVariable("BRIM_FRESH") == "1") SettingsStore.UseFactorySettings();
        await DeviceBattery.Refresh(first: true); // real connected controllers/headphones for the Home tab
        File.WriteAllText(Path.Combine(folder, "devices.txt"), string.Join("\n", DeviceBattery.Devices.Select(d => $"{d.Name}: {d.BatteryText}")));
        var s = SettingsStore.Current;
        s.DockReserve = false;
        s.DockAutoHide = false;
        s.HideInFullscreen = false;
        s.DockTaskManager = true; // show every optional dock button
        ThemeManager.Apply();
        SystemInfo.Sample();
        await MediaService.Instance.InitAsync();
        await Task.Delay(400);
        SystemInfo.Sample();

        var notch = new NotchWindow();
        notch.Show();
        await Task.Delay(300);
        notch.Left = -4000;
        await Task.Delay(500);
        Save(notch, Path.Combine(folder, "notch-closed.png"));

        // Repro: "hide while an app is in front" -> app focused (tucked away) -> back on the desktop
        s.HideOverApps = true;
        notch.SetAppInFront(true);
        await Task.Delay(900);
        Save(notch, Path.Combine(folder, "notch-overapp-1-app.png"));
        notch.SetAppInFront(false);
        await Task.Delay(900);
        Save(notch, Path.Combine(folder, "notch-overapp-2-desktop.png"));
        s.HideOverApps = false;

        // Repro: Clear surface, then what adding a widget does (theme + settings re-applied)
        var surface = s.NotchSurface;
        s.NotchSurface = "Clear";
        notch.ApplySettings();
        notch.Left = -4000;
        await Task.Delay(400);
        Save(notch, Path.Combine(folder, "notch-clear-1-before.png"));
        ThemeManager.Apply();
        notch.ApplySettings();
        notch.Left = -4000;
        await Task.Delay(400);
        Save(notch, Path.Combine(folder, "notch-clear-2-after-apply.png"));
        s.NotchSurface = surface;
        notch.ApplySettings();
        notch.Left = -4000;
        await GameLibrary.Refresh();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Shelf.Add(new[] { Path.Combine(win, "notepad.exe"), Path.Combine(win, "explorer.exe"), Path.Combine(win, "win.ini"), Path.Combine(win, "System32") });
        Shelf.Items[0].Selected = true; Shelf.Items[2].Selected = true;
        GameLibrary.SetOrder(new() { "riot:league_of_legends.live", "folder:d:\\games\\bloodborne" });
        await MessageCenter.Refresh();
        foreach (var tab in new[] { "Home", "Apps", "Games", "Messages", "Notes", "Shelf", "Clip", "Ctrl", "Timer" })
        {
            notch.DebugOpen(tab);
            await Task.Delay(900);
            Save(notch, Path.Combine(folder, $"notch-open-{tab}.png"));
        }

        // Home without music, with weather and the latest message; then fewer tabs in another order
        var homeItems = s.HomeItems.ToList();
        var tabs = s.NotchTabs.ToList();
        s.HomeItems = new() { "Date", "Weather", "Message", "Cpu", "Memory", "Volume" };
        s.NotchTabs = new() { "Messages", "Home", "Games" };
        notch.ApplySettings();
        notch.Left = -4000;
        notch.DebugOpen("Home");
        await Task.Delay(900);
        Save(notch, Path.Combine(folder, "notch-custom-home.png"));
        s.HomeItems = homeItems;
        s.NotchTabs = tabs;

        // Themes
        var theme = s.Theme;
        var accent = s.Accent;
        foreach (var t in new[] { "Vampire", "Synthwave", "Matrix", "Gold" })
        {
            s.Theme = t;
            s.Accent = ThemeManager.Palettes.First(p => p.Name == t).DefaultAccent;
            ThemeManager.Apply();
            notch.ApplySettings();
            notch.Left = -4000;
            notch.DebugOpen("Home");
            await Task.Delay(700);
            Save(notch, Path.Combine(folder, $"theme-{t}-notch.png"));
        }
        s.Theme = "Vampire";
        s.Accent = "#E3122F";
        ThemeManager.Apply();
        var vset = new SettingsWindow { Left = -4000, Top = 0, Height = 1100, WindowStartupLocation = WindowStartupLocation.Manual };
        vset.Show();
        vset.Show("Look");
        await Task.Delay(500);
        Save(vset, Path.Combine(folder, "theme-Vampire-settings.png"));
        vset.Close();
        s.Theme = theme;
        s.Accent = accent;
        ThemeManager.Apply();

        // Positions: the card sits at the window's left / right edge
        foreach (var pos in new[] { "Left", "Right" })
        {
            s.NotchPosition = pos;
            notch.ApplySettings();
            notch.Left = -4000;
            notch.DebugOpen("Home");
            await Task.Delay(700);
            Save(notch, Path.Combine(folder, $"notch-pos-{pos}.png"));
        }
        s.NotchPosition = "Center";
        notch.ApplySettings();
        notch.Left = -4000;

        // Uninstall: what would be offered alongside each program (nothing is removed)
        var progs = Uninstaller.Everything();
        var rel = new System.Collections.Generic.List<string> { $"{progs.Count} programs, {progs.Count(Uninstaller.IsFramework)} frameworks hidden" };
        foreach (var p in progs.Where(p => !Uninstaller.IsFramework(p)))
        {
            var r = Uninstaller.Related(p, progs);
            if (r.Count > 0) rel.Add($"{p.Name} [{p.KeyName}] -> " + string.Join(" | ", r.Select(x => $"{x.Program.Name} ({x.Reason}{(x.StillUsedBy != null ? "; used by " + x.StillUsedBy : "")})")));
        }
        rel.Add("--- games matched to programs");
        foreach (var g in GameLibrary.Current) { var m = Uninstaller.ForGameAny(g, progs); rel.Add($"{g.Name} -> {(m == null ? "(none)" : m.Name + " [" + m.Kind + "]")}"); }
        rel.Add("--- kinds: " + string.Join(", ", progs.GroupBy(p => p.Kind).Select(k => $"{k.Key} {k.Count()}")));
        rel.Add("--- frameworks (never offered)");
        rel.AddRange(progs.Where(Uninstaller.IsFramework).Select(p => "  " + p.Name));
        File.WriteAllLines(Path.Combine(folder, "uninstall.txt"), rel);

        await GameRenderer.Warm(GameLibrary.Current);
        // Launch-with labels for every game with more than one exe
        File.WriteAllLines(Path.Combine(folder, "launch-with.txt"),
            GameLibrary.Current.SelectMany(g => GameRenderer.Options(g).Select(o => $"{g.Name} [{g.Source}]: {o.Label}")));
        notch.ToggleOpen();
        await Task.Delay(700);
        // The song-change pop-up (it used to open empty)
        notch.DebugPeek();
        await Task.Delay(900);
        Save(notch, Path.Combine(folder, "notch-peek.png"));
        notch.DebugOpen("Home");
        notch.ToggleOpen();
        await Task.Delay(700);
        notch.ShowActivity("", "Volume", "64", 64);
        await Task.Delay(700);
        Save(notch, Path.Combine(folder, "notch-activity.png"));
        notch.Close();
        File.WriteAllLines(Path.Combine(folder, "games.txt"),
            GameLibrary.Current.Select(g => $"{g.Name} | {g.Source} | {g.Exe ?? g.LaunchUri} | {g.Args}"));

        var dock = new DockWindow();
        dock.Show();
        await Task.Delay(1500);
        dock.Left = -4000;
        Save(dock, Path.Combine(folder, "dock.png"));
        dock.DebugDocked();
        await Task.Delay(600);
        dock.Left = -4000;
        Save(dock, Path.Combine(folder, "dock-docked.png"));
        dock.Close();

        foreach (var sc in Scenes.All)
        {
            var host = new Window
            {
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = 0,
                Width = 480, Height = 270, Content = new SceneView(sc.Make(), startTime: 40, animate: false),
            };
            host.Show();
            await Task.Delay(150);
            Save(host, Path.Combine(folder, $"scene-{sc.Id}.png"));
            host.Close();
        }

        var variants = new (string name, WidgetItem item)[]
        {
            ("combo-column", new WidgetItem { Kind = WidgetKind.Clock, Parts = new() { WidgetKind.Clock, WidgetKind.Weather, WidgetKind.Music }, X = -4000, Y = 0 }),
            ("combo-row-frost-serif", new WidgetItem { Kind = WidgetKind.Clock, Parts = new() { WidgetKind.Clock, WidgetKind.Calendar }, Layout = "Row", Look = "Frost", Font = "Serif", X = -4000, Y = 0 }),
            ("outline-mono-large", new WidgetItem { Kind = WidgetKind.Stats, Look = "Outline", Font = "Mono", Scale = 1.2, X = -4000, Y = 0 }),
            ("custom-terminal", new WidgetItem { Kind = WidgetKind.Custom, Lines = TextTokens.Presets["Terminal"](), X = -4000, Y = 0 }),
            ("custom-greeting", new WidgetItem { Kind = WidgetKind.Custom, Lines = TextTokens.Presets["Greeting"](), X = -4000, Y = 0 }),
            ("light-hand-pink", new WidgetItem { Kind = WidgetKind.Note, Look = "Tinted", Font = "Hand", TextColor = "#FFADAD", Text = "water the plants", X = -4000, Y = 0 }),
        };
        foreach (var (vname, vitem) in variants)
        {
            var w = new WidgetWindow(vitem);
            w.Show();
            await Task.Delay(500);
            Save(w, Path.Combine(folder, $"variant-{vname}.png"), Color.FromRgb(70, 90, 120));
            w.Close();
        }

        foreach (var (kind, name, _) in WidgetFactory.Catalog)
        {
            var w = new WidgetWindow(new WidgetItem { Kind = kind, X = -4000, Y = 0, Text = kind == WidgetKind.Note ? "Buy milk\nCall mum" : null });
            w.Show();
            await Task.Delay(400);
            Save(w, Path.Combine(folder, $"widget-{kind}.png"), Color.FromRgb(70, 90, 120));
            w.Close();
        }

        // World clock with two real cities (looked up like the settings page does)
        var cities = new System.Collections.Generic.List<CityItem>();
        foreach (var q in new[] { "Amman", "London" })
            if (await WeatherService.FindCity(q) is { } c) cities.Add(c);
        var wc = new WidgetWindow(new WidgetItem { Kind = WidgetKind.WorldClock, X = -4000, Y = 0, Cities = cities });
        wc.Show();
        await Task.Delay(3500); // weather arrives
        Save(wc, Path.Combine(folder, "widget-WorldClock-2.png"), Color.FromRgb(70, 90, 120));
        wc.Close();

        foreach (var (name, make) in new (string, Func<FrameworkElement>)[] { ("quick", () => new QuickPanel(false)), ("tray", () => new TrayPanel()) })
        {
            var host = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight, Left = -4000, Top = 0, ShowActivated = false,
                FontFamily = (FontFamily)Application.Current.FindResource("Brim.Font"),
            };
            var b = new System.Windows.Controls.Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(14), BorderThickness = new Thickness(1), Child = make() };
            b.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brim.Surface");
            b.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Brim.Stroke");
            host.SetResourceReference(Window.ForegroundProperty, "Brim.Text");
            host.Content = b;
            host.Show();
            await Task.Delay(2500);
            Save(host, Path.Combine(folder, $"flyout-{name}.png"));
            host.Close();
        }

        foreach (var (q, name) in new[] { ("", "search-empty"), ("set", "search-set"), ("12*7+3", "search-sum"), ("c", "search-apps") })
        {
            var search = Brim.Views.SearchWindow.Preview(q);
            search.ShowActivated = false;
            search.Show();
            await Task.Delay(1500);
            Save(search, Path.Combine(folder, name + ".png"));
            search.Close();
        }

        foreach (var (q, name) in new[] { ("", "picker-all"), ("vis", "picker-search") })
        {
            var picker = Brim.Views.AppPicker.Preview(q);
            picker.Show();
            await Task.Delay(4000);
            Save(picker, Path.Combine(folder, name + ".png"));
            picker.Close();
        }

        var gallery = new GalleryWindow { Left = -4000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
        gallery.Show();
        await Task.Delay(6000);
        Save(gallery, Path.Combine(folder, "gallery.png"));
        gallery.Close();

        var designed = new WidgetItem { Kind = WidgetKind.Custom, Lines = TextTokens.Presets["Greeting"]() };
        s.Widgets.Add(designed);
        var tall = new SettingsWindow { Left = -4000, Top = 0, Height = 2600, WindowStartupLocation = WindowStartupLocation.Manual };
        tall.Show();
        tall.ShowWidget(designed.Id);
        await Task.Delay(800);
        Save(tall, Path.Combine(folder, "settings-designer.png"));
        tall.ScrollTo(980);
        await Task.Delay(500);
        Save(tall, Path.Combine(folder, "settings-designer-2.png"));
        tall.Close();
        s.Widgets.Remove(designed);

        var set = new SettingsWindow { Left = -4000, Top = 0, Height = Environment.GetEnvironmentVariable("BRIM_TALL") == "1" ? 2200 : 660, WindowStartupLocation = WindowStartupLocation.Manual };
        set.Show();
        foreach (var page in new[] { "Notch", "Look", "Messages", "Shortcuts", "Games", "Widgets", "Taskbar", "Desktop", "Uninstall", "General" })
        {
            set.Show(page);
            await Task.Delay(300);
            Save(set, Path.Combine(folder, $"settings-{page}.png"));
        }
        set.Close();
    }

    static void Save(FrameworkElement el, string path, Color? backdrop = null)
    {
        el.UpdateLayout();
        int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
        if (w <= 0 || h <= 0) return;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(backdrop ?? Color.FromRgb(200, 205, 215)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
