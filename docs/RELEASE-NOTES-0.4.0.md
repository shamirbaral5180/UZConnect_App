# UZConnect 0.4.0

Windows desktop manager for the **FY_UZ801_V3.2** USB modem (MSM8916, stock firmware V2.3.11).

## Fixed

- **Connect and disconnect now always work.** The vendor "connect" command only programmed the
  modem state; Android kept its own mobile-data flag off after a disconnect, so no data session
  was created. UZConnect now enables the Android data layer as part of every connect and waits
  for the real `rmnet` address before reporting success.
- **No more false "Connected".** The data state is read only from the cellular interface. The
  modem's LAN gateway (`192.168.100.1`) is never treated as a data IP, and a disconnect holds
  until the interface is actually gone.
- **Repeatable cycles.** Connect → disconnect → connect was verified three times in a row, each
  ending with a live `rmnet` address and the vendor reporting Connected.

## Changed

- **Wi‑Fi is read-only.** The page now only reads and displays:
  - access-point status,
  - network name (SSID),
  - current password,
  - security mode, encryption, channel and broadcast state,
  - connected devices with IP and MAC,
  - LAN address, DHCP range and lease time.

  There is no power switch, no SSID or password editing, and no save button. **Refresh** only
  re-reads state. Nothing on this page writes to the modem. The password is shown on this PC
  only; it is never logged or sent anywhere.
- **Removed from Windows startup.** UZConnect no longer registers a startup entry, no longer
  offers the "Start with Windows" / "Connect when Windows starts" options, and does not run
  auto-connect on launch. Closing the window still keeps it running in the notification area.

## Kept

- The classic D-Link-style first page and its centre Connect/Disconnect button.
- The bottom-right **Connection details** panel with live download/upload rates, connection
  time, total traffic and this PC's internet state.
- SMS, USSD, APN, statistics, diagnostics and tray behaviour.

## Packages

| File | What it is | SHA-256 |
|---|---|---|
| `UZConnect-0.4.0-win-x64.exe` | Single-file app, no unzip and no .NET install | `132A5EEC486D4A1DD130AFD8DA1B21A9421BFA6085AF7B3DDF2802B0347D1C4B` |
| `UZConnect-0.4.0-win-x64.zip` | Portable folder build | `A82005FD84B42EBFE267C0A4948F50A3BDA4CCC1F5803D540F6FB6EB9FCDE902` |

The single-file build extracts its bundled SMS/Wi‑Fi helpers to
`%LOCALAPPDATA%\UZConnect\helpers` on first use.

## Verified on this machine

- Connect → disconnect → connect through the app three times: disconnected leaves no `rmnet`
  address and `mobile_data=0`; connect restores `mobile_data=1` and a live IP.
- Wi‑Fi page shows status, SSID, password, channel and connected devices with no editable
  controls.
- Windows startup entry removed and not recreated after launch.
- Build 0 warnings, 30/30 tests passing.
