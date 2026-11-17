using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brim.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Brim.Views;

/// <summary>
/// Brim's PDF editor: every page as a picture you can pick up and move. Rotate, delete, add pages from other PDFs,
/// take some pages out into a new PDF (or every page into its own), put text on a page, and set or take off a
/// password. The pictures come from Windows' own PDF reader; saving is done with PDFsharp. Saving never overwrites
/// the original: the edited PDF goes next to it.
/// </summary>
public sealed class PdfEditorWindow : Window
{
    /// <summary>Text put on a page: where (as a fraction of the page as you see it), what, and how big in points.</summary>
    sealed record Stamp(double Fx, double Fy, string Text, double Size);

    sealed class PageRef
    {
        public string Source = "";
        public int Index;
        public int Turn;            // our extra rotation, 0/90/180/270
        public int Own;             // the page's own rotation in the file
        public double WidthPt, HeightPt; // as Windows shows it (its own rotation applied)
        public BitmapSource? Picture;
        public List<Stamp> Stamps = new();
    }

    static readonly Dictionary<string, PdfEditorWindow> open = new(StringComparer.OrdinalIgnoreCase);

    readonly string first;
    readonly Dictionary<string, string?> passwords = new(StringComparer.OrdinalIgnoreCase);
    readonly List<PageRef> pages = new();
    readonly WrapPanel grid = new() { Margin = new Thickness(18, 10, 18, 18) };
    readonly TextBlock status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Slider zoom = new() { Minimum = 110, Maximum = 420, Value = 170, Width = 140, VerticalAlignment = VerticalAlignment.Center };
    readonly HashSet<PageRef> selected = new();
    PageRef? anchor;
    string? newPassword;
    bool removePassword;
    (string text, double size)? placing;
    LiveReorder? reorder;
    bool changed;

    public static void Open(string path)
    {
        if (open.TryGetValue(path, out var w)) { if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal; w.Activate(); return; }
        w = new PdfEditorWindow(path);
        open[path] = w;
        w.Show();
    }

    static bool exitGuard;

    PdfEditorWindow(string path)
    {
        // After the PDF tools have been used, Windows was seen to get stuck unloading its parts once Brim had
        // finished quitting (a process left behind with one thread, which also blocks the next start and updates).
        // So once Brim's own shutdown is done, it ends the process there and then. Registered now, so it runs after
        // every handler added at start-up.
        if (!exitGuard)
        {
            exitGuard = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Native.TerminateProcess(Native.GetCurrentProcess(), (uint)Environment.ExitCode);
        }
        first = path;
        Title = "Brim · Edit PDF · " + Path.GetFileName(path);
        Width = 1040; Height = 720; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("Brim.Font");
        SetResourceReference(BackgroundProperty, "Set.Bg");
        Foreground = Brushes.White;
        UseLayoutRounding = true;
        AllowDrop = true;
        SourceInitialized += (_, _) => { int dark = 1; Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4); };
        Closed += (_, _) => open.Remove(first);
        Closing += (_, e) =>
        {
            if (!changed) return;
            var r = MessageBox.Show(this, "Save your changes to a new PDF first?", "Edit PDF", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) e.Cancel = true;
            else if (r == MessageBoxResult.Yes) e.Cancel = !Save(null);
        };

