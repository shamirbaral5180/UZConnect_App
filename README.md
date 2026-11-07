# UZConnect

UZConnect is a Windows desktop manager for the **FY_UZ801_V3.2** USB modem running stock
firmware **V2.3.11**. The interface is a classic **D-Link Connection Manager** style layout
(no sidebar, no dashboard cards) with the same seven toolbar sections:

**Connection · SMS · Contacts · USSD · Statistics · Settings · Help**

Wi-Fi Management is the only additional section. It is deliberately kept small and lives
inside Settings.

## Verified hardware

| Item | Verified value |
|---|---|
| USB identity | `VID_05C6 PID_90B6` (Qualcomm USB Composite Device 90B6) |
| Android | 4.4.4 (API 19) |
| Model | UZ801 |
| Firmware | V2.3.11 |
| Baseband | N958St_Z85_CN_JSXPH1IDN1H213 |
| Interfaces | RNDIS, HS-USB Modem, HS-USB Diagnostics (COM12), ADB |
| ADB | Authorized; shell runs as root on the tested stock image |
| SIM tested | Ncell Nepal, READY, LTE registration |
| SMS | Inbox read/sync and send verified through the Android SMS provider/helper |

Hardware identity is discovered at runtime. No VID/PID is hard-coded into the application.
See `docs/HARDWARE-REPORT.md` for the complete Phase 1 investigation and test evidence.

## Running

Requirements:

- Windows 10/11
- .NET 10 SDK / runtime
- Android platform-tools (`adb.exe`) installed, or set the path in **Settings**
- The UZ801 connected by USB

```powershell
dotnet build src\UZConnect.App\UZConnect.App.csproj -c Release
dotnet run --project src\UZConnect.App\UZConnect.App.csproj -c Release
```

The built executable is at `src\UZConnect.App\bin\Release\net10.0-windows\UZConnect.exe`.

### SMS helper

Reading and deleting SMS uses the device's Android SMS provider through ADB. Sending needs
the small helper app in `tools\agent` because the stock shell has no supported SmsManager
entry point. The helper can be installed from the SMS composer; it has no UI, no network
permission and no background polling. Build it with:

```powershell
powershell -ExecutionPolicy Bypass -File tools\agent\build-agent.ps1
```

The signed APK is embedded into the UZConnect output automatically when present.

## Functional status

| Feature | Status |
|---|---|
| USB arrival/removal and ADB detection | Working, verified on hardware |
| Connection status, IP, operator, signal | Working |
| Connect / Disconnect | Working through the stock modem control channel (`funcNo 1004`) |
| APN profile management | Working through the stock modem profile table (Profile Management) |
| SMS Inbox/Outbox/Draft/Recycle | Working; Recycle is a local archive |
| SMS compose, reply, forward, export, multi-select | Working |
| SMS sending | Working through the helper agent; the app waits for the final result and confirms against the device Sent folder, so an Android 4.4 generic-failure report does not show a false failure. Verified by self-test. |
| Statistics (speeds, totals, chart) | Working from modem counters |
| USSD command list and send | Working through the modem AT command port (`AT+CUSD`). Menu replies are supported: after a menu the Send button becomes Reply and the same input box accepts option numbers. The app temporarily uses 3G for USSD because this modem rejects CUSD over LTE, keeps 3G while a menu is open, and restores the saved network mode when the session ends or is cancelled. Balance check was verified on the Ncell SIM. |
| Contacts | Read-only SIM phonebook (the requested scope omits contact management) |
| PIN management | Displayed as unsupported; no PIN write is attempted |
| Wi-Fi status | Working: active AP state, saved state and the running access-point process must all agree |
| Wi-Fi details | Working: network name, security, channel, broadcast state, password presence, address ranges and connected clients |
| Wi-Fi client list | Working: device name, IP and MAC of associated Wi-Fi clients only |
| Wi-Fi settings change | Verified on hardware: name and maximum-device limit are backed up, applied, confirmed in active and saved state, and restored on failure |
| Wi-Fi ON/OFF | Not supported by this stock image: the vendor watchdog re-enables the access point, and an interface-only attempt was also reverted within one interval. The UI disables the control |
| Wi-Fi password change | Blocked: the vendor setter writes the supplied password to the Android log, so a secure change cannot be guaranteed |
| Wi-Fi channel / hidden SSID / MAC filtering / DHCP / reservations | Shown read-only or disabled; the stock controls were inspected and do not apply or cannot be verified |
| Diagnostic report | Available as a redacted export; normal UI contains no USB IDs, COM ports, ADB details or debug logs |

## Architecture

```
src/UZConnect.Core          Contracts, models, SMS segment logic, parsers
src/UZConnect.Windows       USB/WMI discovery, ADB, stock web control, SQLite, session
src/UZConnect.App           WPF classic Connection Manager UI (MVVM)
tools/agent                 Android SMS helper source and APK build
tools/wifi                  Root Wi-Fi state helper and the hardware qualification harness
tools/probe                 Console hardware probe used during Phase 1
tests/UZConnect.Tests       Unit tests
docs                        Hardware report, UI reference notes, verification
```

The UI never talks to hardware directly. `ModemSession` owns detection, the providers and the
cache. The ADB provider remains the verified transport for SMS, device information and SIM
status. The stock RNDIS web control channel is used only for modem functions not exposed by
ADB (connection mode, APN profiles, traffic counters, Wi-Fi status). USB detection and SMS do
not depend on Wi-Fi or on the web interface.

## Data and security

- Local data lives in `%LOCALAPPDATA%\UZConnect` (SQLite cache, logs, exports).
- SMS bodies and full IMEIs are never written to logs.
- IMEI is masked by default.
- Deletion is confirmed first; messages are copied to the local Recycle before removal from
  the modem.
- No firmware flashing, partition writes, rooting, bootloader changes or automatic firmware
  modification is performed.
- Wi-Fi changes use the stock vendor control channel, are backed up first, and are verified on the
  modem; see `docs/WIFI-MANAGEMENT.md`. Password changes and Wi-Fi ON/OFF are not offered because
  the firmware cannot apply them securely or reliably.

## UI reference

The layout was reproduced from the supplied D-Link Connection Manager screenshots and the
DWM-222 user manual. See `docs/UI-REFERENCE.md` for the screen-by-screen mapping and the
intentional differences (UZConnect branding, Wi-Fi Management, read-only contacts).
