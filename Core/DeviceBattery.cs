using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Enumeration;

namespace Brim.Core;

/// <summary>Battery is -1 when the device doesn't report it.</summary>
public sealed record BtDevice(string Id, string Name, int Battery, bool Connected, string Glyph, bool Charging = false)
{
    public string BatteryText => Battery < 0 ? "Connected" : Charging ? $"{Battery}%, charging" : $"{Battery}%";
}

/// <summary>
/// Battery levels of Bluetooth headphones, mice, keyboards and controllers, as Windows reports them in Settings.
/// Raises events when one connects or runs low so the notch can show it.
/// </summary>
public static class DeviceBattery
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string ConnectedKey = "{83DA6326-97A6-4088-9453-A1923F573B29} 15"; // DEVPKEY_Device_IsConnected (Bluetooth)
    const string PresentKey = "System.Devices.Present";
    const string BluetoothClass = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";

    static readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(60) };
    static readonly Dictionary<string, BtDevice> known = new();
    static readonly HashSet<string> warned = new();
    static bool started, busy;

    public static IReadOnlyList<BtDevice> Devices { get; private set; } = Array.Empty<BtDevice>();
    public static event Action<BtDevice>? Connected;
    public static event Action<BtDevice>? Low;
    public static event Action? Changed;

    public static void Start()
    {
        if (started) return;
        started = true;
        timer.Tick += async (_, _) => await Refresh();
        timer.Start();
        _ = Refresh(first: true);
    }

    public static async Task Refresh(bool first = false)
    {
        if (busy) return;
        busy = true;
        lastRefresh = DateTime.Now;
        try
        {
            var found = await Task.Run(async () =>
            {
                var list = new List<BtDevice>();
                var aqs = $"System.Devices.ClassGuid:=\"{BluetoothClass}\"";
                var all = await DeviceInformation.FindAllAsync(aqs, new[] { BatteryKey, ConnectedKey, PresentKey }, DeviceInformationKind.Device);
                foreach (var d in all)
                {
                    if (string.IsNullOrWhiteSpace(d.Name)) continue;
                    bool connected = d.Properties.TryGetValue(ConnectedKey, out var c) && c is bool cb ? cb
                                   : d.Properties.TryGetValue(PresentKey, out var p) && p is bool pb && pb;
                    bool hasBattery = d.Properties.TryGetValue(BatteryKey, out var b) && b != null;
                    // Controllers count even without a battery reading: you still want to see they're connected
                    if (!hasBattery && !(connected && IsController(d.Name))) continue;
                    list.Add(new BtDevice(d.Id, Clean(d.Name), hasBattery ? Convert.ToInt32(b) : -1, connected, GlyphFor(d.Name)));
                }

                // PlayStation controllers keep their battery level to themselves; read it from the controller
                foreach (var pad in SonyPads.Read())
                {
                    // Replace the plain Bluetooth entry ("Wireless Controller") with the named, measured one
                    var plain = list.FirstOrDefault(x => x.Connected && x.Battery < 0 && IsController(x.Name));
                    if (plain != null) list.Remove(plain);
                    list.Add(new BtDevice("pad:" + pad.Name, pad.Name, pad.Battery, true, GlyphFor("controller"), pad.Charging));
                }
                // The same headset often shows up twice (hands-free and stereo); keep one
                return list.GroupBy(x => x.Name).Select(g => g.OrderByDescending(x => x.Connected).First()).ToList();
            });

            foreach (var d in found)
            {
                known.TryGetValue(d.Name, out var before);
                if (!first && d.Connected && (before == null || !before.Connected)) Connected?.Invoke(d);
                if (d.Connected && d.Battery >= 0 && d.Battery <= 15 && !d.Charging && warned.Add(d.Name)) Low?.Invoke(d);
                if (d.Battery > 25) warned.Remove(d.Name);
                known[d.Name] = d;
            }
            Devices = found.Where(d => d.Connected).OrderBy(d => d.Name).ToList();
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Error("bt battery", ex); }
        finally { busy = false; }
    }

    public static string ControllerGlyph => GlyphFor("controller");

    static bool IsController(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "(?i)controller|gamepad|xbox|dualsense|dualshock|joy-con|pro controller");

    static DateTime lastRefresh;

    /// <summary>Refresh soon (e.g. when the notch opens), but not more than every few seconds.</summary>
    public static void RefreshIfStale()
    {
        if (DateTime.Now - lastRefresh > TimeSpan.FromSeconds(8)) _ = Refresh();
    }

    static string Clean(string name) => name.Replace(" Hands-Free AG", "").Replace(" Hands-Free", "").Replace(" Avrcp Transport", "").Trim();

    static string GlyphFor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("xbox") || n.Contains("dualsense") || n.Contains("dualshock")) return "";
        if (n.Contains("mouse") || n.Contains("mx ")) return "";
        if (n.Contains("keyboard") || n.Contains("keys")) return "";
        if (n.Contains("watch")) return "";
        return ""; // headphones
    }
}
