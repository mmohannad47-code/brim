using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;

namespace Brim.Core;

/// <summary>
/// PDF to an editable Word document, done by Brim itself (no Office needed). It reads each page's text blocks in
/// reading order with their size and weight (bigger text becomes headings, centred text stays centred), keeps the
/// pictures where they sit, and starts each PDF page on a new page. Layout is simpler than the PDF's (no exact
/// columns or tables), which is what you want when the point is to edit the words.
/// </summary>
public static class PdfToWord
{
    /// <summary>A stretch of text in one look: font, size, weight, slant and colour (null = automatic, i.e. black).</summary>
    sealed class Run
    {
        public StringBuilder Text = new();
        public string? Font; public double Size; public bool Bold, Italic; public string? Colour;
        public bool Same(Run o) => Font == o.Font && Math.Abs(Size - o.Size) < 0.6 && Bold == o.Bold && Italic == o.Italic && Colour == o.Colour;
    }

    /// <summary>A paragraph: its runs, where it sits (PDF points, y up), how it's lined up, and its line pitch.</summary>
    sealed record Para(double Top, double Bottom, double Left, double Right, List<Run> Runs, string? Align, double Pitch, double Size);
    sealed record Pic(double Top, int Index, double WidthPt, double HeightPt, double Left);

    /// <summary>
    /// <paramref name="keepLayout"/>: every block of text, picture, line and coloured box goes exactly where it is
    /// on the PDF's page (tables and columns look the same; text is still editable). Otherwise the text flows as
    /// ordinary paragraphs (easier to rewrite, but side-by-side parts end up one under the other).
    /// </summary>
    public static void Convert(string pdf, string docx, bool keepLayout, string? password = null)
    {
        if (keepLayout) { ConvertLayout(pdf, docx, password); return; }
        ConvertFlow(pdf, docx, password);
    }

    // ================================================================ keep the layout

