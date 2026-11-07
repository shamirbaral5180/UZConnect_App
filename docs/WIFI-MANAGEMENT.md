# Wi-Fi Management — UZ801 V3.2, stock V2.3.11

This document records what UZConnect implements for Wi-Fi, how it is verified, and what is
deliberately unavailable. It does not repeat the firmware investigation; see
`HARDWARE-REPORT.md` for the vendor-service and watchdog analysis.

## Design

The dedicated **Wi-Fi Management** section uses a simple router-style layout: a blue header
strip, a grouped left navigation tree, underlined section titles, right-aligned labels and light
tables. Controls that the firmware cannot support are shown disabled rather than omitted, with a
short reason on the **Feature Availability** page.

Pages: Status, Wireless Settings, MAC Filtering, Wireless Advanced, Wireless Client List,
LAN / DHCP, Address Reservation, Feature Availability.

## Reading state

A root helper (`tools/wifi`) is pushed to `/data/local/tmp/uzconnect-wifi.jar` and executed with
`app_process`. It prints a fixed JSON shape containing no password, PSK, serial or raw
configuration:

- active AP state from the Wi-Fi service, and whether `hostapd` is running;
- network name, channel, maximum clients and broadcast state from the running AP configuration;
- whether a password is configured (a boolean only);
- IPv4 gateway of `br0`, and whether `rndis0` is bridged;
- DHCP ranges parsed from the running `dnsmasq` command line;
- associated Wi-Fi stations with name, IP (from the ARP table) and MAC.

The desktop app rejects a failed or unknown helper response instead of showing partial values.
`PasswordConfigured` is `null` rather than `false` when the configuration cannot be read.

## Changing settings

Only the network name and maximum-device limit are offered. Both travel through the vendor
control channel (`funcNo 1007` save, `funcNo 1008` restart) inside a transaction:

1. Read and verify the **current** settings; abort if they are not confirmed.
2. Back up `mificonfig.json`, `hostapd.conf` and `softap.conf` to a mode-700 directory, verify
   the copies byte-for-byte, and record SHA-256 hashes.
3. Save and restart.
4. Require three consecutive agreeing observations across more than one watchdog interval:
   active name and limit, saved name and limit, the vendor status endpoint, `apState = 13`,
   a running `hostapd`, readable configuration and `rndis0` still bridged.
5. On any failure, restore the original with an independent 90-second recovery token and verify
   it. If recovery cannot be confirmed, report **Recovery required** and advise against retrying.

A lost or timed-out response is not treated as failure or success; saved state is re-read before
the result is reported.

## Verification performed on the modem

| Check | Result |
|---|---|
| Read active state, security, channel, password presence, clients, ranges | Passed |
| Client name, IP and MAC reported for an associated phone/laptop | Passed |
| Apply temporary name and limit | Passed — confirmed in active state, saved state and vendor status |
| Restore original settings | Passed — confirmed the same way |
| Survive a modem restart | Passed — temporary settings were still active and saved after reboot |
| Rollback on injected verification failure | Passed — original restored on the device, AP and USB bridge healthy |
| Save initiated from the Wi-Fi screen | Passed — a network-name change saved from the UI was confirmed on the modem, with the USB bridge still up |
| Restore initiated from the Wi-Fi screen | Passed — the original name and device limit were written back from the UI and confirmed |
| Maximum-devices change from the UI | Passed — limit changes were applied and confirmed |
| USB RNDIS during and after changes | Passed — vendor control endpoint reachable |
| Mobile data | Passed — reported connected after reboot |
| SMS provider read and USSD over the AT port | Passed after the change cycle |
| Secrets in logs | None found |

Verification takes roughly 30 seconds because it spans more than one 20-second watchdog interval.
The screen reports that time as a busy state; a stalled operation is bounded so the page cannot
remain locked indefinitely.

## UI save flow

Pressing **Save** asks for confirmation, then runs the same transaction. The result is reported
only after the modem state is re-read; a lost HTTP response alone never produces a success or a
failure message. The screen then refreshes so the fields show what the modem actually holds.

