using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Brim.Core;

/// <summary>
/// Reads Windows' own notification database (the one the notification centre uses), read-only, through the SQLite
/// that ships with Windows (winsqlite3.dll). Two things the notification API doesn't give apps:
///   • what clicking a notification does (its "launch" instruction: which chat, which email), so Brim can open
///     the message itself the way Windows does;
///   • app badges: the unread counts Store apps (Teams, WhatsApp, Outlook…) put on their icons.
/// Nothing is written, and nothing leaves the PC.
/// </summary>
static class NotificationStore
{
    static readonly string DbFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_bind_int64(IntPtr stmt, int i, long v);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    static List<string[]> Query(string sql, long? arg = null)
    {
        var rows = new List<string[]>();
        if (!File.Exists(DbFile)) return rows;
        IntPtr db = IntPtr.Zero, st = IntPtr.Zero;
        try
        {
            // Read-only; Windows keeps writing to it meanwhile (it uses a write-ahead log, so readers don't block it)
            if (sqlite3_open_v2(Z("file:" + DbFile.Replace('\\', '/') + "?mode=ro"), out db, 0x01 | 0x40, IntPtr.Zero) != 0) return rows;
            sqlite3_busy_timeout(db, 300);
            if (sqlite3_prepare_v2(db, Z(sql), -1, out st, IntPtr.Zero) != 0) return rows;
            if (arg != null) sqlite3_bind_int64(st, 1, arg.Value);
            while (sqlite3_step(st) == 100 /* ROW */)
            {
                var r = new string[3];
                for (int i = 0; i < r.Length; i++)
                {
                    int len = sqlite3_column_bytes(st, i);
                    var p = sqlite3_column_blob(st, i);
                    if (p == IntPtr.Zero || len <= 0) { r[i] = ""; continue; }
                    var b = new byte[len];
                    Marshal.Copy(p, b, 0, len);
                    r[i] = Encoding.UTF8.GetString(b);
                }
                rows.Add(r);
            }
        }
        catch (DllNotFoundException) { }
        catch (Exception ex) { Log.Error("notification store", ex); }
        finally
        {
            if (st != IntPtr.Zero) sqlite3_finalize(st);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
        return rows;
    }

    // ------------------------------------------------------------------ opening the message itself

    public sealed record Launch(string AppId, string Args, string Activation);

    /// <summary>What clicking this notification does, or null if Windows doesn't have it any more.</summary>
    public static Launch? LaunchFor(uint notificationId)
    {
        var rows = Query("select h.PrimaryId, n.Payload, '' from Notification n join NotificationHandler h on h.RecordId = n.HandlerId where n.Id = ?1 and n.Type = 'toast'", notificationId);
        if (rows.Count == 0) return null;
        var payload = rows[0][1];
        var toast = Regex.Match(payload, "<toast\\b[^>]*>", RegexOptions.IgnoreCase).Value;
        string Attr(string name) => System.Net.WebUtility.HtmlDecode(Regex.Match(toast, name + "=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
        var activation = Attr("activationType");
        return new Launch(rows[0][0], Attr("launch"), activation.Length > 0 ? activation : "foreground");
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ApplicationActivationManager { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFICATION_USER_INPUT_DATA { public string Key; public string Value; }

    [ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INotificationActivationCallback
    {
        void Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
                      [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] NOTIFICATION_USER_INPUT_DATA[]? data, uint count);
    }

    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    /// <summary>
    /// Do what clicking the notification in Windows does: open its link, or hand its "launch" instruction to the app
    /// (so it opens that chat or email). Returns false when it couldn't, so the caller can at least open the app.
    /// </summary>
    public static bool Open(uint notificationId)
    {
        var l = LaunchFor(notificationId);
        if (l == null) return false;
        try
        {
            if (l.Activation.Equals("protocol", StringComparison.OrdinalIgnoreCase) && l.Args.Length > 0)
            {
                Launcher.Open(l.Args);
                return true;
            }
            // Desktop apps register a "notification activator"; Windows calls it with the launch instruction
            if (ActivatorFor(l.AppId) is { } clsid)
            {
                var iid = typeof(INotificationActivationCallback).GUID;
                if (CoCreateInstance(ref clsid, IntPtr.Zero, 0x4 /* CLSCTX_LOCAL_SERVER */, ref iid, out var obj) == 0 && obj is INotificationActivationCallback cb)
                {
                    cb.Activate(l.AppId, l.Args, null, 0);
                    return true;
                }
            }
            // Store apps: Windows' own "activate for this notification" (it's how the notification centre does it)
            if (l.AppId.Contains('!'))
            {
                var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
                // ActivateForToast isn't in the interface above (it lives on a newer one); opening the app with the
                // launch instruction as its arguments is what toast activation hands to a Store app
                mgr.ActivateApplication(l.AppId, l.Args, 0, out _);
                return true;
            }
        }
        catch (Exception ex) { Log.Error("open notification", ex); }
        return false;
    }

    static readonly Dictionary<string, Guid?> activators = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The COM class an app registered to receive its notification clicks, wherever this PC keeps it: the registry
    /// (desktop apps), the app's package manifest (Store apps like Teams and Outlook), or its Start menu shortcut.
    /// </summary>
    static Guid? ActivatorFor(string appId)
    {
        if (activators.TryGetValue(appId, out var hit)) return hit;
        Guid? found = null;
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = root.OpenSubKey(@"Software\Classes\AppUserModelId\" + appId);
                if (k?.GetValue("CustomActivator") is string s && Guid.TryParse(s, out var g)) { found = g; break; }
            }
            catch { }
        }
        if (found == null && appId.Contains('!'))
        {
            try
            {
                var family = appId[..appId.IndexOf('!')];
                foreach (var pkg in new Windows.Management.Deployment.PackageManager().FindPackagesForUser("", family))
                {
                    var manifest = Path.Combine(pkg.InstalledLocation.Path, "AppxManifest.xml");
                    var m = Regex.Match(File.ReadAllText(manifest), "ToastActivatorCLSID=\"\\{?([0-9a-fA-F-]{36})\\}?\"");
                    if (m.Success && Guid.TryParse(m.Groups[1].Value, out var g)) { found = g; break; }
                }
            }
            catch { }
        }
        if (found == null)
        {
            // A desktop app's Start menu shortcut can carry it (Shell objects want a single-threaded apartment)
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                    dynamic? item = shell.NameSpace("shell:AppsFolder").ParseName(appId);
                    if (item?.ExtendedProperty("System.AppUserModel.ToastActivatorCLSID") is string s && Guid.TryParse(s, out Guid g) && g != Guid.Empty) found = g;
                }
                catch { }
            }) { IsBackground = true };
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            t.Join(2000);
        }
        activators[appId] = found;
        return found;
    }

    // ------------------------------------------------------------------ badges (unread counts on app icons)

    static DateTime lastWrite;
    static Dictionary<string, int> lastBadges = new();

    /// <summary>App id → the number on its badge (or 1 for a dot), read only when the database changed.</summary>
    public static Dictionary<string, int>? Badges()
    {
        try
        {
            var w1 = File.GetLastWriteTimeUtc(DbFile);
            var w2 = File.Exists(DbFile + "-wal") ? File.GetLastWriteTimeUtc(DbFile + "-wal") : default;
            var w = w1 > w2 ? w1 : w2;
            if (w == lastWrite) return null; // nothing new
            lastWrite = w;
        }
        catch { return null; }
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Query("select h.PrimaryId, n.Payload, '' from Notification n join NotificationHandler h on h.RecordId = n.HandlerId where n.Type = 'badge'"))
        {
            var v = Regex.Match(r[1], "value=\"([^\"]*)\"").Groups[1].Value;
            int n = int.TryParse(v, out var num) ? num : v is "none" or "" ? 0 : 1; // glyphs like "newMessage" count as one
            if (n > 0) result[r[0]] = n;
        }
        lastBadges = result;
        return result;
    }
}
