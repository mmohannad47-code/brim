using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Brim.Core;

/// <summary>
/// Converting documents on this PC: Word, Excel and PowerPoint files (and text, RTF, OpenDocument) to PDF, and PDF to
/// an editable Word document. It uses Microsoft Office when it's installed (it does the job in the background,
/// nothing opens on screen), or LibreOffice. Nothing is uploaded; the result goes next to the original.
/// </summary>
public static class Docs
{
    public sealed record Done(string Source, string Result, DateTime At);

    static readonly string[] WordKinds = { ".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf", ".odt", ".txt", ".htm", ".html", ".xml", ".wpd" };
    static readonly string[] ExcelKinds = { ".xls", ".xlsx", ".xlsm", ".xlsb", ".ods", ".csv" };
    static readonly string[] SlideKinds = { ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".odp" };

    /// <summary>Files converted or edited this session, newest first.</summary>
    public static readonly List<Done> Recent = new();
    public static event Action? Changed;

    public static bool CanMakePdf(string path) => Kind(path) != null;
    public static bool IsPdf(string path) => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    static string? Kind(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return WordKinds.Contains(ext) ? "Word" : ExcelKinds.Contains(ext) ? "Excel" : SlideKinds.Contains(ext) ? "PowerPoint" : null;
    }

    static bool Has(string progId) => Type.GetTypeFromProgID(progId) != null;
    public static bool HasWord => Has("Word.Application");
    public static string? LibreOffice
    {
        get
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var p = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    /// <summary>What does the converting here, for the Docs tab's note ("Microsoft Office", "LibreOffice", or null).</summary>
    public static string? Engine => HasWord ? "Microsoft Office" : LibreOffice != null ? "LibreOffice" : null;

    /// <summary>A name next to the original that isn't taken yet: "Report.pdf", "Report (2).pdf", …</summary>
    public static string Beside(string source, string ext)
    {
        var dir = Path.GetDirectoryName(source)!;
        var name = Path.GetFileNameWithoutExtension(source);
        var p = Path.Combine(dir, name + ext);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{name} ({i}){ext}");
        return p;
    }

    public static void Note(string source, string result)
    {
        Recent.Insert(0, new Done(source, result, DateTime.Now));
        if (Recent.Count > 20) Recent.RemoveAt(Recent.Count - 1);
        Changed?.Invoke();
    }

    /// <summary>Word, Excel, PowerPoint, text… to PDF. Returns the new file, or throws with a message to show.</summary>
    public static async Task<string> ToPdf(string source)
    {
        var kind = Kind(source) ?? throw new InvalidOperationException("Brim can't turn this kind of file into a PDF.");
        var dest = Beside(source, ".pdf");
        if (Has(kind + ".Application"))
        {
            await OnSta(() =>
            {
                switch (kind)
                {
                    case "Word":
                        Office("Word.Application", app =>
                        {
                            dynamic doc = app.Documents.Open(source, false, true, false); // (no conversion question, read-only, not in the recent list)
                            try { doc.ExportAsFixedFormat(dest, 17 /* PDF */); }
                            finally { doc.Close(0 /* don't save */); }
                        });
                        break;
                    case "Excel":
                        Office("Excel.Application", app =>
                        {
                            dynamic book = app.Workbooks.Open(source, 0, true);
                            try { book.ExportAsFixedFormat(0 /* PDF */, dest); }
                            finally { book.Close(false); }
                        });
                        break;
                    default:
                        Office("PowerPoint.Application", app =>
                        {
                            dynamic deck = app.Presentations.Open(source, -1 /* read-only */, 0, 0 /* no window */);
                            try { deck.SaveAs(dest, 32 /* PDF */); }
                            finally { deck.Close(); }
                        }, visibleProperty: false);
                        break;
                }
            });
        }
        else if (LibreOffice is string soffice) dest = await Libre(soffice, source, "pdf", dest);
        else throw new InvalidOperationException("Converting needs Microsoft Office or the free LibreOffice on this PC.");
        if (!File.Exists(dest)) throw new InvalidOperationException("The conversion didn't produce a file.");
        Log.Note("docs", $"{Path.GetFileName(source)} → {Path.GetFileName(dest)}");
        Note(source, dest);
        return dest;
    }

    /// <summary>
    /// PDF to an editable Word document, by Brim itself (see <see cref="PdfToWord"/>): no Office needed. (Word can
    /// open PDFs too, but run hidden it was seen never to finish, so it isn't used for this.)
    /// </summary>
    public static async Task<string> ToWord(string pdf)
    {
        var dest = Beside(pdf, ".docx");
        try { await Task.Run(() => PdfToWord.Convert(pdf, dest, keepLayout: !SettingsStore.Current.PdfToWordFlow)); }
        catch (Exception ex) when (ex.GetType().Name.Contains("Password") || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("This PDF has a password. Remove it in Edit PDF first."); }
        if (!File.Exists(dest)) throw new InvalidOperationException("The conversion didn't produce a file.");
        Log.Note("docs", $"{Path.GetFileName(pdf)} → {Path.GetFileName(dest)}");
        Note(pdf, dest);
        return dest;
    }

    /// <summary>Starts the Office app hidden, runs <paramref name="work"/>, and always quits it again.</summary>
    static void Office(string progId, Action<dynamic> work, bool visibleProperty = true)
    {
        var type = Type.GetTypeFromProgID(progId)!;
        // Word and Excel start a fresh copy for us; PowerPoint joins one that's already open, and quitting that
        // would close your presentations, so then it's left running
        bool shared = progId.StartsWith("PowerPoint") && Process.GetProcessesByName("POWERPNT").Length > 0;
        string exe = progId.StartsWith("Word") ? "WINWORD" : progId.StartsWith("Excel") ? "EXCEL" : "POWERPNT";
        var before = Process.GetProcessesByName(exe).Select(p => p.Id).ToHashSet();
        dynamic app = Activator.CreateInstance(type)!;
        // If Office ever hangs (a question it shows to nobody), the copy we started is stopped rather than left behind
        var ours = shared ? null : Process.GetProcessesByName(exe).FirstOrDefault(p => !before.Contains(p.Id));
        using var watchdog = new Timer(_ => { try { if (ours is { HasExited: false }) { Log.Note("docs", $"{exe} didn't finish in time; stopped it"); ours.Kill(); } } catch { } },
                                       null, TimeSpan.FromMinutes(8), Timeout.InfiniteTimeSpan);
        try
        {
            if (visibleProperty) app.Visible = false;
            try { app.DisplayAlerts = 0; } catch { }
            work(app);
        }
        finally
        {
            try { if (!shared) app.Quit(); } catch { }
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(app); } catch { }
        }
    }

    /// <summary>Office wants a single-threaded apartment; run the work on one of its own, off the UI thread.</summary>
    static Task OnSta(Action work)
    {
        var done = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            try { work(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex); }
        }) { IsBackground = true, Name = "Brim docs" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    static async Task<string> Libre(string soffice, string source, string to, string wanted, string extra = "")
    {
        var outDir = Path.Combine(Path.GetTempPath(), "brim-docs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        try
        {
            var psi = new ProcessStartInfo(soffice, $"--headless {extra} --convert-to {to} --outdir \"{outDir}\" \"{source}\"")
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await p.WaitForExitAsync(cts.Token);
            var made = Directory.GetFiles(outDir).FirstOrDefault() ?? throw new InvalidOperationException("LibreOffice didn't produce a file.");
            File.Move(made, wanted);
            return wanted;
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    }
}
