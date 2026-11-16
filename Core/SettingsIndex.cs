using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Brim.Core;

/// <summary>
/// Everything Ctrl+Space can find besides apps: Windows' settings (by the words people actually type, like "refresh
/// rate" or "PIN"), the classic tools (Device Manager, Services…) and Brim's own settings.
/// </summary>
public static class SettingsIndex
{
    /// <param name="Name">What the result says.</param>
    /// <param name="Where">Where it lives (shown under the name).</param>
    /// <param name="Target">An ms-settings: page, a program or console to start, or "brim:Page" for Brim's own settings.</param>
    /// <param name="Words">Other words that should find it, comma separated.</param>
    public sealed record Entry(string Name, string Where, string Target, string Words)
    {
        public bool IsBrim => Target.StartsWith("brim:", StringComparison.Ordinal);
        public string[] Phrases { get; } = Words.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    const string Sys = "Settings › System", Dev = "Settings › Bluetooth & devices", Net = "Settings › Network & internet",
        Per = "Settings › Personalisation", App = "Settings › Apps", Acc = "Settings › Accounts", Time = "Settings › Time & language",
        Game = "Settings › Gaming", Ease = "Settings › Accessibility", Priv = "Settings › Privacy & security", Upd = "Settings › Windows Update",
        Tool = "Windows tool", Brim = "Brim settings";

