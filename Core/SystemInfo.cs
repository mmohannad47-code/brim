using System;
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
