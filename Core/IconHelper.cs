using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Brim.Core;

public static class IconHelper
{
    static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);
    static Native.IImageList? jumbo;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    /// <summary>
    /// Icon of anything the shell knows by name, including Start menu apps that aren't files
    /// ("shell:AppsFolder\&lt;app id&gt;", e.g. Store apps), with proper transparency.
    /// </summary>
    public static ImageSource? ForShellItem(string parsingName, int size = 96)
    {
        var key = $"shellitem|{size}|{parsingName}";
        if (cache.TryGetValue(key, out var hit)) return hit;
        ImageSource? result = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) == 0 &&
                // Exactly this size (icon only), drawn by the shell from the app's best icon: sharp when shown smaller
                factory.GetImage(new SIZE { cx = size, cy = size }, 0x4 /* icon only */, out hbm) == 0 &&
                GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bmp) != 0 && bmp.bmBits != IntPtr.Zero && bmp.bmBitsPixel == 32)
            {
                int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight), stride = bmp.bmWidthBytes;
                var pixels = new byte[stride * h];
                Marshal.Copy(bmp.bmBits, pixels, 0, pixels.Length);
                if (bmp.bmHeight > 0)
                {
                    // Bottom-up rows: flip
                    var flipped = new byte[pixels.Length];
                    for (int y = 0; y < h; y++) Buffer.BlockCopy(pixels, (h - 1 - y) * stride, flipped, y * stride, stride);
                    pixels = flipped;
                }
                var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
                src.Freeze();
                result = src;
            }
        }
        catch { }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
        if (cache.Count > 600) cache.Clear();
        cache[key] = result;
        return result;
    }

    public static bool IsUrl(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shell icon for any file, folder or shortcut (no shortcut arrow), at 96 px so it stays sharp on high-DPI screens
    /// (the 48 px system icon was stretched and looked soft).
    /// </summary>
    public static ImageSource? ForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsUrl(path)) return null;
        // Store apps saved as "shell:AppsFolder\<app id>"
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return ForShellItem(path);
        if (cache.TryGetValue(path, out var hit)) return hit;
        ImageSource? result = null;
        // Ask for the big one first (for a shortcut: the program it points to, so there's no shortcut arrow)
        var source = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(path) : path;
        if (source != null && (File.Exists(source) || Directory.Exists(source)))
        {
            result = ForShellItem(source, 96);
            if (result != null) { cache[path] = result; return result; }
        }
        try
        {
            var info = new Native.SHFILEINFO();
            const uint SHGFI_SYSICONINDEX = 0x4000;
            if (Native.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_SYSICONINDEX) != IntPtr.Zero)
            {
                if (jumbo == null)
                {
                    var iid = typeof(Native.IImageList).GUID;
                    Native.SHGetImageList(2 /* SHIL_EXTRALARGE, 48px */, ref iid, out jumbo);
                }
                if (jumbo != null && jumbo.GetIcon(info.iIcon, 1 /* ILD_TRANSPARENT */, out IntPtr hIcon) == 0 && hIcon != IntPtr.Zero)
                    result = FromHIcon(hIcon, true);
            }
        }
        catch (Exception ex) { Log.Error("icon " + path, ex); }
        cache[path] = result;
        return result;
    }

    public static ImageSource? FromHIcon(IntPtr hIcon, bool destroy)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (destroy) Native.DestroyIcon(hIcon); }
    }

    /// <summary>Icon a window advertises itself (used for packaged apps hosted by ApplicationFrameHost).</summary>
    public static ImageSource? ForWindow(IntPtr hwnd)
    {
        Native.SendMessageTimeout(hwnd, Native.WM_GETICON, new IntPtr(1), IntPtr.Zero, 2, 100, out IntPtr h);
        if (h == IntPtr.Zero) Native.SendMessageTimeout(hwnd, Native.WM_GETICON, new IntPtr(2), IntPtr.Zero, 2, 100, out h);
        if (h == IntPtr.Zero) h = Native.GetClassLongPtr(hwnd, -14 /* GCLP_HICON */);
        return h == IntPtr.Zero ? null : FromHIcon(h, false);
    }

    /// <summary>The main colour of an icon (ignoring transparent and grey pixels), for tinted backgrounds.</summary>
    public static Color? Tint(BitmapSource src)
    {
        try
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] < 128) continue;
                double B = px[i], G = px[i + 1], R = px[i + 2];
                double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                double weight = 0.08 + (max == 0 ? 0 : (max - min) / max);
                r += R * weight; g += G * weight; b += B * weight; total += weight;
            }
            if (total <= 0) return null;
            return Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total));
        }
        catch { return null; }
    }

    public static string? ResolveShortcut(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic lnk = shell.CreateShortcut(path);
            string target = lnk.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }

    public static string NiceName(string path)
    {
        if (IsUrl(path))
        {
            try { return new Uri(path).Host.Replace("www.", ""); } catch { return path; }
        }
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileNameWithoutExtension(trimmed);
        if (string.IsNullOrEmpty(name)) name = trimmed;
        if (File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(fvi.FileDescription)) return fvi.FileDescription!;
            }
            catch { }
        }
        return name;
    }
}
