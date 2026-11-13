using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Brim.Core;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>Master volume, mute, microphone mute, the output level (for the visualiser) and output-device switching.</summary>
public static class Audio
{
    static IAudioEndpointVolume? volume, micVolume;
    static IAudioMeterInformation? meter;
    static DateTime acquired, micAcquired;

    static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

    static void Ensure()
    {
        if (volume != null && (DateTime.UtcNow - acquired).TotalSeconds < 5) return;
        try
        {
            if (Enumerator().GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var dev) != 0 || dev == null)
            { volume = null; meter = null; return; }
            volume = Activate<IAudioEndpointVolume>(dev);
            meter = Activate<IAudioMeterInformation>(dev);
        }
        catch { volume = null; meter = null; }
        acquired = DateTime.UtcNow;
    }

    static void EnsureMic()
    {
        if (micVolume != null && (DateTime.UtcNow - micAcquired).TotalSeconds < 5) return;
        try
        {
            micVolume = Enumerator().GetDefaultAudioEndpoint(1 /* capture */, 0 /* console */, out var dev) == 0 && dev != null
                ? Activate<IAudioEndpointVolume>(dev)
                : null;
        }
        catch { micVolume = null; }
        micAcquired = DateTime.UtcNow;
    }

    static T? Activate<T>(IMMDevice dev) where T : class
    {
        Guid iid = typeof(T).GUID;
        dev.Activate(ref iid, 23, IntPtr.Zero, out object o);
        return o as T;
    }

    public static float Volume
    {
        get { Ensure(); float v = 0; try { volume?.GetMasterVolumeLevelScalar(out v); } catch { } return v; }
        set { Ensure(); var g = Guid.Empty; try { volume?.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref g); } catch { } }
    }

    public static bool Muted
    {
        get { Ensure(); bool m = false; try { volume?.GetMute(out m); } catch { } return m; }
        set { Ensure(); var g = Guid.Empty; try { volume?.SetMute(value, ref g); } catch { } }
    }

    public static bool HasMic { get { EnsureMic(); return micVolume != null; } }

    public static bool MicMuted
    {
        get { EnsureMic(); bool m = false; try { micVolume?.GetMute(out m); } catch { } return m; }
        set { EnsureMic(); var g = Guid.Empty; try { micVolume?.SetMute(value, ref g); } catch { } }
    }

    public static float Peak
    {
        get { Ensure(); float p = 0; try { meter?.GetPeakValue(out p); } catch { } return p; }
    }

    public static List<AudioDevice> Outputs()
    {
        var list = new List<AudioDevice>();
        try
        {
            var en = Enumerator();
            string? defId = null;
            if (en.GetDefaultAudioEndpoint(0, 1, out var def) == 0 && def != null) def.GetId(out defId);
            if (en.EnumAudioEndpoints(0, 1 /* active */, out var coll) != 0 || coll == null) return list;
            coll.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (coll.Item(i, out var dev) != 0 || dev == null) continue;
                dev.GetId(out string id);
                list.Add(new AudioDevice(id, FriendlyName(dev) ?? "Speakers", id == defId));
            }
        }
        catch (Exception ex) { Log.Error("audio devices", ex); }
        return list;
    }

    static string? FriendlyName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var store) != 0 || store == null) return null;
        var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.p) : null; }
        finally { PropVariantClear(ref pv); }
    }

    /// <summary>Name of the current default output (e.g. "Headphones (WH-1000XM5)").</summary>
    public static string? DefaultOutputName()
    {
        try
        {
            if (Enumerator().GetDefaultAudioEndpoint(0, 1, out var dev) != 0 || dev == null) return null;
            return FriendlyName(dev);
        }
        catch { return null; }
    }

    /// <summary>Switch the default output (all three roles), the same call the Windows sound panel makes.</summary>
    public static void SetDefaultOutput(string id)
    {
        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role);
            acquired = default;
        }
        catch (Exception ex) { Log.Error("set output", ex); }
    }

    // ---------- per-app volume (the same sessions Windows' volume mixer shows) ----------

    public sealed class AppVolume
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string? ExePath { get; init; }
        internal List<ISimpleAudioVolume> Controls { get; } = new();

        public float Volume
        {
            get { float v = 0; try { Controls.FirstOrDefault()?.GetMasterVolume(out v); } catch { } return v; }
            set { var g = Guid.Empty; foreach (var c in Controls) try { c.SetMasterVolume(Math.Clamp(value, 0f, 1f), ref g); } catch { } }
        }

        public bool Muted
        {
            get { bool m = false; try { Controls.FirstOrDefault()?.GetMute(out m); } catch { } return m; }
            set { var g = Guid.Empty; foreach (var c in Controls) try { c.SetMute(value, ref g); } catch { } }
        }
    }

    /// <summary>Every app currently able to play sound on the default output, grouped by app.</summary>
    public static List<AppVolume> Apps()
    {
        var byKey = new Dictionary<string, AppVolume>();
        try
        {
            if (Enumerator().GetDefaultAudioEndpoint(0, 1, out var dev) != 0 || dev == null) return new();
            var mgr = Activate<IAudioSessionManager2>(dev);
            if (mgr == null || mgr.GetSessionEnumerator(out var sessions) != 0 || sessions == null) return new();
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out object raw) != 0) continue;
                if (raw is not IAudioSessionControl2 ctl || raw is not ISimpleAudioVolume vol) continue;
                ctl.GetState(out int state);
                if (state == 2) continue; // expired
                bool system = ctl.IsSystemSoundsSession() == 0;
                ctl.GetProcessId(out uint pid);
                string key, name;
                string? path = null;
                if (system) { key = "system"; name = "System sounds"; }
                else
                {
                    path = Native.ProcessPath(pid);
                    key = path?.ToLowerInvariant() ?? "pid:" + pid;
                    name = path != null ? IconHelper.NiceName(path) : "App " + pid;
                    if (ctl.GetDisplayName(out var dn) == 0 && !string.IsNullOrWhiteSpace(dn) && !dn.StartsWith("@")) name = dn;
                }
                if (!byKey.TryGetValue(key, out var app))
                {
                    app = new AppVolume { Key = key, Name = name, ExePath = path };
                    byKey[key] = app;
                }
                app.Controls.Add(vol);
            }
        }
        catch (Exception ex) { Log.Error("audio apps", ex); }
        return byKey.Values.OrderBy(a => a.Key == "system").ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName(IntPtr a, IntPtr b);
        [PreserveSig] int GetIconPath(IntPtr a);
        [PreserveSig] int SetIconPath(IntPtr a, IntPtr b);
        [PreserveSig] int GetGroupingParam(IntPtr a);
        [PreserveSig] int SetGroupingParam(IntPtr a, IntPtr b);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr a);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr a);
        [PreserveSig] int GetSessionIdentifier(IntPtr a);
        [PreserveSig] int GetSessionInstanceIdentifier(IntPtr a);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt; ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