    static void ConvertLayout(string pdf, string docx, string? password)
    {
        using var doc = password == null ? PdfDocument.Open(pdf) : PdfDocument.Open(pdf, new ParsingOptions { Password = password });
        var pages = doc.GetPages().ToList();
        double pageW = pages.Count > 0 ? pages[0].Width : 612, pageH = pages.Count > 0 ? pages[0].Height : 792;
        var images = new List<(byte[] data, string ext)>();
        var body = new StringBuilder();
        int id = 1;

        for (int pi = 0; pi < pages.Count; pi++)
        {
            var page = pages[pi];
            double ph = page.Height;
            var drawings = new StringBuilder();
            int z = 1;

            // Lines and filled boxes (table rules, cell shading, coloured bars), behind everything
            int shapes = 0;
            try
            {
                foreach (var path in page.Paths)
                {
                    if (path.IsClipping || (!path.IsFilled && !path.IsStroked)) continue;
                    string? fill = path.IsFilled ? Hex(path.FillColor) ?? "000000" : null;
                    string? stroke = path.IsStroked ? Hex(path.StrokeColor) ?? "000000" : null;
                    double lw = Math.Max(0.5, path.LineWidth);
                    foreach (var sp in path)
                    {
                        if (shapes > 900) break;
                        if (sp.GetBoundingRectangle() is not { } r) continue;
                        if (r.Width < 0.2 && r.Height < 0.2) continue;
                        if (r.Width > page.Width * 0.97 && r.Height > ph * 0.97) continue; // the page's own background
                        if (fill == "FFFFFF" && stroke == null) continue;                  // white on white adds nothing
                        bool thin = r.Width < 2.5 || r.Height < 2.5;
                        if (thin)
                        {
                            var c = stroke ?? fill;
                            if (c == null || c == "FFFFFF") continue;
                            double w = Math.Max(r.Width, r.Width < 2.5 ? lw : 0), h = Math.Max(r.Height, r.Height < 2.5 ? lw : 0);
                            drawings.Append(Box(ref id, z++, r.Left - (w - r.Width) / 2, ph - r.Top - (h - r.Height) / 2, w, h, c, null, 0));
                        }
                        else if (fill != null) drawings.Append(Box(ref id, z++, r.Left, ph - r.Top, r.Width, r.Height, fill, stroke, stroke != null ? lw : 0));
                        else if (stroke != null && sp.IsDrawnAsRectangle) drawings.Append(Box(ref id, z++, r.Left, ph - r.Top, r.Width, r.Height, null, stroke, lw));
                        else if (stroke != null)
                        {
                            // Other outlines: their straight across-and-down pieces (most table borders)
                            foreach (var cmd in sp.Commands.OfType<UglyToad.PdfPig.Core.PdfSubpath.Line>())
                            {
                                double x1 = Math.Min(cmd.From.X, cmd.To.X), x2 = Math.Max(cmd.From.X, cmd.To.X), y1 = Math.Min(cmd.From.Y, cmd.To.Y), y2 = Math.Max(cmd.From.Y, cmd.To.Y);
                                if (x2 - x1 < 0.5 && y2 - y1 < 0.5) continue;
                                if (x2 - x1 > 0.5 && y2 - y1 > 0.5) continue; // slanted: skip
                                double w = Math.Max(x2 - x1, lw), h = Math.Max(y2 - y1, lw);
                                drawings.Append(Box(ref id, z++, x1 - (w - (x2 - x1)) / 2, ph - y2 - (h - (y2 - y1)) / 2, w, h, stroke, null, 0));
                                shapes++;
                            }
                        }
                        shapes++;
                    }
                }
            }
            catch { }

            // Pictures, where they are
            foreach (var img in page.GetImages())
            {
                try
                {
                    var r = img.Bounds;
                    if (r.Width < 4 || r.Height < 4) continue;
                    byte[]? data = null; string ext = "png";
                    if (img.TryGetPng(out var png)) data = png;
                    else if (img.RawBytes.Length > 3 && img.RawBytes[0] == 0xFF && img.RawBytes[1] == 0xD8) { data = img.RawBytes.ToArray(); ext = "jpeg"; }
                    if (data == null) continue;
                    images.Add((data, ext));
                    drawings.Append(Picture(ref id, 2000 + z++, images.Count, r.Left, ph - r.Top, r.Width, r.Height));
                }
                catch { }
            }

            // An all-but-invisible paragraph to hold this page's drawings
            body.Append("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/><w:rPr><w:sz w:val=\"2\"/></w:rPr></w:pPr>");
            body.Append(drawings);
            body.Append("</w:p>");

            // Text: each block in a frame at its place, its lines kept as they are
            var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            if (words.Count > 0)
            {
                var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                foreach (var b in UnsupervisedReadingOrderDetector.Instance.Get(blocks).OrderBy(b => b.ReadingOrder))
                {
                    // Pieces of text on the same baseline are one line on the page ("1." and "Introduction …… 1"
                    // are separate pieces in the PDF): kept on one line, each piece at its own place with a tab stop
                    var rows = new List<List<UglyToad.PdfPig.DocumentLayoutAnalysis.TextLine>>();
                    foreach (var tl in b.TextLines.OrderByDescending(Base))
                    {
                        double sz = tl.Words.SelectMany(w => w.Letters).Select(l => l.PointSize).DefaultIfEmpty(10).Max();
                        var row = rows.LastOrDefault();
                        if (row != null && Math.Abs(Base(row[0]) - Base(tl)) < sz * 0.35) row.Add(tl);
                        else rows.Add(new() { tl });
                    }
                    foreach (var row in rows) row.Sort((p, q) => p.BoundingBox.Left.CompareTo(q.BoundingBox.Left));
                    var lines = new List<List<(double left, List<Run> runs)>>();
                    var lineBases = new List<double>();
                    foreach (var row in rows)
                    {
                        var pieces = new List<(double, List<Run>)>();
                        foreach (var tl in row)
                        {
                            var runs = new List<Run>();
                            foreach (var w in tl.Words)
                            {
                                var look = LookOf(w);
                                if (look == null) continue;
                                if (runs.Count > 0 && runs[^1].Same(look)) runs[^1].Text.Append(' ').Append(Sym(w.Text, w.FontName));
                                else
                                {
                                    if (runs.Count > 0) runs[^1].Text.Append(' ');
                                    look.Text.Append(Sym(w.Text, w.FontName));
                                    runs.Add(look);
                                }
                            }
                            if (runs.Count > 0) pieces.Add((tl.BoundingBox.Left, runs));
                        }
                        if (pieces.Count > 0) { lines.Add(pieces); lineBases.Add(Base(row[0])); }
                    }
                    if (lines.Count == 0) continue;
                    var box = b.BoundingBox;
                    double size = lines.SelectMany(l => l).SelectMany(p => p.runs).Max(r => r.Size);
                    double pitch = size * 1.18;
                    if (rows.Count >= 2)
                        pitch = Math.Max(size * 0.9, Math.Abs(Base(rows[0][0]) - Base(rows[^1][0])) / (rows.Count - 1));
                    // Lined up how? (all lines ending at the same place but starting at different ones: right; both moving: centred)
                    string? align = null;
                    if (rows.Count >= 2 && rows.All(r => r.Count == 1))
                    {
                        var ls = rows.Select(r => r[0].BoundingBox.Left).ToList();
                        var rs = rows.Select(r => r[^1].BoundingBox.Right).ToList();
                        bool sameLeft = ls.Max() - ls.Min() < 2, sameRight = rs.Max() - rs.Min() < 2;
                        if (!sameLeft && sameRight) align = "right";
                        else if (!sameLeft && !sameRight && Math.Abs((ls.Max() - ls.Min()) - (rs.Max() - rs.Min())) < 4) align = "center";
                    }
                    // A little wider than in the PDF (fonts here may run a touch wider), widened the way it's lined up
                    double extra = box.Width * 0.06 + 6;
                    double x = align == "right" ? box.Left - extra : align == "center" ? box.Left - extra / 2 : box.Left;
                    x = Math.Max(0, x);
                    double width = Math.Min(box.Width + extra, page.Width - x);
                    // The frame's top: the first line's baseline, less the part of the line above a baseline
                    double y = ph - Base(rows[0][0]) - pitch * 0.8;
                    // Each line its own paragraph in the same frame (Word keeps paragraphs with the same frame together):
                    // so each can start where it does in the PDF (its indent), have its later pieces at their places
                    // (tab stops), and sit at its own height (the gap above it: the usual line pitch plus any extra)
                    var gaps = new List<double>();
                    for (int li = 1; li < lineBases.Count; li++) gaps.Add(lineBases[li - 1] - lineBases[li]);
                    double lineH = gaps.Count == 0 ? pitch : Math.Max(size * 0.9, gaps.OrderBy(g => g).ElementAt(gaps.Count / 2) * 0.98);
                    if (gaps.Count > 0) lineH = Math.Min(lineH, gaps.Min()); // never more than the tightest gap, or lines drift down
                    y = ph - lineBases[0] - lineH * 0.8;
                    string frame = $"<w:framePr w:w=\"{Tw(width)}\" w:hAnchor=\"page\" w:vAnchor=\"page\" w:x=\"{Tw(x)}\" w:y=\"{Tw(Math.Max(0, y))}\" w:wrap=\"through\"/>";
                    for (int li = 0; li < lines.Count; li++)
                    {
                        var pieces = lines[li];
                        double indent = align == null ? Math.Max(0, pieces[0].left - x) : 0;
                        double before = li == 0 ? 0 : Math.Max(0, lineBases[li - 1] - lineBases[li] - lineH);
                        body.Append("<w:p><w:pPr>").Append(frame);
                        if (pieces.Count > 1)
                            body.Append("<w:tabs>" + string.Concat(pieces.Skip(1).Select(p => Tw(p.left - x)).Where(t => t > 0).Distinct().Take(60).Select(t => $"<w:tab w:val=\"left\" w:pos=\"{t}\"/>")) + "</w:tabs>");
                        body.Append($"<w:spacing w:before=\"{Tw(before)}\" w:after=\"0\" w:line=\"{Tw(lineH)}\" w:lineRule=\"exact\"/>");
                        if (indent > 0.5) body.Append($"<w:ind w:left=\"{Tw(indent)}\"/>");
                        if (align != null) body.Append($"<w:jc w:val=\"{align}\"/>");
                        body.Append("</w:pPr>");
                        for (int pi2 = 0; pi2 < pieces.Count; pi2++)
                        {
                            if (pi2 > 0) body.Append("<w:r><w:tab/></w:r>");
                            foreach (var r in pieces[pi2].runs) body.Append(RunXml(r, false));
                        }
                        body.Append("</w:p>");
                    }
                }
            }
            if (pi < pages.Count - 1) body.Append("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\"/></w:pPr><w:r><w:br w:type=\"page\"/></w:r></w:p>");
        }
        Write(docx, body.ToString(), images, pageW, pageH, 18, 18, 18, 18);
    }

