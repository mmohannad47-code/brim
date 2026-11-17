using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Brim.Core;

/// <summary>A folder found by a drive scan: what's in it, all the way down.</summary>
public sealed class DirNode
{
    public string Name = "";
    public DirNode? Parent;
    public List<DirNode>? Kids;
    /// <summary>Files directly inside it.</summary>
    public long Own;
    /// <summary>Everything inside it, all the way down.</summary>
    public long Size;
    public long Files;
    public long Folders;
    public long ModifiedTicks;
    /// <summary>Windows didn't let Brim look inside (needs admin, or private to another account).</summary>
    public bool Denied;

    public string Path => Parent == null ? Name : System.IO.Path.Combine(Parent.Path, Name);
    public DateTime Modified => ModifiedTicks > 0 ? DateTime.FromFileTime(ModifiedTicks) : DateTime.MinValue;
}

public sealed record BigFile(string Path, long Size, DateTime Modified);

public sealed record FileEntry(string Name, long Size, DateTime Modified);

public sealed class ScanResult
{
    public required DirNode Root;
    public required List<BigFile> Largest;
    public long Denied;
    public TimeSpan Took;
}

/// <summary>
/// Reads a whole drive the fast way (Windows' FindFirstFileEx with large fetches, several folders at once), the
/// way WizTree and TreeSize do. Only folders are kept, with their totals, plus the largest files; a folder's own
/// files are read again when you open it. Junctions and symbolic links aren't followed, so nothing is counted twice,
/// and files that only live in OneDrive (not on this PC) count as nothing.
/// </summary>
public static class StorageScan
{
    const int Keep = 1000; // largest files remembered

