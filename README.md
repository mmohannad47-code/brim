# Brim

**A cleaner Windows desktop.** Brim adds a notch at the top of your screen (music, shortcuts, games, messages, notes and more), an optional dock that replaces the taskbar, desktop widgets and live wallpapers.

Works on Windows 10 (version 2004 or newer) and Windows 11. No admin rights needed, and nothing else to install.

---

## Install

1. Go to the [**Releases**](../../releases/latest) page and download **`Brim-x.y.z-Setup.exe`** (about 90 MB).
2. Double-click it.
3. **"Windows protected your PC"?** Brim isn't signed with a paid certificate, so Windows warns about new downloads. Click **More info**, then **Run anyway**.
4. Click **Yes** to install. Brim installs just for you (into `%LOCALAPPDATA%\Programs\Brim`), adds itself to the Start menu and starts up.
5. The first launch takes a few seconds while it unpacks itself and Windows Defender checks it. After that it starts almost instantly.

Settings open on the first run so you can pick what you want. Nothing about your taskbar or desktop changes until you choose it.

**Start with Windows:** Settings › General › *Start Brim with Windows*.

### Update

Brim updates itself (from 0.6.0 on): it downloads new versions in the background and the notch pops up *"Brim x is ready"*. Click it to restart into the new version. You can also check in **Settings › General › Updates**, or turn automatic updates off there. Your settings, notes and widgets are always kept.

Coming from 0.5.x? Download the newest `Setup.exe` once and run it (click **Yes** to update); after that, updates come by themselves.

### Uninstall

Windows **Settings › Apps › Installed apps › Brim › Uninstall** (or *Add or remove programs* on Windows 10). Brim puts your taskbar and desktop icons back first, then asks whether to keep your settings.

---

## User guide

### The notch

The small pill at the top of the screen. **Point at it** (or press **Ctrl+Alt+N**) to open it. Its tabs:

| Tab | What it does |
|---|---|
| **Home** | What's playing (Spotify, YouTube, any app with media controls) with cover art, a seek bar and controls, plus the date, CPU, memory, connected devices' batteries and volume. |
| **Shortcuts** | Up to 12 apps, folders or websites. Drag an app onto the notch to add it; right-click to remove. The first 9 open with **Ctrl+Alt+1…9**. |
| **Games** | Every game you have installed, with cover art: Steam, Epic, GOG, Riot, EA, Ubisoft, Battle.net, Xbox, and game folders on any drive. New installs appear by themselves. Right-click a game to launch with DirectX/Vulkan, pick a different exe, change the cover or uninstall it. |
| **Messages** | Notifications from Discord, Teams, Outlook, WhatsApp and other chat and mail apps. Click one to open it; right-click to mute a person or app. Turn on **Settings › Messages › Only pop up in the notch** to stop Windows' own corner pop-up for those apps. |
| **Notes** | A page to type anything. Saves as you type. |
| **Shelf** | Drop files on the notch to park them, then drag them out later. |
| **Clipboard** | The last 40 things you copied. Click to copy again. |
| **Controls** | Wi-Fi, Bluetooth, mic, volume, output device and brightness. |
| **Timer** | A focus timer; the notch counts down and pops out when time's up. |

Choose which tabs to show, and their order, in **Settings › Notch**. The notch can sit at the left, centre or right, look like a notch or a floating island, and hides itself in fullscreen games and videos. With more than one monitor, **Settings › Notch › Screen › Every screen** puts a notch on each of them.

It also pops out briefly for song changes, new messages, muting or unmuting your sound or mic, charging and headphones connecting.

### The dock (optional taskbar replacement)

**Settings › Taskbar** › choose **Brim dock**. You get a rounded bar with Start, your pinned and open apps, the tray, quick settings and the clock.

- Click an app to switch to it, again to minimise. Middle-click for a new window. Right-click to pin or close.
- Point at an app to see live previews of its windows.
- **Red badge:** the app has notifications or unread messages. **Blue arrow badge:** it's downloading something.
- **⌃** opens the tray icons. The status icons open quick settings. The far-right strip is *show desktop*.
- Size, spacing, position, auto-hide and whether windows stay above the dock are all in **Settings › Taskbar**.