    static double Base(UglyToad.PdfPig.DocumentLayoutAnalysis.TextLine l)
    {
        var first = l.Words.FirstOrDefault()?.Letters.FirstOrDefault();
        return first != null ? first.StartBaseLine.Y : l.BoundingBox.Bottom;
    }

    static int Tw(double pt) => (int)Math.Round(pt * 20);
    static long Emu(double pt) => (long)Math.Round(pt * 12700);

    static string? Hex(UglyToad.PdfPig.Graphics.Colors.IColor? c)
    {
        if (c == null) return null;
        try
        {
            var (r, g, b) = c.ToRGBValues();
            return $"{(int)Math.Round(Math.Clamp(r, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(g, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(b, 0, 1) * 255):X2}";
        }
        catch { return null; }
    }

    static string Anchor(int id, int z, double x, double y, double w, double h, string graphic) =>
        $"<w:r><w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"{z}\" behindDoc=\"1\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
        "<wp:simplePos x=\"0\" y=\"0\"/>" +
        $"<wp:positionH relativeFrom=\"page\"><wp:posOffset>{Emu(x)}</wp:posOffset></wp:positionH>" +
        $"<wp:positionV relativeFrom=\"page\"><wp:posOffset>{Emu(y)}</wp:posOffset></wp:positionV>" +
        $"<wp:extent cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/>" +
        $"<wp:docPr id=\"{id}\" name=\"Shape {id}\"/><wp:cNvGraphicFramePr/>" + graphic + "</wp:anchor></w:drawing></w:r>";

