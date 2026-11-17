using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace Brim.Core;

public static class SystemInfo
{
    static long lastIdle, lastKernel, lastUser;

    public static double Cpu { get; private set; }
    public static double Ram { get; private set; }
    public static double RamUsedGb { get; private set; }
    public static double RamTotalGb { get; private set; }
    public static bool HasBattery { get; private set; }
    public static int BatteryPercent { get; private set; }
    public static bool Charging { get; private set; }

    public static void Sample()
    {
        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long di = idle - lastIdle, dk = kernel - lastKernel, du = user - lastUser;
            long total = dk + du;
            if (lastKernel != 0 && total > 0) Cpu = Math.Clamp((total - di) * 100.0 / total, 0, 100);
            lastIdle = idle; lastKernel = kernel; lastUser = user;
        }

        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref m))
        {
            RamTotalGb = m.ullTotalPhys / 1073741824.0;
            RamUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            Ram = m.ullTotalPhys == 0 ? 0 : RamUsedGb / RamTotalGb * 100;
        }

        if (Native.GetSystemPowerStatus(out var p))
        {
            HasBattery = (p.BatteryFlag & 128) == 0 && p.BatteryFlag != 255 && p.BatteryLifePercent <= 100;
            BatteryPercent = p.BatteryLifePercent <= 100 ? p.BatteryLifePercent : 0;
            Charging = p.ACLineStatus == 1;
        }
    }
}

/// <summary>
/// The last minute of CPU, memory and network readings (one a second), for the graphs in the notch's System tab.
/// Network speed is the bytes the PC's connected adapters moved since the last reading.
/// </summary>
public static class SystemHistory
{
    public const int Length = 60;
    public static readonly double[] Cpu = new double[Length], Ram = new double[Length], Down = new double[Length], Up = new double[Length];
    static int filled;
    public static int Filled => filled;

    static System.Net.NetworkInformation.NetworkInterface[] nics = Array.Empty<System.Net.NetworkInformation.NetworkInterface>();
    static DateTime nicsAt;
    static long lastIn = -1, lastOut = -1;
    static DateTime lastAt;

    /// <summary>Bytes a second, now.</summary>
    public static double DownNow => Down[Length - 1];
    public static double UpNow => Up[Length - 1];

    static void Push(double[] a, double v) { Array.Copy(a, 1, a, 0, Length - 1); a[Length - 1] = v; }

    /// <summary>Called once a second (after <see cref="SystemInfo.Sample"/>).</summary>
    public static void Sample()
    {
        Push(Cpu, SystemInfo.Cpu);
        Push(Ram, SystemInfo.Ram);
        double down = 0, up = 0;
        try
        {
            if (DateTime.Now - nicsAt > TimeSpan.FromSeconds(30))
            {
                nicsAt = DateTime.Now;
                nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                                n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                    .ToArray();
                lastIn = lastOut = -1; // a different set of adapters: start counting afresh
            }
            long bin = 0, bout = 0;
            foreach (var n in nics)
            {
                try { var st = n.GetIPStatistics(); bin += st.BytesReceived; bout += st.BytesSent; } catch { }
            }
            var now = DateTime.Now;
            double secs = Math.Max(0.2, (now - lastAt).TotalSeconds);
            if (lastIn >= 0 && bin >= lastIn && bout >= lastOut) { down = (bin - lastIn) / secs; up = (bout - lastOut) / secs; }
            lastIn = bin; lastOut = bout; lastAt = now;
        }
        catch { }
        Push(Down, down);
        Push(Up, up);
        if (filled < Length) filled++;
    }

    /// <summary>"8.4 MB/s", "120 KB/s".</summary>
    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond >= 1_000_000 ? $"{bytesPerSecond / 1_000_000:0.0} MB/s" :
        bytesPerSecond >= 1_000 ? $"{bytesPerSecond / 1_000:0} KB/s" : $"{bytesPerSecond:0} B/s";
}
