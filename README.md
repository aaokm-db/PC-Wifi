# PC Wifi — Windows Hotspot Manager

**English** | [العربية](README.ar.md)

A native Windows desktop application for managing the Windows Mobile Hotspot. PC Wifi combines hotspot controls, connected-device visibility, bandwidth limits, traffic monitoring, and a keep-alive watchdog in one WPF interface.

> The application targets Windows. The source is provided; the prebuilt executable from the supplied archive is intentionally not committed because it predates the bilingual UI changes. Build the current source using `build.ps1`.

## Features

- **Hotspot control:** start/stop the Windows Mobile Hotspot and view its network profile.
- **Network configuration:** view or edit the SSID, password, and supported Wi-Fi band.
- **Hidden network mode:** optional stealth/discoverability controls.
- **Connected-device management:** view discovered clients, rename devices, disconnect/block clients, and retain device history.
- **Traffic monitoring:** track upload/download totals and live transfer rates.
- **Usage and speed limits:** configure global and per-device download quotas and bandwidth limits.
- **Keep-alive watchdog:** monitor hotspot state and attempt recovery if Windows or the Wi-Fi driver stops the hotspot.
- **Power and startup options:** prevent system sleep while active, adjust the Windows no-connections timeout, start with Windows, and minimize to the system tray.
- **Activity log:** review and copy operational events.
- **Themes and languages:** Windows 11-inspired light/dark themes and an English/Arabic language selector.

## Requirements

- Windows 10 or Windows 11 with Mobile Hotspot support.
- A Wi-Fi adapter and driver that support Windows Mobile Hotspot.
- .NET Framework 4.x / Windows desktop runtime components available on the system.
- Administrator privileges for features that modify Windows Firewall rules or apply network QoS controls. Basic hotspot operation may work without elevation, depending on the machine and Windows configuration.
- PowerShell to run the build script.

Some Wi-Fi bands and features depend on the adapter, driver, Windows build, and upstream internet connection.

## Build from source

1. Clone or download this repository on a Windows machine.
2. Open **Windows PowerShell** in the repository folder.
3. Run:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\build.ps1
   ```

4. The script compiles `PCWifi.cs` and writes `PCWifi.exe` in the repository root.
5. Run `PCWifi.exe`. If you need firewall blocking or QoS enforcement, restart it as Administrator from the app's admin prompt/card.

If the icon file is missing, the build script runs `GenerateIcon.ps1` to recreate it.

## Run

```powershell
.\PCWifi.exe
```

Optional command-line switches:

- `--minimized` — start minimized.
- `--screenshots <folder>` — set the screenshot output directory used by the app's screenshot workflow.

## Language

The language preference is saved in `%LOCALAPPDATA%\PCWifi\settings.cfg` as `Language=en` or `Language=ar`. Use the language row in the left sidebar to switch languages. Existing settings and device records are retained when changing the language.

The interface is bilingual for the main navigation and core hotspot, device, data, keep-alive, and log controls. Some diagnostic messages, operating-system text, device-provided names, and less common dynamic messages may remain in English.

## Data and permissions

- Application settings and device records are stored locally under `%LOCALAPPDATA%\PCWifi\`.
- The app interacts with Windows Mobile Hotspot APIs and, for selected controls, Windows Firewall and QoS facilities.
- Blocking and bandwidth enforcement can require elevation. Review the Windows prompts before approving them.
- Hidden SSID mode is not encryption and does not make a Wi-Fi network secure by itself. Use a strong password and current Wi-Fi security settings.
- Device discovery and identification depend on information exposed by Windows and the network adapter; not every client will always provide a reliable hostname or address.

## Repository layout

```text
PCWifi/
├── PCWifi.cs                 # Main WPF application and hotspot controller
├── legacy/
│   └── HotspotKeepAlive.cs   # Separate/older keep-alive implementation retained from source archive
├── app.ico                   # Application icon
├── GenerateIcon.ps1          # Recreates app.ico
├── build.ps1                 # Builds the main PCWifi.exe
├── README.md                 # English documentation
├── README.ar.md              # Arabic documentation
├── docs/
│   └── BUILDING.md           # Build notes
├── CONTRIBUTING.md
├── SECURITY.md
└── .gitignore
```

`legacy/HotspotKeepAlive.cs` is preserved as source material from the supplied archive. It is not compiled by the main `build.ps1` script.

## Troubleshooting

- **Hotspot cannot start:** confirm Mobile Hotspot works in Windows Settings, update the Wi-Fi driver, and check that Windows has an active internet source.
- **No connected devices appear:** wait for the next refresh, reconnect the client, and check whether the adapter exposes client information on your Windows build.
- **Block/limit controls do not take effect:** run PC Wifi as Administrator and review Windows Firewall/QoS policy conflicts.
- **Hotspot turns off while idle:** enable the Keep-Alive Guard and the Windows no-connections timeout option. Some adapter drivers may still override Windows settings.
- **Build fails:** run the script from Windows PowerShell on Windows and verify the .NET Framework compiler and Windows metadata references used by `build.ps1` are present.

## Contributing

Bug reports and focused pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).