    /// <summary>A rectangle at (x, y) from the page's top left, in points: filled, outlined, or both.</summary>
    static string Box(ref int id, int z, double x, double y, double w, double h, string? fill, string? line, double lineWidth)
    {
        string fillXml = fill != null ? $"<a:solidFill><a:srgbClr val=\"{fill}\"/></a:solidFill>" : "<a:noFill/>";
        string lineXml = line != null && lineWidth > 0 ? $"<a:ln w=\"{Emu(lineWidth)}\"><a:solidFill><a:srgbClr val=\"{line}\"/></a:solidFill></a:ln>" : "<a:ln><a:noFill/></a:ln>";
        var g = "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvSpPr/>" +
                $"<wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>{fillXml}{lineXml}</wps:spPr>" +
                "<wps:bodyPr/></wps:wsp></a:graphicData></a:graphic>";
        return Anchor(id++, z, x, y, Math.Max(0.1, w), Math.Max(0.1, h), g);
    }

    static string Picture(ref int id, int z, int index, double x, double y, double w, double h)
    {
        var g = "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
                $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"image{index}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                $"<pic:blipFill><a:blip r:embed=\"rIdImg{index}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
                "</pic:pic></a:graphicData></a:graphic>";
        return Anchor(id++, z, x, y, w, h, g);
    }

    // ================================================================ flowing text

