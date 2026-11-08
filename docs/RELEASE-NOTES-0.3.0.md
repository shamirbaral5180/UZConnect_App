# UZConnect 0.3.0

Windows desktop manager for the **FY_UZ801_V3.2** USB modem (MSM8916, stock firmware V2.3.11).

## Fixed

- **“Connected” but no internet on the PC.** The stock firmware leaves IPv4 forwarding off and
  the NAT chain empty, so Wi‑Fi/RNDIS clients cannot reach the internet. UZConnect now repairs
  forwarding and adds the MASQUERADE rule after every connect, and verifies them. It also detects
  the data session over ADB when the vendor status page is unavailable.
- **Wi‑Fi ON/OFF.** The large toggle now works: off cancels the vendor refresh alarm and disables
  the access point; on re-arms the alarm and restarts it. Success requires the framework state,
  a running `hostapd` and a valid channel; the AP is restored if it does not stay off.
- **Startup behaviour.** Auto-connect now runs only when Windows starts UZConnect
  (`--minimized`). Opening the app yourself never connects automatically.
- **Tray behaviour.** Closing the window keeps the modem state in the notification area, connected
  or disconnected, until you choose Exit.

## Improved

- The classic first page is unchanged. Its **Connection details** panel now shows live download
  and upload rates (one-second samples), connection time, total traffic and whether this PC can
  reach the internet.

## Still limited by stock firmware

- Wi‑Fi password changes and hidden SSID remain disabled (the vendor service overwrites them and
  the networking service logs the passphrase).
- Channel, security mode and DHCP/DNS editing are read-only.

## Packages

| File | What it is | SHA-256 |
|---|---|---|
| `UZConnect-0.3.0-win-x64.exe` | Single-file app, no unzip and no .NET install | `BA536492EF77C135AC2F912F31861CD61ADF799102D4EF647CE642418F8833D1` |
| `UZConnect-0.3.0-win-x64.zip` | Portable folder build | `9F952E6794D543E945778EB0CD53B001EB3FF8538A94A5D211A426E06DFB051B` |

The single-file build extracts its bundled SMS/Wi‑Fi helpers to
`%LOCALAPPDATA%\UZConnect\helpers` on first use.

## Verified on this machine

- Forwarding and NAT applied automatically after the modem connected.
- Wi‑Fi off → access point stopped (`hostapd` gone, state disabled); Wi‑Fi on → AP back on
  channel 6 with the refresh alarm re-armed.
- Real-time details panel shows live rates and PC internet status.
- Build 0 warnings, 30/30 tests passing.
