# UZConnect 0.2.0

Windows desktop manager for the **FY_UZ801_V3.2** USB modem (MSM8916, stock firmware V2.3.11),
in the classic D-Link Connection Manager style.

## New in 0.2.0

- **Notification area support.** Closing the window now hides UZConnect to the tray instead of
  exiting, so the modem session keeps running. The tray menu offers Open, Connect to Internet,
  Disconnect and Exit; double-click opens the window.
- **Start with Windows.** Optional per-user startup entry (`HKCU\...\Run`), launched minimized.
- **Connect on startup.** Optional auto-connect that brings mobile data up after the modem is
  detected, with a bounded retry (up to three attempts) rather than an endless loop.
- Closing to the tray can be turned off in **Settings → General**; the window then exits normally.

## Packages

| File | What it is | SHA-256 |
|---|---|---|
| `UZConnect-0.2.0-win-x64.exe` | Single-file app, no unzip and no .NET install | see release assets |
| `UZConnect-0.2.0-win-x64.zip` | Portable folder build | see release assets |

The single-file build extracts its bundled SMS/Wi-Fi helpers to
`%LOCALAPPDATA%\UZConnect\helpers` on first use.

## What works

- USB detection, ADB transport, device/SIM/network status
- Mobile data connect/disconnect and APN profiles (NTC and Ncell defaults)
- SMS inbox/sent/drafts/recycle, compose/reply/forward/export, multi-select delete with
  confirmation, and the classic message Edit dialog
- USSD command list, interactive menu replies, temporary 3G switching and restore
- Wi-Fi Management: real AP state, network name, maximum devices, security/channel/broadcast,
  connected Wi-Fi clients (name/IP/MAC), DHCP ranges and a per-feature availability page
- Network name and device-limit changes are backed up, applied, verified against the active
  and saved modem state, and rolled back automatically if verification fails

## Known limitations

- **Wi-Fi ON/OFF, password change and hidden SSID are disabled.** The vendor service re-asserts
  its settings on every access-point bind, and the stock networking service writes the WPA
  passphrase to the Android log on every AP start. The controls are shown disabled with the
  reason instead of pretending to work.
- Channel, security mode and DHCP/gateway/DNS settings are read-only.
- Auto-connect timing depends on the modem's network registration and can take a minute after a
  manual disconnect.

## Install

1. Download the `.exe` (recommended) or the `.zip`.
2. Run `UZConnect.exe`, connect the modem and wait for **Device ready**.
3. If ADB is not detected, set the `adb.exe` path in **Settings > General**.
4. Install the bundled SMS helper once from the same page to enable sending.
5. Optionally enable **Start with Windows** and **Connect to the internet when UZConnect starts**.

No firmware flashing, partition writing or bootloader changes are performed.