    public static readonly Entry[] All =
    {
        // ---- System
        new("Display", Sys, "ms-settings:display", "screen, monitor, resolution, scale, scaling, zoom, text size, brightness, orientation, rotate, multiple displays, second monitor, extend, duplicate, hdr"),
        new("Refresh rate", Sys + " › Display › Advanced display", "ms-settings:display-advanced", "hz, hertz, 60hz, 120hz, 144hz, 165hz, 240hz, fps, monitor refresh, advanced display, display information, dynamic refresh rate"),
        new("Graphics settings", Sys + " › Display › Graphics", "ms-settings:display-advancedgraphics", "gpu, graphics card, high performance gpu, gpu preference, hardware accelerated gpu scheduling, auto hdr, optimisations for windowed games, variable refresh rate, vrr"),
        new("HDR", Sys + " › Display › HDR", "ms-settings:display-hdr", "high dynamic range, auto hdr, hdr video, sdr content brightness"),
        new("Night light", Sys + " › Display", "ms-settings:nightlight", "blue light, warm, eye strain, night mode, colour temperature"),
        new("Resolution and scale", Sys + " › Display", "ms-settings:display", "display resolution, scale, dpi, text size, make everything bigger"),
        new("Brightness", Sys + " › Display", "ms-settings:display", "screen brightness, dim, adaptive brightness, content adaptive brightness"),
        new("Sound", Sys, "ms-settings:sound", "audio, speakers, headphones, output device, input device, microphone level, volume, spatial sound"),
        new("All sound devices", Sys + " › Sound", "ms-settings:sound-devices", "audio devices, disable speaker, rename headphones, microphone properties"),
        new("Volume mixer", Sys + " › Sound", "ms-settings:apps-volume", "app volume, per-app volume, mixer, app sound output"),
        new("Notifications", Sys, "ms-settings:notifications", "alerts, banners, toast, notification sounds, app notifications"),
        new("Do not disturb / Focus", Sys + " › Notifications", "ms-settings:quiethours", "focus, focus assist, quiet hours, do not disturb, dnd, silence notifications"),
        new("Power & battery", Sys, "ms-settings:powersleep", "power mode, best performance, battery, sleep, screen timeout, turn off screen, hibernate, lid, power button, energy"),
        new("Energy saver / Battery saver", Sys + " › Power & battery", "ms-settings:batterysaver", "battery saver, energy saver, low battery, save battery"),
        new("Storage", Sys, "ms-settings:storagesense", "disk space, free up space, storage sense, temporary files, cleanup, drive full"),
        new("Multitasking / Snap windows", Sys, "ms-settings:multitasking", "snap layouts, snap windows, alt tab, title bar shake, virtual desktops"),
        new("Clipboard", Sys, "ms-settings:clipboard", "clipboard history, win v, sync clipboard"),
        new("Nearby sharing", Sys, "ms-settings:crossdevice", "share files, nearby share"),
        new("Remote Desktop", Sys, "ms-settings:remotedesktop", "rdp, remote access, remote control"),
        new("Projecting to this PC", Sys, "ms-settings:project", "miracast, wireless display, cast to pc"),
        new("Troubleshoot", Sys, "ms-settings:troubleshoot", "troubleshooter, fix problems, other troubleshooters"),
        new("Recovery", Sys, "ms-settings:recovery", "reset this pc, reinstall windows, go back, advanced startup, safe mode, fresh start"),
        new("Activation", Sys, "ms-settings:activation", "product key, license, activate windows, windows edition, upgrade"),
        new("About this PC", Sys, "ms-settings:about", "pc name, rename pc, specs, specifications, system info, ram, processor, cpu, windows version, device id"),
        new("Device encryption", Priv, "ms-settings:deviceencryption", "bitlocker, encrypt drive"),

        // ---- Bluetooth & devices
        new("Bluetooth & devices", Dev, "ms-settings:bluetooth", "bluetooth, pair, add device, headphones, controller, airpods, earbuds"),
        new("Printers & scanners", Dev, "ms-settings:printers", "printer, scanner, print queue, default printer"),
        new("Mouse", Dev, "ms-settings:mousetouchpad", "mouse speed, pointer speed, cursor speed, scroll, scroll lines, primary button, left handed, mouse acceleration, enhance pointer precision"),
        new("Touchpad", Dev, "ms-settings:devices-touchpad", "trackpad, gestures, three finger, four finger, tap to click, scroll direction, swipe, sensitivity"),
        new("Pen & Windows Ink", Dev, "ms-settings:pen", "stylus, pen buttons, handwriting"),
        new("AutoPlay", Dev, "ms-settings:autoplay", "usb insert, memory card, autorun"),
        new("USB", Dev, "ms-settings:usb", "usb notifications, usb battery saver"),
        new("Cameras", Dev, "ms-settings:camera", "webcam, camera settings, brightness camera"),
        new("Phone Link / Mobile devices", Dev, "ms-settings:mobile-devices", "phone, android, iphone, phone link, link to windows"),

        // ---- Network & internet
        new("Wi-Fi", Net, "ms-settings:network-wifi", "wifi, wireless, wlan, known networks, hidden network, wifi password"),
        new("Ethernet", Net, "ms-settings:network-ethernet", "cable, lan, wired, ip address, dns, metered connection"),
        new("Network status", Net, "ms-settings:network-status", "internet, connected, network reset, data usage, properties"),
        new("Advanced network settings", Net, "ms-settings:network-advancedsettings", "network adapters, network reset, adapter options, dns, ip settings"),
        new("VPN", Net, "ms-settings:network-vpn", "virtual private network, vpn connection"),
        new("Mobile hotspot", Net, "ms-settings:network-mobilehotspot", "hotspot, share internet, tethering"),
        new("Airplane mode", Net, "ms-settings:network-airplanemode", "flight mode, airplane"),
        new("Proxy", Net, "ms-settings:network-proxy", "proxy server, pac script"),
        new("Data usage", Net, "ms-settings:datausage", "data limit, data used, metered"),

        // ---- Personalisation
        new("Background / Wallpaper", Per, "ms-settings:personalization-background", "wallpaper, desktop background, slideshow, picture, spotlight"),
        new("Colours / Accent colour", Per, "ms-settings:colors", "dark mode, light mode, accent colour, color, transparency effects, theme mode, accent on title bars"),
        new("Dark mode", Per + " › Colours", "ms-settings:colors", "dark theme, light theme, app mode, windows mode"),
        new("Themes", Per, "ms-settings:themes", "theme, desktop icons, sounds theme, mouse cursor theme"),
        new("Desktop icons", Per + " › Themes", "ms-settings:themes", "this pc icon, recycle bin icon, desktop icon settings"),
        new("Lock screen", Per, "ms-settings:lockscreen", "lockscreen, screen saver, lock screen picture, spotlight"),
        new("Start menu", Per, "ms-settings:personalization-start", "start, recommended, recently added, folders next to power, layout"),
        new("Taskbar", Per, "ms-settings:taskbar", "taskbar alignment, widgets button, task view, search box, system tray, hidden icons, auto hide"),
        new("Fonts", Per, "ms-settings:fonts", "install font, typeface"),
        new("Touch keyboard", Per, "ms-settings:personalization-textinput", "on-screen keyboard, emoji keyboard"),
        new("Dynamic Lighting", Per, "ms-settings:personalization-lighting", "rgb, lighting, keyboard lights, lamp array"),

        // ---- Apps
        new("Installed apps", App, "ms-settings:appsfeatures", "uninstall, remove app, apps & features, programs, app size, repair app, reset app"),
        new("Default apps", App, "ms-settings:defaultapps", "default browser, open with, file associations, default program, pdf reader"),
        new("Startup apps", App, "ms-settings:startupapps", "run at startup, launch on boot, start with windows, autostart"),
        new("Optional features", Sys, "ms-settings:optionalfeatures", "add feature, wireless display, openssh"),
        new("Apps for websites", App, "ms-settings:appsforwebsites", "open links in app"),
        new("Video playback", App, "ms-settings:videoplayback", "hdr video, video quality"),

        // ---- Accounts
        new("Your info", Acc, "ms-settings:yourinfo", "account picture, profile, microsoft account"),
        new("Sign-in options", Acc, "ms-settings:signinoptions", "pin, password, windows hello, fingerprint, face recognition, dynamic lock, change password, security key"),
        new("Email & accounts", Acc, "ms-settings:emailandaccounts", "email accounts, add account"),
        new("Other users", Acc, "ms-settings:otherusers", "add user, family, local account, guest"),
        new("Windows Backup", Acc, "ms-settings:backup", "backup, sync settings, onedrive backup, remember my apps"),

        // ---- Time & language
        new("Date & time", Time, "ms-settings:dateandtime", "clock, time zone, timezone, set time automatically, sync time"),
        new("Language & region", Time, "ms-settings:regionlanguage", "language, display language, keyboard layout, input language, add language, region, country, arabic"),
        new("Region format", Time + " › Language & region", "ms-settings:regionformatting", "date format, time format, 24 hour, first day of week, currency"),
        new("Typing", Time, "ms-settings:typing", "autocorrect, spell check, text suggestions, keyboard settings, advanced keyboard"),
        new("Speech", Time, "ms-settings:speech", "voice, speech recognition, voice typing"),

        // ---- Gaming
        new("Game Mode", Game, "ms-settings:gaming-gamemode", "game mode, gaming performance"),
        new("Game Bar", Game, "ms-settings:gaming-gamebar", "xbox game bar, win g, overlay"),
        new("Captures", Game, "ms-settings:gaming-gamedvr", "record game, screen recording, clips, record what happened"),

        // ---- Accessibility
        new("Text size", Ease, "ms-settings:easeofaccess-display", "bigger text, font size, make text bigger"),
        new("Visual effects", Ease, "ms-settings:easeofaccess-visualeffects", "animations, animation effects, transparency effects, always show scrollbars, dismiss notifications after"),
        new("Mouse pointer and touch", Ease, "ms-settings:easeofaccess-mousepointer", "cursor size, pointer size, cursor colour, mouse pointer style"),
        new("Text cursor", Ease, "ms-settings:easeofaccess-cursor", "caret, text cursor thickness, cursor indicator"),
        new("Magnifier", Ease, "ms-settings:easeofaccess-magnifier", "zoom, magnify"),
        new("Colour filters", Ease, "ms-settings:easeofaccess-colorfilter", "colour blind, grayscale, invert colours"),
        new("Contrast themes", Ease, "ms-settings:easeofaccess-highcontrast", "high contrast"),
        new("Narrator", Ease, "ms-settings:easeofaccess-narrator", "screen reader, read aloud"),
        new("Audio (mono audio)", Ease, "ms-settings:easeofaccess-audio", "mono audio, flash screen on notification"),
        new("Captions", Ease, "ms-settings:easeofaccess-closedcaptioning", "subtitles, live captions"),
        new("Keyboard accessibility", Ease, "ms-settings:easeofaccess-keyboard", "sticky keys, filter keys, toggle keys, on-screen keyboard, print screen key, underline access keys"),
        new("Mouse keys", Ease, "ms-settings:easeofaccess-mouse", "control mouse with keyboard, numpad mouse"),
        new("Eye control", Ease, "ms-settings:easeofaccess-eyecontrol", "eye tracker"),

        // ---- Privacy & security
        new("Windows Security", Priv, "windowsdefender:", "defender, antivirus, virus scan, firewall, threat protection, malware"),
        new("Privacy & security", Priv, "ms-settings:privacy", "privacy, permissions, app permissions"),
        new("Location", Priv, "ms-settings:privacy-location", "gps, location services, location history"),
        new("Camera permissions", Priv, "ms-settings:privacy-webcam", "camera access, webcam access, allow apps to use camera"),
        new("Microphone permissions", Priv, "ms-settings:privacy-microphone", "mic access, allow apps to use microphone"),
        new("General privacy", Priv, "ms-settings:privacy-general", "advertising id, tailored experiences"),
        new("Diagnostics & feedback", Priv, "ms-settings:privacy-feedback", "telemetry, diagnostic data, feedback frequency"),
        new("Activity history", Priv, "ms-settings:privacy-activityhistory", "timeline, activity history"),
        new("Find my device", Priv, "ms-settings:findmydevice", "lost device, locate"),
        new("For developers", Sys, "ms-settings:developers", "developer mode, sudo, terminal, file explorer options, show file extensions, hidden files"),

        // ---- Windows Update
        new("Windows Update", Upd, "ms-settings:windowsupdate", "updates, check for updates, update windows, patches, pause updates"),
        new("Update history", Upd, "ms-settings:windowsupdate-history", "installed updates, uninstall updates"),
        new("Advanced update options", Upd, "ms-settings:windowsupdate-options", "optional updates, active hours, delivery optimisation, restart"),
        new("Windows Insider Program", Upd, "ms-settings:windowsinsider", "insider, preview builds, beta"),

        // ---- Classic tools
        new("Device Manager", Tool, "devmgmt.msc", "drivers, hardware, update driver, uninstall driver, devices"),
        new("Disk Management", Tool, "diskmgmt.msc", "partitions, format drive, drive letter, volumes"),
        new("Services", Tool, "services.msc", "windows services, stop service, startup type"),
        new("Event Viewer", Tool, "eventvwr.msc", "logs, errors, crash logs, event log"),
        new("Task Scheduler", Tool, "taskschd.msc", "scheduled tasks"),
        new("Computer Management", Tool, "compmgmt.msc", "manage computer, local users"),
        new("Registry Editor", Tool, "regedit.exe", "registry, regedit"),
        new("Control Panel", Tool, "control.exe", "classic control panel"),
        new("Network Connections", Tool, "ncpa.cpl", "network adapters, adapter settings, change adapter options, ipv4, dns server"),
        new("Power Options (classic)", Tool, "powercfg.cpl", "power plan, high performance plan, lid close action, power button action, ultimate performance"),
        new("Sound (classic)", Tool, "mmsys.cpl", "playback devices, recording devices, sound control panel, sample rate, exclusive mode"),
        new("Programs and Features", Tool, "appwiz.cpl", "uninstall a program, add remove programs"),
        new("System Properties", Tool, "sysdm.cpl", "computer name, performance options, virtual memory, page file, system protection, restore point"),
        new("Environment Variables", Tool, "rundll32.exe|sysdm.cpl,EditEnvironmentVariables", "path, env vars, system variables, user variables"),
        new("Mouse Properties (classic)", Tool, "main.cpl", "double click speed, pointer trails, mouse buttons"),
        new("Windows Firewall", Tool, "firewall.cpl", "firewall, allow app through firewall, ports"),
        new("User Accounts (classic)", Tool, "netplwiz.exe", "auto login, user passwords, netplwiz"),
        new("Credential Manager", Tool, "control.exe|/name Microsoft.CredentialManager", "saved passwords, windows credentials"),
        new("Devices and Printers", Tool, "control.exe|printers", "printers, devices"),
        new("Fonts folder", Tool, "control.exe|fonts", "installed fonts"),
        new("System Information", Tool, "msinfo32.exe", "msinfo, hardware info, bios version, motherboard"),
        new("DirectX Diagnostic Tool", Tool, "dxdiag.exe", "dxdiag, directx version, graphics driver info"),
        new("Disk Cleanup", Tool, "cleanmgr.exe", "clean disk, temp files, free space"),
        new("Defragment and Optimise Drives", Tool, "dfrgui.exe", "defrag, optimise drive, trim ssd"),
        new("Resource Monitor", Tool, "resmon.exe", "resmon, network activity, disk activity"),
        new("Performance Monitor", Tool, "perfmon.msc", "perfmon, performance counters"),
        new("Task Manager", Tool, "taskmgr.exe", "end task, processes, cpu usage, memory usage, startup"),
        new("Windows Tools", Tool, "control.exe|admintools", "administrative tools"),
        new("System Restore", Tool, "rstrui.exe", "restore point, roll back"),
        new("On-Screen Keyboard", Tool, "osk.exe", "osk, virtual keyboard"),
        new("Snipping Tool", Tool, "ms-screenclip:", "screenshot, screen snip, print screen"),

        // ---- Brim's own settings
        new("Brim: notch", Brim, "brim:Notch", "notch, notch tabs, island, notch shape, notch position, hide over apps, live activities, home tab, glance, stats"),
        new("Brim: keyboard shortcuts", Brim + " › Notch", "brim:Notch", "hotkeys, shortcuts keys, ctrl space, ctrl alt, change keys, disable shortcut, win 1"),
        new("Brim: lyrics", Brim + " › Notch", "brim:Notch", "lyrics in the notch, song lyrics"),
        new("Brim: clipboard history", Brim + " › Notch", "brim:Notch", "clipboard tab, clips, remember clipboard"),
        new("Brim: look and theme", Brim + " › Look", "brim:Look", "theme, accent, colour, colour wheel, notch colour, dock colour, glass, dark, widget style"),
        new("Brim: messages", Brim + " › Messages", "brim:Messages", "message pop-ups, notifications in the notch, discord, teams"),
        new("Brim: shortcuts tab", Brim + " › Shortcuts", "brim:Shortcuts", "notch shortcuts, apps in the notch"),
        new("Brim: games", Brim + " › Games", "brim:Games", "game library, game covers, game folders"),
        new("Brim: widgets", Brim + " › Widgets", "brim:Widgets", "desktop widgets, clock widget, weather widget, music widget, weather city"),
        new("Brim: dock / taskbar", Brim + " › Taskbar", "brim:Taskbar", "dock, dock side, dock left, dock right, edge of the screen, auto hide dock, dock size, dock position, keyboard language, clock on dock"),
        new("Brim: desktop", Brim + " › Desktop", "brim:Desktop", "desktop icons, wallpaper, live wallpaper, oled care, burn in"),
        new("Brim: uninstall apps", Brim + " › Uninstall", "brim:Uninstall", "remove programs with brim"),
        new("Brim: updates and online", Brim + " › General", "brim:General", "brim updates, online, internet, offline, weather online, start with windows, backup settings"),
    };

    static int Score(string text, string q)
    {
        var n = text.ToLowerInvariant();
        if (n == q) return 100;
        if (n.StartsWith(q)) return 80;
        if (Regex.IsMatch(n, @"\b" + Regex.Escape(q))) return 60;
        if (q.Length >= 3 && n.Contains(q)) return 40;
        return 0;
    }

    /// <summary>How well this entry fits what was typed (0: not at all). Its name counts most, then its other words.</summary>
    public static int Match(Entry e, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length < 2) return 0;
        int best = Score(e.Name, q);
        foreach (var p in e.Phrases) best = Math.Max(best, Score(p, q) - 6);
        if (best > 0) return best;
        // Every word typed appears somewhere ("change pin", "mouse speed")
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var all = (e.Name + " " + e.Words + " " + e.Where).ToLowerInvariant();
        return words.Length > 1 && words.All(w => Regex.IsMatch(all, @"\b" + Regex.Escape(w))) ? 45 : 0;
    }
}