## Unavailable, with reasons

- **Wi-Fi ON/OFF:** the vendor watchdog re-enables the AP. An interface-only attempt
  (`ip link set wlan0 down`) was also reverted within one interval, while USB control and mobile
  data stayed up. No reliable reversible path is confirmed, so the buttons are disabled.
- **Password change:** the vendor setter logs the supplied password. A secure change cannot be
  guaranteed, so no password control is offered.
- **Security mode, channel, hidden SSID, MAC filtering:** the inspected vendor controls either do
  not apply the requested value or cannot be verified; shown read-only.
- **DHCP range, lease, gateway, DNS, reservations:** the vendor setter does not reach the running
  address server, and `br0` is shared with USB tethering. Shown read-only to avoid breaking both
  Wi-Fi and USB connectivity.

## Second qualification round — ON/OFF, password and SSID visibility

Three further mechanisms were tested on the modem. All three are overridden by stock components,
so the controls remain disabled.

### Wi-Fi ON/OFF

| Attempt | Result |
|---|---|
| Cancel the 20 s vendor alarm, then framework AP disable | Stays off while the alarm is cancelled, but re-enabling does not reliably broadcast; the access point came back on the framework default name instead of the configured one. |
| Cancel the alarm, then stop `hostapd` only | `netd` restarts `hostapd` within about 40 s; AP returns. |
| Restore the vendor alarm helper | Intermittent process abort until the helper was changed to halt cleanly; the alarm itself still does not solve the disable path. |

The framework disable is the only state that keeps the AP silent, and the firmware does not
restore a working beacon afterwards without a reboot. The vendor service also adopted the
framework default SSID into its own saved configuration during the test; the original name was
restored through the verified SSID transaction.

### Password

A privileged path was built that writes the framework AP configuration directly (no vendor
setter), updates the vendor JSON, restarts the AP and verifies by SHA-256 without printing the key.
It applied and the AP used the new key, but:

- `netd` logs the passphrase on every access-point start:
  `CMD INPUT [set wpa_passphrase=…]`, `Cmd: wpa_passphrase Argument :…`,
  `Updated:wpa_passphrase=…`. This is stock behavior and also happens on a normal restart with
  the existing password.
- The vendor service overwrote the framework key from its in-memory configuration on the next
  bind, so the new key was not durable.

### SSID visibility (hidden)

Writing `hiddenSSID` to the framework configuration and the vendor JSON, then restarting the AP,
was reverted on the next vendor bind: both the active `ignore_broadcast_ssid` and the framework
flag returned to visible. The vendor applies its in-memory hidden flag on every AP bind and has no
endpoint that updates that memory.

### Required changes — approval needed

These features cannot be completed safely without changing protected firmware components:

1. **`netd`**: redact `wpa_passphrase` in its hostapd control logging. This is a native system
   binary; replacing it requires a matching build for this device and a writable system partition.
2. **`MifiService`** (`/system/priv-app/MifiService.apk`): stop auto-binding the AP when Wi-Fi is
   intentionally off, accept hidden-SSID and password changes without logging, and persist them.
   The APK is platform-signed and holds system permissions; rebuilding it requires the OEM signing
   key, which is not available.
3. Without those changes, the only alternative is to replace the vendor Wi-Fi stack with an
   independent hostapd/DHCP implementation. That would remove the APN, connection and status
   management UZConnect depends on and is not recommended.

No system file, partition or signature was modified. The controls stay disabled until the required
changes are approved.

## Qualification harness

`tools/wifi/verify` is a console project used for the tests above. It prints only condition
results, never SSIDs, MACs, serials or secrets.

```
dotnet run --project tools/wifi/verify/WifiVerify.csproj -c Release
```

Additional arguments are opt-in and change modem state: `--diagnose`, `--rollback-test`,
`--apply-temp`, `--persist-check`, `--restore-from <backupId>`, `--approved-basic-test`.

Build the helper with `powershell -File tools/wifi/build.ps1`. The jar is copied into the app
output as `Assets/uzconnect-wifi.jar`.
