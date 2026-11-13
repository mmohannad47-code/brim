using System;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace Brim.Core;

/// <summary>Wi-Fi and Bluetooth on/off through Windows' radio API.</summary>
public static class Radios
{
    static Radio? wifi, bluetooth;
    static bool loaded;

    public static async Task Load()
    {
        if (loaded) return;
        try
        {
            await Radio.RequestAccessAsync();
            var all = await Radio.GetRadiosAsync();
            wifi = all.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            bluetooth = all.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            loaded = true;
        }
        catch (Exception ex) { Log.Error("radios", ex); }
    }

    public static bool HasWifi => wifi != null;
    public static bool HasBluetooth => bluetooth != null;
    public static bool WifiOn => wifi?.State == RadioState.On;
    public static bool BluetoothOn => bluetooth?.State == RadioState.On;

    public static async Task SetWifi(bool on)
    {
        try { if (wifi != null) await wifi.SetStateAsync(on ? RadioState.On : RadioState.Off); }
        catch (Exception ex) { Log.Error("wifi toggle", ex); }
    }

    public static async Task SetBluetooth(bool on)
    {
        try { if (bluetooth != null) await bluetooth.SetStateAsync(on ? RadioState.On : RadioState.Off); }
        catch (Exception ex) { Log.Error("bluetooth toggle", ex); }
    }
}

public sealed record NetworkState(bool Connected, bool Wireless, string Name, int Bars);

public static class Network
{
    public static NetworkState Current()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null) return new NetworkState(false, false, "Not connected", 0);
            if (p.IsWlanConnectionProfile)
            {
                string ssid = p.WlanConnectionProfileDetails?.GetConnectedSsid() ?? p.ProfileName;
                return new NetworkState(true, true, ssid, p.GetSignalBars() ?? 4);
            }
            return new NetworkState(true, false, p.IsWwanConnectionProfile ? "Mobile" : "Ethernet", 4);
        }
        catch { return new NetworkState(false, false, "Not connected", 0); }
    }
}

/// <summary>Built-in display brightness (laptops) through WMI. Slow, so reads and writes happen off the UI thread.</summary>
public static class Brightness
{
    static bool? available;
    static int pending = -1;
    static int writing;

    public static async Task<int?> Read()
    {
        if (available == false) return null;
        return await Task.Run<int?>(() =>
        {
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (ManagementObject o in s.Get())
                {
                    available = true;
                    return Convert.ToInt32(o["CurrentBrightness"]);
                }
            }
            catch { }
            available = false;
            return null;
        });
    }

    /// <summary>Coalesces slider drags: only the latest value is written.</summary>
    public static void Set(int percent)
    {
        Interlocked.Exchange(ref pending, Math.Clamp(percent, 0, 100));
        if (Interlocked.Exchange(ref writing, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                int v;
                while ((v = Interlocked.Exchange(ref pending, -1)) >= 0)
                {
                    using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                    foreach (ManagementObject o in s.Get())
                        o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v });
                }
            }
            catch (Exception ex) { Log.Error("brightness", ex); }
            finally { Interlocked.Exchange(ref writing, 0); }
        });
    }
}