    public static Task<ScanResult> Run(string root, IProgress<(long bytes, long files)>? progress, CancellationToken stop) => Task.Run(() =>
    {
        var started = DateTime.Now;
        var top = new PriorityQueue<BigFile, long>();
        long topMin = 0, bytes = 0, files = 0, denied = 0;
        var rootNode = new DirNode { Name = root.EndsWith('\\') ? root : root + "\\" };
        var work = new ConcurrentStack<DirNode>();
        work.Push(rootNode);
        int pending = 1;
        var lastReport = DateTime.Now;

        void Process(DirNode node)
        {
            var path = node.Path;
            var h = FindFirstFileEx(@"\\?\" + System.IO.Path.Combine(path, "*"), 1 /* FindExInfoBasic */, out var d, 0, IntPtr.Zero, 2 /* LARGE_FETCH */);
            if (h == new IntPtr(-1)) { node.Denied = true; Interlocked.Increment(ref denied); return; }
            try
            {
                do
                {
                    var name = d.cFileName;
                    if (name == "." || name == "..") continue;
                    bool dir = (d.dwFileAttributes & 0x10) != 0;
                    long mtime = ((long)d.ftLastWriteTime.dwHighDateTime << 32) | (uint)d.ftLastWriteTime.dwLowDateTime;
                    if (dir)
                    {
                        // Junctions and symbolic links point at something counted elsewhere (or on another drive)
                        if ((d.dwFileAttributes & 0x400) != 0 && (d.dwReserved0 == 0xA0000003 || d.dwReserved0 == 0xA000000C)) continue;
                        var kid = new DirNode { Name = name, Parent = node, ModifiedTicks = mtime };
                        (node.Kids ??= new List<DirNode>()).Add(kid);
                        Interlocked.Increment(ref pending);
                        work.Push(kid);
                    }
                    else
                    {
                        // Only in OneDrive (or another cloud), not on this PC: takes no space here
                        if ((d.dwFileAttributes & (0x400000 | 0x1000)) != 0) continue;
                        long size = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
                        node.Own += size;
                        node.Files++;
                        if (mtime > node.ModifiedTicks) node.ModifiedTicks = mtime;
                        Interlocked.Add(ref bytes, size);
                        Interlocked.Increment(ref files);
                        if (size > Volatile.Read(ref topMin))
                            lock (top)
                            {
                                top.Enqueue(new BigFile(System.IO.Path.Combine(path, name), size, DateTime.FromFileTime(mtime)), size);
                                if (top.Count > Keep) top.Dequeue();
                                if (top.Count == Keep) Volatile.Write(ref topMin, top.Peek().Size);
                            }
                    }
                }
                while (FindNextFile(h, out d));
            }
            finally { FindClose(h); }
        }

        void Worker()
        {
            var spin = new SpinWait();
            while (Volatile.Read(ref pending) > 0 && !stop.IsCancellationRequested)
            {
                if (!work.TryPop(out var node)) { spin.SpinOnce(); continue; }
                spin = new SpinWait();
                try { Process(node); }
                catch { node.Denied = true; }
                Interlocked.Decrement(ref pending);
                if (progress != null && (DateTime.Now - lastReport).TotalMilliseconds > 250)
                {
                    lastReport = DateTime.Now;
                    progress.Report((Interlocked.Read(ref bytes), Interlocked.Read(ref files)));
                }
            }
        }

        var threads = new Thread[Math.Clamp(Environment.ProcessorCount / 2, 2, 6)];
        for (int i = 0; i < threads.Length; i++) { threads[i] = new Thread(Worker) { IsBackground = true, Priority = ThreadPriority.BelowNormal }; threads[i].Start(); }
        foreach (var t in threads) t.Join();
        stop.ThrowIfCancellationRequested();

        Total(rootNode);
        var largest = new List<BigFile>(top.Count);
        while (top.Count > 0) largest.Add(top.Dequeue());
        largest.Reverse();
        return new ScanResult { Root = rootNode, Largest = largest, Denied = denied, Took = DateTime.Now - started };
    }, stop);

    /// <summary>Add up every folder from the bottom (without recursion: folders can nest very deep).</summary>
    static void Total(DirNode root)
    {
        var order = new List<DirNode>();
        var stack = new Stack<DirNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            order.Add(n);
            if (n.Kids != null) foreach (var k in n.Kids) stack.Push(k);
        }
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            n.Size += n.Own;
            if (n.Parent != null)
            {
                n.Parent.Size += n.Size;
                n.Parent.Files += n.Files;
                n.Parent.Folders += n.Folders + 1;
                if (n.ModifiedTicks > n.Parent.ModifiedTicks) n.Parent.ModifiedTicks = n.ModifiedTicks;
            }
            n.Kids?.TrimExcess();
        }
    }

    /// <summary>A folder's own files (not its folders), read now.</summary>
    public static List<FileEntry> Files(string folder)
    {
        var list = new List<FileEntry>();
        var h = FindFirstFileEx(@"\\?\" + System.IO.Path.Combine(folder, "*"), 1, out var d, 0, IntPtr.Zero, 2);
        if (h == new IntPtr(-1)) return list;
        try
        {
            do
            {
                if ((d.dwFileAttributes & 0x10) != 0) continue;
                if ((d.dwFileAttributes & (0x400000 | 0x1000)) != 0) continue;
                long mtime = ((long)d.ftLastWriteTime.dwHighDateTime << 32) | (uint)d.ftLastWriteTime.dwLowDateTime;
                list.Add(new FileEntry(d.cFileName, ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow, DateTime.FromFileTime(mtime)));
            }
            while (FindNextFile(h, out d));
        }
        finally { FindClose(h); }
        return list;
    }

    /// <summary>Take a removed file or folder's size off every folder above it.</summary>
    public static void Subtract(DirNode? from, long size, long files)
    {
        for (var n = from; n != null; n = n.Parent) { n.Size -= size; n.Files -= files; }
    }

    public static string Size(long b) =>
        b >= 1L << 40 ? $"{b / (double)(1L << 40):0.00} TB" :
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" :
        b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" :
        b >= 1L << 10 ? $"{b / 1024.0:0} KB" : $"{b} bytes";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileEx(string name, int infoLevel, out WIN32_FIND_DATA data, int searchOp, IntPtr filter, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool FindNextFile(IntPtr h, out WIN32_FIND_DATA data);
    [DllImport("kernel32.dll")]
    static extern bool FindClose(IntPtr h);
}