        var root = new DockPanel();
        root.Children.Add(Toolbar());
        var foot = new Border { Padding = new Thickness(18, 8, 18, 10) };
        DockPanel.SetDock(foot, Dock.Bottom);
        var fg = new Grid();
        fg.ColumnDefinitions.Add(new ColumnDefinition());
        fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        fg.Children.Add(status);
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal };
        zoomRow.Children.Add(Dim("Page size", 12, new Thickness(0, 0, 10, 0)));
        zoom.SetResourceReference(StyleProperty, "BrimSlider");
        zoom.ValueChanged += (_, _) => { foreach (var c in grid.Children.OfType<Border>()) Size(c); };
        zoomRow.Children.Add(zoom);
        Grid.SetColumn(zoomRow, 1);
        fg.Children.Add(zoomRow);
        foot.Child = fg;
        root.Children.Add(foot);
        var scroll = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.PreviewMouseWheel += (_, e) => { if (Keyboard.Modifiers == ModifierKeys.Control) { zoom.Value += e.Delta > 0 ? 20 : -20; e.Handled = true; } };
        root.Children.Add(scroll);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) Delete();
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { selected.Clear(); foreach (var p in pages) selected.Add(p); Refresh(); }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) Save(null);
            else if (e.Key == Key.Escape && placing != null) { placing = null; Cursor = null; Refresh(); }
        };
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var f in files.Where(Docs.IsPdf))
            {
                try { await AddFile(f, pages.Count); }
                catch (Exception ex) { Log.Error("pdf editor", ex); MessageBox.Show(this, $"Couldn't open {Path.GetFileName(f)}: {ex.Message}", "Edit PDF"); }
            }
        };
        Loaded += async (_, _) =>
        {
            try { await AddFile(first, 0); changed = false; Refresh(); }
            catch (Exception ex)
            {
                Log.Error("pdf editor", ex);
                MessageBox.Show(this, $"Couldn't open {Path.GetFileName(first)}: {ex.Message}", "Edit PDF");
                Close();
            }
        };
    }

    // ---------------------------------------------------------------- toolbar

    FrameworkElement Toolbar()
    {
        var bar = new Border { Padding = new Thickness(14, 12, 14, 6) };
        DockPanel.SetDock(bar, Dock.Top);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new WrapPanel();
        left.Children.Add(Tool("", "Add PDF", "Add the pages of another PDF at the end (or drop PDFs on this window)", async () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PDF|*.pdf", Multiselect = true, Title = "Add pages from" };
            if (dlg.ShowDialog(this) == true) foreach (var f in dlg.FileNames) await AddFile(f, pages.Count);
        }));
        left.Children.Add(Tool("", "Rotate left", "Turn the selected pages a quarter to the left", () => Turn(-90)));
        left.Children.Add(Tool("", "Rotate right", "Turn the selected pages a quarter to the right", () => Turn(90), flip: true));
        left.Children.Add(Tool("", "Delete", "Take the selected pages out (Delete key)", Delete));
        left.Children.Add(Tool("", "Extract", "Save the selected pages as a new PDF", Extract));
        left.Children.Add(Tool("", "Split all", "Every page as a PDF of its own, in a new folder", SplitAll));
        left.Children.Add(Tool("", "Add text", "Type some text, then click on a page to put it there", AddText));
        left.Children.Add(Tool("", "Password", "Lock the PDF with a password, or take its password off", Password));
        g.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var saveAs = Tool(null, "Save as…", "Choose where to save", () => Save(Ask()));
        right.Children.Add(saveAs);
        var save = Tool(null, "Save", "Save as a new PDF next to the original (Ctrl+S)", () => Save(null));
        save.SetResourceReference(Control.BackgroundProperty, "Brim.Accent");
        save.SetResourceReference(Control.ForegroundProperty, "Brim.OnAccent");
        right.Children.Add(save);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        bar.Child = g;
        return bar;

        string? Ask()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PDF|*.pdf", FileName = Path.GetFileNameWithoutExtension(first) + " (edited).pdf", InitialDirectory = Path.GetDirectoryName(first) };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }
    }

    Button Tool(string? glyph, string text, string tip, Action click, bool flip = false) => Tool(glyph, text, tip, () => { click(); return Task.CompletedTask; }, flip);

    Button Tool(string? glyph, string text, string tip, Func<Task> click, bool flip = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph != null)
        {
            var i = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Brim.Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            if (flip) { i.RenderTransformOrigin = new Point(0.5, 0.5); i.RenderTransform = new ScaleTransform(-1, 1); }
            content.Children.Add(i);
        }
        content.Children.Add(new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, Height = 34, Padding = new Thickness(12, 0, 14, 0), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip };
        b.SetResourceReference(StyleProperty, "Pill");
        b.SetResourceReference(Control.BackgroundProperty, "Set.Field");
        b.SetResourceReference(Control.ForegroundProperty, "Brim.Text");
        b.Click += async (_, _) =>
        {
            try { await click(); }
            catch (Exception ex) { Log.Error("pdf editor", ex); MessageBox.Show(this, ex.Message, "Edit PDF", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        return b;
    }

    // ---------------------------------------------------------------- pages

    async Task AddFile(string path, int at)
    {
        string? pw = passwords.TryGetValue(path, out var known) ? known : null;
        // Windows' PDF reader is used only on a background thread: its objects made on this window's thread have to be
        // let go of on it too, and when Brim quit with some still around it never finished quitting
        PdfReaderJob job;
        while (true)
        {
            var tryPw = pw;
            try { job = await Task.Run(() => PdfReaderJob.Open(path, tryPw)); break; }
            catch (Exception) when (doc_IsProtected(path))
            {
                pw = AskText("This PDF has a password", $"Type the password for {Path.GetFileName(path)}:", secret: true);
                if (pw == null) return;
            }
        }
        passwords[path] = pw;
        var added = job.Pages.Select((p, i) => new PageRef { Source = path, Index = i, Own = p.own, WidthPt = p.w, HeightPt = p.h }).ToList();
        pages.InsertRange(Math.Min(at, pages.Count), added);
        if (at > 0) changed = true;
        Refresh();
        // The pictures, a page at a time (a long PDF stays usable while they come in)
        try
        {
            foreach (var r in added)
            {
                try
                {
                    var png = await Task.Run(() => job.Render(r.Index, 420));
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new MemoryStream(png);
                    bmp.EndInit();
                    bmp.Freeze();
                    r.Picture = bmp;
                    var card = grid.Children.OfType<Border>().FirstOrDefault(c => c.Tag == r);
                    if (card != null) Fill(card);
                }
                catch (Exception ex) { Log.Error("pdf page picture", ex); }
                if (!IsLoaded) break; // the window was closed
            }
        }
        finally { job.Dispose(); }
    }

    /// <summary>One PDF open in Windows' reader, used from background threads only (see <see cref="AddFile"/>).</summary>
    sealed class PdfReaderJob : IDisposable
    {
        Windows.Data.Pdf.PdfDocument? doc;
        public List<(int own, double w, double h)> Pages = new();

        public static PdfReaderJob Open(string path, string? password)
        {
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            var doc = (password == null ? Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file) : Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file, password)).AsTask().GetAwaiter().GetResult();
            var job = new PdfReaderJob { doc = doc };
            for (uint i = 0; i < doc.PageCount; i++)
            {
                using var p = doc.GetPage(i);
                job.Pages.Add((p.Rotation switch { Windows.Data.Pdf.PdfPageRotation.Rotate90 => 90, Windows.Data.Pdf.PdfPageRotation.Rotate180 => 180, Windows.Data.Pdf.PdfPageRotation.Rotate270 => 270, _ => 0 },
                               p.Size.Width * 72 / 96, p.Size.Height * 72 / 96));
            }
            return job;
        }

        /// <summary>The page as a PNG, <paramref name="width"/> pixels wide.</summary>
        public byte[] Render(int index, uint width)
        {
            if (doc == null) throw new ObjectDisposedException(nameof(PdfReaderJob));
            using var p = doc.GetPage((uint)index);
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            p.RenderToStreamAsync(ras, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = width }).AsTask().GetAwaiter().GetResult();
            ras.Seek(0);
            using var s = ras.AsStreamForRead();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        public void Dispose() => doc = null;
    }

    static bool doc_IsProtected(string path)
    {
        try { using var d = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly); return false; }
        catch (PdfReaderException) { return true; }
        catch { return true; }
    }

    void Refresh()
    {
        reorder?.End();
        reorder = null;
        grid.Children.Clear();
        for (int i = 0; i < pages.Count; i++) grid.Children.Add(Card(pages[i]));
        selected.RemoveWhere(p => !pages.Contains(p));
        status.Text = $"{pages.Count} page{(pages.Count == 1 ? "" : "s")}" +
                      (selected.Count > 0 ? $" · {selected.Count} selected" : " · click to select (Ctrl or Shift for more) · drag to reorder") +
                      (placing != null ? " · click on a page to put the text there (Esc to stop)" : "") +
                      (newPassword != null ? " · will be saved with a password" : removePassword ? " · will be saved without a password" : "") +
                      (changed ? " · not saved yet" : "");
    }

    Border Card(PageRef r)
    {
        var card = new Border { Tag = r, CornerRadius = new CornerRadius(12), Padding = new Thickness(8), Margin = new Thickness(0, 0, 14, 14), BorderThickness = new Thickness(2), Cursor = Cursors.Hand };
        card.SetResourceReference(Border.BackgroundProperty, "Set.Card");
        card.BorderBrush = selected.Contains(r) ? (Brush)FindResource("Brim.Accent") : Brushes.Transparent;
        Fill(card);
        Size(card);
        Point? down = null;
        card.MouseLeftButtonDown += (_, e) => { down = e.GetPosition(grid); e.Handled = true; };
        card.MouseMove += (_, e) =>
        {
            if (down is not Point p || e.LeftButton != MouseButtonState.Pressed || placing != null) return;
            if ((e.GetPosition(grid) - p).Length < 8) return;
            down = null;
            reorder ??= new LiveReorder(grid, c => c.Tag is PageRef, null, moved =>
            {
                if (!moved) return;
                var order = grid.Children.OfType<Border>().Select(c => (PageRef)c.Tag).ToList();
                pages.Clear();
                pages.AddRange(order);
                changed = true;
                Dispatcher.BeginInvoke(new Action(Refresh));
            }, lift: 0, liftScale: 1.05) { Wrap = true };
            reorder.Begin(card, e.GetPosition(grid));
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (down == null) return;
            down = null;
            if (placing is { } t)
            {
                var img = (FrameworkElement)((Grid)card.Child).Children[0];
                var at = e.GetPosition(img);
                double fx = Math.Clamp(at.X / Math.Max(1, img.ActualWidth), 0, 1), fy = Math.Clamp(at.Y / Math.Max(1, img.ActualHeight), 0, 1);
                r.Stamps.Add(new Stamp(fx, fy, t.text, t.size));
                placing = null;
                Cursor = null;
                changed = true;
                Refresh();
                return;
            }
            Select(r);
        };
        return card;
    }

    void Select(PageRef r)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && anchor != null && pages.Contains(anchor))
        {
            int a = pages.IndexOf(anchor), b = pages.IndexOf(r);
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) selected.Add(pages[i]);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { if (!selected.Add(r)) selected.Remove(r); anchor = r; }
        else { bool only = selected.Count == 1 && selected.Contains(r); selected.Clear(); if (!only) selected.Add(r); anchor = r; }
        Refresh();
    }

    /// <summary>The page picture (turned as you've turned it), its text, and its number.</summary>
    void Fill(Border card)
    {
        var r = (PageRef)card.Tag;
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var page = new Grid { Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        if (r.Picture != null)
            page.Children.Add(new Image { Source = r.Picture, Stretch = Stretch.Fill, LayoutTransform = new RotateTransform(r.Turn) });
        // The text you've added, shown where it will go
        var stamps = new Canvas { IsHitTestVisible = false };
        page.Children.Add(stamps);
        page.SizeChanged += (_, _) =>
        {
            stamps.Children.Clear();
            double shownWidthPt = r.Turn % 180 == 0 ? r.WidthPt : r.HeightPt;
            double scale = page.ActualWidth / Math.Max(1, shownWidthPt);
            foreach (var s in r.Stamps)
            {
                var t = new TextBlock { Text = s.Text, FontFamily = new FontFamily("Arial"), FontSize = Math.Max(1, s.Size * scale), Foreground = Brushes.Black };
                Canvas.SetLeft(t, s.Fx * page.ActualWidth);
                Canvas.SetTop(t, s.Fy * page.ActualHeight - s.Size * scale);
                stamps.Children.Add(t);
            }
        };
        g.Children.Add(page);
        var label = new TextBlock { Text = (pages.IndexOf(r) + 1).ToString() + (r.Source != first ? " · " + Path.GetFileNameWithoutExtension(r.Source) : "") + (r.Stamps.Count > 0 ? " · text" : ""), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        Grid.SetRow(label, 1);
        g.Children.Add(label);
        card.Child = g;
        Size(card);
    }

    void Size(Border card)
    {
        if (card.Child is not Grid g || g.Children[0] is not Grid page) return;
        var r = (PageRef)card.Tag;
        double w = zoom.Value;
        double ratio = (r.Turn % 180 == 0 ? r.HeightPt / Math.Max(1, r.WidthPt) : r.WidthPt / Math.Max(1, r.HeightPt));
        page.Width = w;
        page.Height = w * ratio;
        card.Width = w + 16;
    }

    List<PageRef> Targets()
    {
        if (selected.Count == 0) { MessageBox.Show(this, "Select one or more pages first (click a page; Ctrl or Shift for more).", "Edit PDF"); return new(); }
        return pages.Where(selected.Contains).ToList();
    }

    void Turn(int by)
    {
        foreach (var p in Targets()) p.Turn = ((p.Turn + by) % 360 + 360) % 360;
        changed = true;
        Refresh();
    }

    void Delete()
    {
        var gone = Targets();
        if (gone.Count == 0) return;
        if (gone.Count == pages.Count) { MessageBox.Show(this, "A PDF needs at least one page.", "Edit PDF"); return; }
        pages.RemoveAll(gone.Contains);
        selected.Clear();
        changed = true;
        Refresh();
    }

    void AddText()
    {
        var text = AskText("Add text", "What should it say? Then click on a page where it should go.");
        if (string.IsNullOrWhiteSpace(text)) return;
        var size = AskText("Text size", "Size in points (12 is like normal text):", initial: "14");
        if (!double.TryParse(size, out var pt)) pt = 14;
        placing = (text, Math.Clamp(pt, 4, 144));
        Cursor = Cursors.Cross;
        Refresh();
    }

    void Password()
    {
        bool locked = passwords.Values.Any(v => v != null);
        if (locked || newPassword != null)
        {
            var r = MessageBox.Show(this, "Save it without a password?\n\n(No: set a different password.)", "Password", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes) { newPassword = null; removePassword = true; changed = true; Refresh(); return; }
        }
        var pw = AskText("Lock with a password", "Anyone opening the saved PDF will need this password:", secret: true);
        if (string.IsNullOrEmpty(pw)) return;
        var again = AskText("Lock with a password", "Type it once more:", secret: true);
        if (again != pw) { MessageBox.Show(this, "The two passwords weren't the same.", "Password"); return; }
        newPassword = pw;
        removePassword = false;
        changed = true;
        Refresh();
    }

    // ---------------------------------------------------------------- saving

    /// <summary>Writes these pages (in this order, turned, with their text) to <paramref name="dest"/>.</summary>
    void Write(IEnumerable<PageRef> which, string dest, string? password)
    {
        var sources = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var output = new PdfDocument();
            foreach (var r in which)
            {
                if (!sources.TryGetValue(r.Source, out var src))
                {
                    var pw = passwords.TryGetValue(r.Source, out var p) ? p : null;
                    src = pw == null ? PdfReader.Open(r.Source, PdfDocumentOpenMode.Import) : PdfReader.Open(r.Source, pw, PdfDocumentOpenMode.Import);
                    sources[r.Source] = src;
                }
                var page = output.AddPage(src.Pages[r.Index]);
                int total = ((page.Rotate + r.Turn) % 360 + 360) % 360;
                if (r.Stamps.Count > 0)
                {
                    // Draw on the page as it lies in the file (not turned), working out where each bit of text goes
                    page.Rotate = 0;
                    GlobalFontSettings();
                    double W = page.Width.Point, H = page.Height.Point;
                    using var gfx = XGraphics.FromPdfPage(page);
                    foreach (var s in r.Stamps)
                    {
                        var (x, y) = total switch
                        {
                            90 => (s.Fy * W, H - s.Fx * H),
                            180 => (W - s.Fx * W, H - s.Fy * H),
                            270 => (W - s.Fy * W, s.Fx * H),
                            _ => (s.Fx * W, s.Fy * H),
                        };
                        var state = gfx.Save();
                        gfx.RotateAtTransform(-total, new XPoint(x, y));
                        gfx.DrawString(s.Text, new XFont("Arial", s.Size), XBrushes.Black, new XPoint(x, y));
                        gfx.Restore(state);
                    }
                }
                page.Rotate = total;
            }
            if (!string.IsNullOrEmpty(password))
            {
                output.SecuritySettings.UserPassword = password;
                output.SecuritySettings.OwnerPassword = password;
            }
            var tmp = dest + ".brimpart";
            output.Save(tmp);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
        }
        finally { foreach (var d in sources.Values) d.Dispose(); }
    }

    static bool fontsReady;
    static void GlobalFontSettings()
    {
        if (fontsReady) return;
        fontsReady = true;
        try { PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true; } catch { }
    }

    string? CurrentPassword() => removePassword ? null : newPassword ?? passwords.GetValueOrDefault(first);

    bool Save(string? dest)
    {
        try
        {
            dest ??= Docs.Beside(Path.Combine(Path.GetDirectoryName(first)!, Path.GetFileNameWithoutExtension(first) + " (edited).pdf"), ".pdf");
            if (string.Equals(dest, first, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "Pick a different name: Brim never saves over the original.", "Edit PDF"); return false; }
            Write(pages, dest, CurrentPassword());
            changed = false;
            Docs.Note(first, dest);
            Log.Note("docs", $"edited {Path.GetFileName(first)} → {Path.GetFileName(dest)} ({pages.Count} pages)");
            Refresh();
            status.Text = $"Saved as {Path.GetFileName(dest)} · {pages.Count} pages";
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("pdf save", ex);
            MessageBox.Show(this, "Couldn't save: " + ex.Message, "Edit PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    void Extract()
    {
        var some = Targets();
        if (some.Count == 0) return;
        var nums = string.Join(",", some.Select(p => pages.IndexOf(p) + 1));
        var dest = Docs.Beside(Path.Combine(Path.GetDirectoryName(first)!, $"{Path.GetFileNameWithoutExtension(first)} (pages {(nums.Length > 30 ? some.Count + " pages" : nums)}).pdf"), ".pdf");
        Write(some, dest, CurrentPassword());
        Docs.Note(first, dest);
        status.Text = $"Saved {some.Count} page{(some.Count == 1 ? "" : "s")} as {Path.GetFileName(dest)}";
    }

    void SplitAll()
    {
        var folder = Path.Combine(Path.GetDirectoryName(first)!, Path.GetFileNameWithoutExtension(first) + " (pages)");
        for (int i = 2; Directory.Exists(folder); i++) folder = Path.Combine(Path.GetDirectoryName(first)!, $"{Path.GetFileNameWithoutExtension(first)} (pages {i})");
        Directory.CreateDirectory(folder);
        for (int i = 0; i < pages.Count; i++)
            Write(new[] { pages[i] }, Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(first)} - page {i + 1}.pdf"), CurrentPassword());
        Docs.Note(first, folder);
        status.Text = $"{pages.Count} PDFs in {Path.GetFileName(folder)}";
        Launcher.Open(folder);
    }

    /// <summary>
    /// Dev check (`--pdfedit-test`): open a PDF, add another's pages, turn the first page, put text on it, drop the
    /// last page, save with a password, then read the result back and say what's in it. Also a picture of the window.
    /// </summary>
    internal static async Task<string> SelfTest(string pdf, string other, string folder)
    {
        var sb = new System.Text.StringBuilder();
        var w = new PdfEditorWindow(pdf) { Left = -4000, Top = 0, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Show();
        await Task.Delay(1500); // (Loaded adds the first file)
        int firstCount = w.pages.Count;
        await w.AddFile(other, w.pages.Count);
        sb.AppendLine($"pages: {firstCount} + {w.pages.Count - firstCount} = {w.pages.Count}, pictures {w.pages.Count(p => p.Picture != null)}");
        w.selected.Add(w.pages[0]);
        w.Turn(90);
        w.pages[0].Stamps.Add(new Stamp(0.1, 0.2, "BRIM STAMP 42", 18));
        w.selected.Clear();
        w.selected.Add(w.pages[^1]);
        w.Delete();
        (w.pages[0], w.pages[1]) = (w.pages[1], w.pages[0]); // swap the first two
        w.newPassword = "secret";
        w.Refresh();
        await Task.Delay(400);
        var shot = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        shot.Render((Visual)w.Content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(shot));
        using (var fs = File.Create(Path.Combine(folder, "pdf-editor.png"))) enc.Save(fs);
        var dest = Path.Combine(folder, "edited.pdf");
        w.changed = false;
        w.Write(w.pages, dest, w.CurrentPassword());
        try { PdfReader.Open(dest, PdfDocumentOpenMode.InformationOnly); sb.AppendLine("opened WITHOUT the password: wrong"); }
        catch (Exception) { sb.AppendLine("needs the password: yes"); }
        using (var back = PdfReader.Open(dest, "secret", PdfDocumentOpenMode.Import))
            sb.AppendLine($"saved: {back.PageCount} pages, rotations {string.Join(",", Enumerable.Range(0, back.PageCount).Select(i => back.Pages[i].Rotate))}");
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(dest, new UglyToad.PdfPig.ParsingOptions { Password = "secret" }))
        {
            var text = string.Join(" | ", pig.GetPages().Select((p, i) => $"p{i + 1}: " + (p.Text.Length > 40 ? p.Text[..40] : p.Text)));
            sb.AppendLine($"text: {text}");
            sb.AppendLine($"stamp found on page 2 (the turned one, moved there by the swap): {pig.GetPage(2).Text.Contains("BRIM STAMP 42")}");
        }
        w.Close();
        return sb.ToString();
    }

    // ---------------------------------------------------------------- small pieces

    static TextBlock Dim(string t, double size, Thickness margin)
    {
        var tb = new TextBlock { Text = t, FontSize = size, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        return tb;
    }

    /// <summary>A small question window with one box to type in (hidden letters for passwords).</summary>
    string? AskText(string title, string prompt, bool secret = false, string initial = "")
    {
        var w = new Window
        {
            Title = title, Owner = this, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily, Foreground = Brushes.White, ShowInTaskbar = false,
        };
        w.SetResourceReference(BackgroundProperty, "Set.Bg");
        w.SourceInitialized += (_, _) => { int dark = 1; Native.DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref dark, 4); };
        var s = new StackPanel { Margin = new Thickness(20) };
        s.Children.Add(new TextBlock { Text = prompt, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        Control box = secret ? new PasswordBox { MinHeight = 32 } : new TextBox { Text = initial, MinHeight = 32 };
        box.SetResourceReference(StyleProperty, "Field");
        s.Children.Add(box);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        string? result = null;
        var ok = new Button { Content = "OK", IsDefault = true, Height = 32, Padding = new Thickness(18, 0, 18, 0), Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "Pill");
        ok.SetResourceReference(Control.BackgroundProperty, "Brim.Accent");
        ok.SetResourceReference(Control.ForegroundProperty, "Brim.OnAccent");
        ok.Click += (_, _) => { result = box is PasswordBox pb ? pb.Password : ((TextBox)box).Text; w.DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Height = 32, Padding = new Thickness(18, 0, 18, 0) };
        cancel.SetResourceReference(StyleProperty, "Pill");
        cancel.SetResourceReference(Control.BackgroundProperty, "Set.Field");
        cancel.SetResourceReference(Control.ForegroundProperty, "Brim.Text");
        row.Children.Add(ok);
        row.Children.Add(cancel);
        s.Children.Add(row);
        w.Content = s;
        w.Loaded += (_, _) => box.Focus();
        return w.ShowDialog() == true ? result : null;
    }
}
