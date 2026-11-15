using System;
using System.Runtime.InteropServices;

namespace Brim.Core;

/// <summary>
/// Keeps Brim light: says where its memory goes (a `[memory]` line in brim.log every half hour), and lets go of
/// cached pictures (icons, game covers) once Brim has been quiet for a while. Nothing on screen is touched: those
/// pictures stay alive through what shows them, and the rest load again in a few ms when next needed.
/// </summary>
public static class MemoryCare
{
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage,
            PrivateWorkingSetSize, SharedCommitUsage;
    }

    [DllImport("kernel32.dll")] static extern bool K32GetProcessMemoryInfo(IntPtr proc, ref PROCESS_MEMORY_COUNTERS_EX2 c, uint cb);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    /// <summary>Brim's own memory (the "Memory" column in Task Manager), everything it touches including the parts
    /// of Windows, .NET and the graphics driver shared with other apps, and .NET's objects, in MB.</summary>
    public static (double own, double total, double committed, double objects) Now()
    {
        var c = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
        K32GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.cb);
        const double MB = 1024 * 1024;
        return (c.PrivateWorkingSetSize.ToUInt64() / MB, c.WorkingSetSize.ToUInt64() / MB, c.PrivateUsage.ToUInt64() / MB,
                GC.GetGCMemoryInfo().HeapSizeBytes / MB);
    }

    public static string Describe()
    {
        var (own, total, committed, objects) = Now();
        // (committed: everything Brim holds, in RAM or set aside; it doesn't jump around when Windows trims)
        return $"own {own:0.0} MB (Task Manager), with shared Windows parts {total:0.0} MB, committed {committed:0.0} MB, .NET objects {objects:0.0} MB; " +
               $"cached: {IconHelper.CacheCount} icons, {GameLibrary.CoverCount} game covers, {ClipboardHistory.Items.Count} clips";
    }

    /// <summary>Forget the cached pictures nothing is showing. Returns how many were let go.</summary>
    public static int Release() =>
        IconHelper.ReleaseCache() + GameLibrary.ReleaseCovers() + Brim.Views.SearchWindow.ReleaseIcons();
}
