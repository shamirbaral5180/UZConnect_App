# UZConnect 0.1.0

Windows desktop manager for the **FY_UZ801_V3.2** USB modem (MSM8916, stock firmware V2.3.11),
in the classic D-Link Connection Manager style.

## Packages

| File | What it is | SHA-256 |
|---|---|---|
| `UZConnect-0.1.0-win-x64.exe` | Single-file app, no unzip and no .NET install | `CE4BC6AA62BA1A55D3C66E8A631D4F1C741DAB2B727991F0C01F8D147AD2935D` |
| `UZConnect-0.1.0-win-x64.zip` | Portable folder build | `FAF2707885A1ABAC5E86FE1F1B8B38440835B7B41BB781E80B442D3EC520825A` |

The single-file build extracts its bundled SMS/Wi-Fi helpers to
`%LOCALAPPDATA%\UZConnect\helpers` on first use.

## What works

- USB detection, ADB transport, device/SIM/network status
- Mobile data connect/disconnect and APN profile management (NTC and Ncell defaults)
- SMS inbox/sent/drafts/recycle, compose/reply/forward/export, multi-select delete with
  confirmation, reading a message in the classic Edit dialog
- USSD command list, interactive menu replies, temporary 3G switching for USSD and restoration
- Wi-Fi Management page (router-style layout): status, network name, maximum devices,
  security/channel/broadcast display, connected Wi-Fi clients (name/IP/MAC), DHCP ranges,
  and a per-feature availability list
- Network name and maximum-device changes are backed up, applied, verified against the active
  and saved modem state, and rolled back automatically if verification fails

## Known limitations (stock firmware V2.3.11)

- **Wi-Fi ON/OFF, password change and hidden SSID are disabled.** The vendor service and `netd`
  overwrite these settings or restart the access point; the stock networking service also writes
  the WPA passphrase to the Android log on every AP start. The controls are shown disabled with
  the reason, and no unverified change is written to the modem.
- Channel, security mode and DHCP/gateway/DNS settings are read-only.

## Requirements

- Windows 10/11 x64 (self-contained build; no .NET installation required)
- The modem connected over USB with ADB authorized; the SMS helper APK and Wi-Fi helper JAR are
  included in `Assets/` and installed/pushed by the app when needed

No firmware flashing, partition writing or bootloader changes are performed by the application.