    static void ConvertFlow(string pdf, string docx, string? password)
    {
        using var doc = password == null ? PdfDocument.Open(pdf) : PdfDocument.Open(pdf, new ParsingOptions { Password = password });
        var pages = doc.GetPages().ToList();
        // The body text size: the one most letters use (headings are noticeably bigger than it)
        var sizes = pages.Take(12).SelectMany(p => p.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).Select(l => Math.Round(l.PointSize * 2) / 2).ToList();
        double body = sizes.Count == 0 ? 11 : sizes.GroupBy(s => s).OrderByDescending(g => g.Count()).First().Key;

        var images = new List<(byte[] data, string ext)>();
        var bodyXml = new StringBuilder();
        double pageW = pages.Count > 0 ? pages[0].Width : 612, pageH = pages.Count > 0 ? pages[0].Height : 792;
        var perPage = new List<List<object>>();
        var lefts = new List<double>(); var rights = new List<double>(); var tops = new List<double>(); var bottoms = new List<double>();

        for (int pi = 0; pi < pages.Count; pi++)
        {
            var page = pages[pi];
            var items = new List<object>();
            var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            if (words.Count > 0)
            {
                var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks).OrderBy(b => b.ReadingOrder).ToList();
                foreach (var b in ordered)
                {
                    var runs = new List<Run>();
                    foreach (var line in b.TextLines)
                    {
                        bool firstWord = true;
                        foreach (var w in line.Words)
                        {
                            var look = LookOf(w);
                            if (look == null) continue;
                            // Between lines a space (or nothing, joining a word broken with a hyphen); between words a space
                            string gap = "";
                            if (runs.Count > 0)
                            {
                                var last = runs[^1].Text;
                                if (firstWord && last.Length > 1 && last[^1] == '-' && char.IsLetter(last[^2])) last.Length--;
                                else gap = " ";
                            }
                            if (runs.Count > 0 && runs[^1].Same(look)) runs[^1].Text.Append(gap).Append(Sym(w.Text, w.FontName));
                            else
                            {
                                if (gap.Length > 0) runs[^1].Text.Append(gap);
                                look.Text.Append(Sym(w.Text, w.FontName));
                                runs.Add(look);
                            }
                            firstWord = false;
                        }
                    }
                    if (runs.Count == 0) continue;
                    var box = b.BoundingBox;
                    // Lined up how? Centred on the page, against the right, or filling the width (justified)
                    double mid = (box.Left + box.Right) / 2;
                    string? align = null;
                    if (box.Width < page.Width * 0.75 && Math.Abs(mid - page.Width / 2) < page.Width * 0.03 && b.TextLines.Count <= 4) align = "center";
                    else if (b.TextLines.Count >= 3 && b.TextLines.Take(b.TextLines.Count - 1).All(l => l.BoundingBox.Width > box.Width * 0.94)) align = "both";
                    double pitch = 0;
                    if (b.TextLines.Count >= 2)
                    {
                        var bases = b.TextLines.Select(l => l.BoundingBox.Bottom).ToList();
                        pitch = Math.Abs(bases[0] - bases[^1]) / (bases.Count - 1);
                    }
                    double size = runs.Max(r => r.Size);
                    items.Add(new Para(box.Top, box.Bottom, box.Left, box.Right, runs, align, pitch, size));
                    lefts.Add(box.Left); rights.Add(box.Right); tops.Add(box.Top); bottoms.Add(box.Bottom);
                }
            }
            foreach (var img in page.GetImages())
            {
                try
                {
                    var r = img.Bounds;
                    if (r.Width < 24 || r.Height < 24) continue; // specks and rules, not pictures
                    byte[]? data = null; string ext = "png";
                    if (img.TryGetPng(out var png)) data = png;
                    else if (img.RawBytes.Length > 3 && img.RawBytes[0] == 0xFF && img.RawBytes[1] == 0xD8) { data = img.RawBytes.ToArray(); ext = "jpeg"; }
                    if (data == null) continue;
                    images.Add((data, ext));
                    double w = Math.Min(r.Width, pageW - 72), h = r.Height * (w / r.Width);
                    items.Add(new Pic(r.Top, images.Count, w, h, r.Left));
                }
                catch { }
            }
            perPage.Add(items);
        }

        // The page margins: where the text usually starts and ends (a few stray bits near the edge don't count)
        double Pct(List<double> v, double p) { if (v.Count == 0) return double.NaN; v.Sort(); return v[(int)Math.Clamp(Math.Round(p * (v.Count - 1)), 0, v.Count - 1)]; }
        double mLeft = Math.Clamp(double.IsNaN(Pct(lefts, 0.05)) ? 72 : Pct(lefts, 0.05), 18, pageW / 3);
        double mRight = Math.Clamp(double.IsNaN(Pct(rights, 0.95)) ? 72 : pageW - Pct(rights, 0.95), 18, pageW / 3);
        double mTop = Math.Clamp(double.IsNaN(Pct(tops, 0.97)) ? 72 : pageH - Pct(tops, 0.97), 18, pageH / 4);
        double mBottom = Math.Clamp(double.IsNaN(Pct(bottoms, 0.03)) ? 72 : Pct(bottoms, 0.03), 18, pageH / 4);

