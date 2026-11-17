using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brim.Core;

namespace Brim.Views;

/// <summary>
/// The notch's Docs tab: Word → PDF, PDF → Word and Edit PDF. Drop a file on a tile (or click it to pick one); the
/// result goes next to the original and into the Recent list, where you can open it or drag it anywhere.
/// </summary>
sealed class DocsView : Grid
{
    readonly Action keepOpen, letGo;
    readonly StackPanel recent = new();
    readonly TextBlock note = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Button clear;
    Border? toPdf, toWord, edit;

    public DocsView(Action keepOpen, Action letGo)
    {
        this.keepOpen = keepOpen;
        this.letGo = letGo;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        // Below the tiles, one card: the note, how PDF → Word comes out, and the recent files
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        var card = Cards.Make(body, new Thickness(12, 8, 8, 8));
        card.Margin = new Thickness(0, 10, 0, 0);
        SetRow(card, 1);
        Children.Add(card);

        var tiles = new Grid();
        for (int i = 0; i < 5; i++) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(10) : new GridLength(1, GridUnitType.Star) });
        toPdf = Tile("\uEA90", "Word → PDF", "Word, Excel, PowerPoint or text", f => !Docs.IsPdf(f) && Docs.CanMakePdf(f), Docs.ToPdf,
            "Documents|*.doc;*.docx;*.rtf;*.odt;*.txt;*.xls;*.xlsx;*.csv;*.ods;*.ppt;*.pptx;*.odp|All files|*.*");
        toWord = Tile("\uE8A5", "PDF → Word", "Get an editable .docx", Docs.IsPdf, Docs.ToWord, "PDF|*.pdf");
        edit = Tile("\uE70F", "Edit PDF", "Reorder, rotate, delete, merge, split, add text, password", Docs.IsPdf, f =>
        {
            PdfEditorWindow.Open(f);
            return System.Threading.Tasks.Task.FromResult("");
        }, "PDF|*.pdf");
        tiles.Children.Add(toPdf);
        Grid.SetColumn(toWord, 2);
        tiles.Children.Add(toWord);
        Grid.SetColumn(edit, 4);
        tiles.Children.Add(edit);
        Children.Add(tiles);

        note.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        // The note, and how PDF → Word comes out: the same layout (each part where it is), or flowing text
        var noteRow = new Grid();
        noteRow.ColumnDefinitions.Add(new ColumnDefinition());
        noteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        noteRow.Children.Add(note);
        var mode = new Button { Height = 24, Padding = new Thickness(10, 0, 10, 0), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        mode.SetResourceReference(StyleProperty, "Pill");
        mode.SetResourceReference(Control.BackgroundProperty, "Brim.Hover");
        mode.SetResourceReference(Control.ForegroundProperty, "Brim.Text");
        void ShowMode()
        {
            bool flow = SettingsStore.Current.PdfToWordFlow;
            mode.Content = flow ? "PDF → Word: flowing text" : "PDF → Word: keep layout";
            mode.ToolTip = flow
                ? "Text comes out as ordinary paragraphs: easiest to rewrite, but columns and tables end up one under the other. Click for: keep layout"
                : "Every part stays where it is on the page (tables, columns, pictures, colours): looks like the PDF, text still editable. Click for: flowing text";
        }
        mode.Click += (_, _) => { SettingsStore.Current.PdfToWordFlow = !SettingsStore.Current.PdfToWordFlow; SettingsStore.Save(); ShowMode(); };
        ShowMode();
        Grid.SetColumn(mode, 1);
        noteRow.Children.Add(mode);
        body.Children.Add(noteRow);

        var head = new Grid { Margin = new Thickness(2, 8, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = "RECENT", FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        head.Children.Add(label);
        clear = new Button { Content = "Clear", Height = 24, Padding = new Thickness(10, 0, 10, 0), FontSize = 11.5 };
        clear.SetResourceReference(StyleProperty, "Pill");
        clear.SetResourceReference(Control.BackgroundProperty, "Brim.Hover");
        clear.SetResourceReference(Control.ForegroundProperty, "Brim.Text");
        clear.Click += (_, _) => { Docs.Recent.Clear(); Fill(); };
        Grid.SetColumn(clear, 1);
        head.Children.Add(clear);
        Grid.SetRow(head, 1);
        body.Children.Add(head);

        var scroll = new ScrollViewer { Content = recent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        body.Children.Add(scroll);

        Docs.Changed += () => Dispatcher.BeginInvoke(new Action(Fill));
        Loaded += (_, _) => Fill();
    }

    void Fill()
    {
        var engine = Docs.Engine;
        note.Text = (engine != null ? $"Word → PDF with {engine}" : "Word → PDF needs Microsoft Office or the free LibreOffice")
                    + " · nothing is uploaded · results go next to the original";
        recent.Children.Clear();
        clear.Visibility = Docs.Recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Docs.Recent.Count == 0)
        {
            var t = new TextBlock { Text = "Converted and edited files show up here. Drag them anywhere.", FontSize = 11.5, Margin = new Thickness(2, 2, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
            recent.Children.Add(t);
            return;
        }
        foreach (var d in Docs.Recent.Where(d => File.Exists(d.Result)).Take(12)) recent.Children.Add(Row(d));
    }

    Border Row(Docs.Done d)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = IconHelper.ForPath(d.Result);
        if (icon != null) g.Children.Add(new Image { Source = icon, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Path.GetFileName(d.Result), FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var when = new TextBlock { Text = $"from {Path.GetExtension(d.Source).TrimStart('.').ToUpperInvariant()} · {d.At:HH:mm}", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        when.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        Grid.SetColumn(when, 2);
        g.Children.Add(when);
        var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 4), Child = g, Cursor = Cursors.Hand, ToolTip = d.Result + "\nClick to open · drag it anywhere" };
        b.SetResourceReference(Border.BackgroundProperty, "Brim.Hover");
        Point? down = null;
        b.MouseLeftButtonDown += (_, e) => down = e.GetPosition(b);
        b.MouseMove += (_, e) =>
        {
            if (down is not Point p || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(b) - p).Length < 6) return;
            down = null;
            keepOpen();
            try { DragDrop.DoDragDrop(b, new DataObject(DataFormats.FileDrop, new[] { d.Result }), DragDropEffects.Copy | DragDropEffects.Move); }
            finally { letGo(); }
        };
        b.MouseLeftButtonUp += (_, _) => { if (down != null) { down = null; Launcher.Open(d.Result); } };
        return b;
    }

    /// <summary>A big drop target: drop files of the right kind on it, or click it to pick some.</summary>
    Border Tile(string glyph, string title, string sub, Func<string, bool> accepts, Func<string, System.Threading.Tasks.Task<string>> run, string filter)
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = new TextBlock { Text = glyph, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = (FontFamily)Application.Current.FindResource("Brim.Icons") };
        var t = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 2) };
        var st = new TextBlock { Text = sub, FontSize = 10.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 150, HorizontalAlignment = HorizontalAlignment.Center };
        st.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        s.Children.Add(icon); s.Children.Add(t); s.Children.Add(st);
        var b = new Border { CornerRadius = new CornerRadius(16), Height = 104, Child = s, AllowDrop = true, Cursor = Cursors.Hand, BorderThickness = new Thickness(1.5) };
        b.SetResourceReference(Border.BackgroundProperty, "Brim.Raised");
        b.BorderBrush = Brushes.Transparent;

        async void Go(string[] files)
        {
            var ok = files.Where(accepts).ToList();
            if (ok.Count == 0) { Flash(st, "That kind of file doesn't go here", sub); return; }
            foreach (var f in ok)
            {
                st.Text = $"Working on {Path.GetFileName(f)}…";
                try { await run(f); }
                catch (Exception ex) { Log.Error("docs", ex); Flash(st, ex.Message, sub, 4); return; }
            }
            st.Text = sub;
        }
        b.DragEnter += (_, e) => { bool ok = e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Any(accepts); e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None; if (ok) b.SetResourceReference(Border.BorderBrushProperty, "Brim.Accent"); e.Handled = true; };
        b.DragOver += (_, e) => { e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Any(accepts) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        b.DragLeave += (_, _) => b.BorderBrush = Brushes.Transparent;
        b.Drop += (_, e) => { b.BorderBrush = Brushes.Transparent; if (e.Data.GetData(DataFormats.FileDrop) is string[] f) Go(f); e.Handled = true; };
        b.MouseLeftButtonUp += (_, _) =>
        {
            keepOpen();
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, Multiselect = true };
                if (dlg.ShowDialog() == true) Go(dlg.FileNames);
            }
            finally { letGo(); }
        };
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Brim.Hover");
        b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Brim.Raised");
        return b;
    }

    static void Flash(TextBlock t, string text, string back, double seconds = 2.5)
    {
        t.Text = text;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); if (t.Text == text) t.Text = back; };
        timer.Start();
    }
}
