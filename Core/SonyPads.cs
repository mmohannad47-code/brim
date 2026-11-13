using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Brim.Core;

public sealed record PadReading(string Name, int Battery, bool Charging, bool Wireless);

/// <summary>
/// PlayStation controllers (DualShock 4, DualSense) report their battery inside their own input data rather than
/// to Windows, so Windows' Bluetooth page never shows it. This opens the controller the way Steam does (shared,
/// read-only use) and reads one report.
/// </summary>
public static class SonyPads
{
    const ushort Sony = 0x054C;
    static readonly Dictionary<ushort, string> Models = new()
    {
        [0x05C4] = "DualShock 4", [0x09CC] = "DualShock 4", [0x0BA0] = "DualShock 4",
        [0x0CE6] = "DualSense", [0x0DF2] = "DualSense Edge",
    };

    /// <summary>What the last read saw (for the --pad-test check).</summary>
    public static string Debug = "";
    public static bool DebugOn;

    public static List<PadReading> Read()
    {
        var list = new List<PadReading>();
        foreach (var path in HidPaths())
        {
            try { if (ReadOne(path) is { } r) list.Add(r); }
            catch (Exception ex) { Log.Error("pad read", ex); }
        }
        return list;
    }

    static PadReading? ReadOne(string path)
    {
        using var h = Open(path, true) ?? Open(path, false);
        if (h == null) return null;
        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attr) || attr.VendorID != Sony || !Models.TryGetValue(attr.ProductID, out var name)) return null;
        if (!HidD_GetPreparsedData(h, out var pre)) return null;
        HIDP_CAPS caps;
        try { if (HidP_GetCaps(pre, out caps) != 0x00110000 /* HIDP_STATUS_SUCCESS */) return null; }
        finally { HidD_FreePreparsedData(pre); }
        if (caps.UsagePage != 0x01 || caps.Usage is not (0x04 or 0x05)) return null; // the game-controller part only

        bool dualSense = name.StartsWith("DualSense");
        bool wireless = caps.InputReportByteLength > 64;
        using var fs = new FileStream(h, FileAccess.Read, 1, true);
        var buf = new byte[caps.InputReportByteLength];

        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool got = ReadReport(fs, buf);
            if (DebugOn) Debug += $"{name} attempt={attempt} got={got} id=0x{buf[0]:X2} inLen={caps.InputReportByteLength} featLen={caps.FeatureReportByteLength} bytes={BitConverter.ToString(buf, 0, Math.Min(40, buf.Length))}\n";
            if (!got) continue;
            byte id = buf[0];
            int at = (dualSense, id) switch
            {
                (false, 0x01) when !wireless => 30, // DS4 over USB
                (false, 0x11) => 32,                // DS4 over Bluetooth
                (true, 0x01) when !wireless => 53,  // DualSense over USB
                (true, 0x31) => 54,                 // DualSense over Bluetooth
                _ => -1,
            };
            if (at > 0 && at < buf.Length)
            {
                byte b = buf[at];
                if (dualSense)
                {
                    int level = b & 0x0F, status = b >> 4;
                    return new PadReading(name, Math.Min(100, level * 10 + 5), status is 1 or 2, wireless);
                }
                bool cable = (b & 0x10) != 0;
                int pct = Math.Min(100, (b & 0x0F) * 100 / (cable ? 11 : 8));
                return new PadReading(name, pct, cable && pct < 100, wireless);
            }
            // Over Bluetooth the controller starts in a basic mode without battery data; asking for its calibration
            // (what every game and Steam does) switches it to full reports
            if (attempt == 0 && wireless && caps.FeatureReportByteLength > 0)
            {
                var feat = new byte[caps.FeatureReportByteLength];
                feat[0] = dualSense ? (byte)0x05 : (byte)0x02;
                HidD_GetFeature(h, feat, feat.Length);
            }
        }
        return new PadReading(name, -1, false, wireless);
    }

    static bool ReadReport(FileStream fs, byte[] buf)
    {
        using var cts = new CancellationTokenSource(900);
        try { return fs.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult() > 0; }
        catch { return false; }
    }

    static SafeFileHandle? Open(string path, bool write)
    {
        var h = CreateFile(path, write ? 0xC0000000 : 0x80000000, 3 /* share read+write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x40000000 /* OVERLAPPED */, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return h;
    }

    static IEnumerable<string> HidPaths()
    {
        var paths = new List<string>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12 /* PRESENT | DEVICEINTERFACE */);
        if (set == new IntPtr(-1)) return paths;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero))
                    {
                        var p = Marshal.PtrToStringUni(detail + 4);
                        // Only Sony devices: skip opening anything else
                        if (p != null && p.Contains("054c", StringComparison.OrdinalIgnoreCase)) paths.Add(p);
                    }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