        for (int pi = 0; pi < perPage.Count; pi++)
        {
            var items = perPage[pi];
            // Pictures go in where they sit on the page, among the paragraphs (reading order kept for the text)
            var flow = new List<object>(items.OfType<Para>());
            foreach (var pic in items.OfType<Pic>().OrderByDescending(p => p.Top))
            {
                int at = flow.FindIndex(o => o is Para p && p.Top < pic.Top - 1);
                if (at < 0) flow.Add(pic); else flow.Insert(at, pic);
            }
            double? above = pi == 0 ? null : (double?)null;
            foreach (var o in flow)
            {
                if (o is Para p)
                {
                    // The space above it: the gap down from the thing before (capped, so a big jump doesn't push a page out)
                    double before = above is double a ? Math.Clamp(a - p.Top, 0, 40) : 0;
                    bodyXml.Append(ParaXml(p, body, before, mLeft));
                    above = p.Bottom;
                }
                else if (o is Pic pic)
                {
                    bodyXml.Append(PicXml(pic, pic.Left - mLeft));
                    above = pic.Top - pic.HeightPt;
                }
            }
            if (pi < perPage.Count - 1) bodyXml.Append("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\"/></w:pPr><w:r><w:br w:type=\"page\"/></w:r></w:p>");
        }