Prefer Windows' own taskbar? The same page can just tidy it (centre or left icons, hide Search/Task View/Widgets) or auto-hide it.

### Widgets

**Settings › Widgets**: clock, day, calendar, music, weather (set your city), PC stats, day progress, world clock, notes, and your own designs (*Design your own*). Drag widgets to move them; right-click to customise, lock, combine or remove.

### Wallpapers

**Settings › Desktop › Wallpapers** (or the tray icon):

- **Pictures** and **OLED** (true-black): thousands of free wallpapers.
- **Videos**: live video wallpapers.
- **Animated**: effects Brim draws itself (aurora, rain, stars…), nothing to download.
- **Wallpaper Engine**: if you have Wallpaper Engine, your library shows here and opens in Wallpaper Engine.
- **Downloaded**: ones you've used before, or add your own pictures and videos.

Live wallpapers pause by themselves while a game or maximised window covers the screen.

### Looks

**Settings › Look**: themes (Black, Graphite, Vampire, Synthwave, Matrix, Gold, Ember, Glacier and more), accent colours, glass or solid surfaces, and matching colours from your album art.

### Keyboard shortcuts

| Keys | Does |
|---|---|
| **Ctrl+Alt+N** | Open or close the notch |
| **Ctrl+Space** | Search apps, games, settings and recent files; type a sum to calculate it |
| **Ctrl+Alt+1…9** | Open shortcut 1–9 |
| **Ctrl+Alt+P** | Play / pause music |
| **Ctrl+Alt+M** | Mute / unmute the microphone |
| **Ctrl+Alt+C** | Copy the colour under the pointer (#RRGGBB) |
| **Ctrl+Alt+V** | Paste as plain text |
| **Ctrl+Alt+H** | Hide / show desktop icons |

### Backup

**Settings › General › Save my settings** writes everything (notch, dock, widgets, shortcuts, games, look) to one file. *Load settings* brings it back, on this PC or another.

---

## Troubleshooting

- **The Windows taskbar didn't come back.** Brim restores it when it quits normally. If Brim was force-closed, start Brim again and choose **Windows taskbar** in Settings › Taskbar, or right-click Brim's tray icon › *Use Windows' taskbar again*.
- **No messages from an app.** Brim shows what apps send to Windows' notification centre. Check notifications are on in the app (Discord: *User Settings › Notifications › Enable Desktop Notifications*) and in Windows Settings › System › Notifications. While a fullscreen video or game is open, Windows tells apps not to notify; Brim's Messages page says when that's happening.
- **A game is missing or has the wrong cover.** Settings › Games lets you add folders, rename, hide or change a game. Right-click a cover › *Choose cover image…* to use your own picture.
- **Something else.** Settings › General › *Settings and log files* opens the folder with `brim.log`; send that file along with what happened.

---

## Privacy

Brim runs on your PC. It only goes online to fetch the things you ask for: wallpapers (wallhaven.cc, Pixabay), game covers (Steam store, SteamGridDB), weather (Open-Meteo, only once you set a city), lyrics (LRCLIB) and website icons for your shortcuts. Notes, clipboard history, messages and settings never leave your PC.

---

## Building from source

The .NET 8 SDK can live in `.tools\dotnet` (get it with `.tools\dotnet-install.ps1`), so nothing has to be installed system-wide.

```powershell
# Development build
.\.tools\dotnet\dotnet.exe build -c Release

# Release file: one self-contained exe that runs without .NET installed
.\.tools\dotnet\dotnet.exe publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

**API keys:** video wallpapers (Pixabay) and covers for games that aren't on Steam (SteamGridDB) need free keys. Official releases have them built in. A build from this source doesn't (`Core/ApiKeys.Local.cs` is private and not in the repository), so those two features ask for your own key in Settings. Everything else works without one.

`Brim.exe --snapshot <folder>` renders every screen to PNGs off-screen without touching your desktop or settings (set `BRIM_FRESH=1` to see it as a brand-new install would).

### Layout

- `Core/`: Windows interop, settings, media, audio, taskbar and tray, notifications, games, wallpapers, installer.
- `Views/`: the notch, dock, widgets, settings and other windows.
- `Theme/Styles.xaml`: shared colours and control styles.