        Write(docx, bodyXml.ToString(), images, pageW, pageH, mLeft, mRight, mTop, mBottom);
    }

    /// <summary>How a word looks (from most of its letters), or null for a word with nothing visible in it.</summary>
    static Run? LookOf(Word w)
    {
        var letters = w.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        if (letters.Count == 0) return null;
        var font = letters.GroupBy(l => l.FontName ?? "").OrderByDescending(g => g.Count()).First().Key;
        string? colour = null;
        try
        {
            var c = letters[0].FillColor ?? letters[0].Color;
            if (c != null)
            {
                var (r, g, b) = c.ToRGBValues();
                // Near black stays "automatic", so the text follows Word's own colour (dark mode, high contrast)
                if (r > 0.12 || g > 0.12 || b > 0.12) colour = $"{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
            }
        }
        catch { }
        return new Run
        {
            // Symbol only for its own private letters: real characters (•, α) drawn in it come out as boxes
            Font = PictureFont(font) || IsSymbolFont(font) && !w.Text.Any(c => c >= 0xF020 && c <= 0xF07E) ? null : FontFamilyOf(font),
            Size = Math.Round(letters.Average(l => l.PointSize) * 2) / 2,
            Bold = Weight(font, "bold", "black", "heavy", "semibold", "demi"),
            Italic = Weight(font, "italic", "oblique"),
            Colour = colour,
        };
    }

    /// <summary>
    /// Bullets and ticks from the Symbol and Wingdings fonts come out of PDFs as private codes (U+F0B7 and so on) that
    /// other fonts draw as boxes or odd foreign-looking letters: turned into the real characters.
    /// </summary>
    static string Sym(string s, string? font)
    {
        if (!s.Any(c => c >= '\uF000' && c <= '\uF0FF')) return s;
        bool symbol = font != null && font.Contains("Symbol", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c switch
            {
                '\uF0B7' or '\uF06C' or '\uF09F' or '\uF0A1' => '•', // bullet
                '\uF0A7' or '\uF06E' or '\uF0A8' => '▪',             // small square
                '\uF0D8' or '\uF0E0' or '\uF0E8' => '➢',             // arrowhead
                '\uF0FC' or '\uF0FE' => '✓',                         // tick
                '\uF0FB' or '\uF0FD' => '✗',                         // cross
                '\uF071' or '\uF06F' or '\uF070' => '□',             // empty box
                '\uF076' => '❖',                                     // diamond of four
                '\uF0AE' or '\uF0DE' => '→',                         // right arrow
                '\uF0AC' => '←',                                     // left arrow
                '\uF02D' => '–',                                     // dash
                >= '\uF020' and <= '\uF07E' when symbol => (char)(c - 0xF000), // Symbol's letters (Greek in that font, which the text keeps)
                >= '\uF000' and <= '\uF0FF' => '•',                  // anything else from those fonts: a bullet
                _ => c,
            });
        return sb.ToString();
    }

    /// <summary>Fonts made of pictures (their symbols are turned into real characters, see <see cref="Sym"/>).</summary>
    static bool IsSymbolFont(string? f) => f != null && f.Contains("Symbol", StringComparison.OrdinalIgnoreCase);
    static bool PictureFont(string? f) => f != null && (f.Contains("Wingdings", StringComparison.OrdinalIgnoreCase) || f.Contains("Webdings", StringComparison.OrdinalIgnoreCase));

    static bool Weight(string? font, params string[] words) => font != null && words.Any(w => font.Contains(w, StringComparison.OrdinalIgnoreCase));

    static HashSet<string>? installed;
    static readonly Dictionary<string, string?> fontNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The font a PDF names ("ABCDEF+Calibri-Bold", "ArialMT", "TimesNewRomanPSMT", "SegoeUI") as Word knows it
    /// ("Calibri", "Arial", "Times New Roman", "Segoe UI"), when it's installed on this PC; otherwise null (Word's default).
    /// </summary>
    static string? FontFamilyOf(string pdfName)
    {
        if (fontNames.TryGetValue(pdfName, out var known)) return known;
        installed ??= new HashSet<string>(System.Windows.Media.Fonts.SystemFontFamilies.SelectMany(f => f.FamilyNames.Values), StringComparer.OrdinalIgnoreCase);
        var n = pdfName;
        int plus = n.IndexOf('+');
        if (plus >= 0 && plus <= 7) n = n[(plus + 1)..];      // the subset tag
        n = n.Split(',', '-', '_')[0];                          // ",Bold", "-Italic"
        foreach (var tail in new[] { "PSMT", "MT", "PS", "Std", "Pro" }) if (n.EndsWith(tail, StringComparison.Ordinal) && n.Length > tail.Length + 2) n = n[..^tail.Length];
        n = n switch { "Helvetica" => "Arial", "Times" or "TimesRoman" => "Times New Roman", "Courier" => "Courier New", _ => n };
        // "TimesNewRoman" → "Times New Roman", "SegoeUI" → "Segoe UI"
        var spaced = System.Text.RegularExpressions.Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ");
        string? result = installed.Contains(spaced) ? spaced : installed.Contains(n) ? n : null;
        fontNames[pdfName] = result;
        return result;
    }

    static string ParaXml(Para p, double body, double before, double marginLeft)
    {
        string text = string.Concat(p.Runs.Select(r => r.Text.ToString()));
        bool heading = p.Size >= body * 1.25 && text.Length < 200;
        var ppr = new StringBuilder("<w:pPr>");
        if (heading) ppr.Append(p.Size >= body * 1.6 ? "<w:pStyle w:val=\"Heading1\"/>" : "<w:pStyle w:val=\"Heading2\"/>");
        // Spacing: the gap above, nothing after (the next one has its own gap), and the PDF's line pitch
        ppr.Append($"<w:spacing w:before=\"{(int)Math.Round(before * 20)}\" w:after=\"0\"");
        if (p.Pitch > 4) ppr.Append($" w:line=\"{(int)Math.Round(p.Pitch * 20)}\" w:lineRule=\"atLeast\"");
        ppr.Append("/>");
        double indent = p.Left - marginLeft;
        if (p.Align == null && indent > 4) ppr.Append($"<w:ind w:left=\"{(int)Math.Round(indent * 20)}\"/>");
        if (p.Align != null) ppr.Append($"<w:jc w:val=\"{p.Align}\"/>");
        ppr.Append("</w:pPr>");
        var runs = new StringBuilder();
        foreach (var r in p.Runs) runs.Append(RunXml(r, heading));
        return $"<w:p>{ppr}{runs}</w:p>";
    }

    /// <summary>One run of text in its font, size, weight, slant and colour.</summary>
    static string RunXml(Run r, bool bold)
    {
        int half = (int)Math.Round(Math.Clamp(r.Size, 4, 96) * 2);
        var x = new StringBuilder("<w:r><w:rPr>");
        if (r.Font != null) x.Append($"<w:rFonts w:ascii=\"{Esc(r.Font)}\" w:hAnsi=\"{Esc(r.Font)}\" w:cs=\"{Esc(r.Font)}\" w:eastAsia=\"{Esc(r.Font)}\"/>");
        if (r.Bold || bold) x.Append("<w:b/><w:bCs/>");
        if (r.Italic) x.Append("<w:i/><w:iCs/>");
        if (r.Colour != null) x.Append($"<w:color w:val=\"{r.Colour}\"/>");
        x.Append($"<w:sz w:val=\"{half}\"/><w:szCs w:val=\"{half}\"/></w:rPr>");
        x.Append($"<w:t xml:space=\"preserve\">{Esc(r.Text.ToString())}</w:t></w:r>");
        return x.ToString();
    }

    static string PicXml(Pic pic, double indent)
    {
        long cx = (long)(pic.WidthPt * 12700), cy = (long)(pic.HeightPt * 12700);
        var ind = indent > 4 ? $"<w:pPr><w:spacing w:before=\"120\" w:after=\"120\"/><w:ind w:left=\"{(int)Math.Round(indent * 20)}\"/></w:pPr>" : "<w:pPr><w:spacing w:before=\"120\" w:after=\"120\"/></w:pPr>";
        return $"<w:p>{ind}<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/>" +
               $"<wp:docPr id=\"{pic.Index}\" name=\"Picture {pic.Index}\"/><wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>" +
               "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
               $"<pic:nvPicPr><pic:cNvPr id=\"{pic.Index}\" name=\"image{pic.Index}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
               $"<pic:blipFill><a:blip r:embed=\"rIdImg{pic.Index}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
               $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
               "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
    }

    /// <summary>Text made safe for XML (and without the control characters XML can't hold).</summary>
    static string Esc(string s)
    {
        var clean = new StringBuilder(s.Length);
        foreach (char c in s) if (c == '\t' || c >= 0x20 && c != 0xFFFE && c != 0xFFFF) clean.Append(c);
        return SecurityElement.Escape(clean.ToString()) ?? "";
    }

    static void Write(string path, string body, List<(byte[] data, string ext)> images, double pageW, double pageH, double mLeft, double mRight, double mTop, double mBottom)
    {
        var tmp = path + ".brimpart";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Part(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            Part("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>");
            Part("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            for (int i = 0; i < images.Count; i++)
            {
                var name = $"media/image{i + 1}.{images[i].ext}";
                using (var s = zip.CreateEntry("word/" + name, CompressionLevel.NoCompression).Open()) s.Write(images[i].data);
                rels.Append($"<Relationship Id=\"rIdImg{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"{name}\"/>");
            }
            rels.Append("</Relationships>");
            Part("word/_rels/document.xml.rels", rels.ToString());
            Part("word/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\" w:eastAsia=\"Calibri\"/><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault>" +
                "<w:pPrDefault><w:pPr><w:spacing w:after=\"140\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"120\"/><w:outlineLvl w:val=\"0\"/></w:pPr><w:rPr><w:b/></w:rPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:pPr><w:keepNext/><w:spacing w:before=\"200\" w:after=\"100\"/><w:outlineLvl w:val=\"1\"/></w:pPr><w:rPr><w:b/></w:rPr></w:style>" +
                "</w:styles>");
            int w = (int)Math.Round(pageW * 20), h = (int)Math.Round(pageH * 20);
            Part("word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"wps\"><w:body>" + body +
                $"<w:sectPr><w:pgSz w:w=\"{w}\" w:h=\"{h}\"/><w:pgMar w:top=\"{(int)(mTop * 20)}\" w:right=\"{(int)(mRight * 20)}\" w:bottom=\"{(int)(mBottom * 20)}\" w:left=\"{(int)(mLeft * 20)}\" w:header=\"360\" w:footer=\"360\" w:gutter=\"0\"/></w:sectPr>" +
                "</w:body></w:document>");
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
