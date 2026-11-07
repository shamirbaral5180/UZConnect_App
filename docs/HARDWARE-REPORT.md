# Phase 1 hardware report — FY_UZ801_V3.2, stock V2.3.11

Investigation date: 2026-11-05/06. All tests were read-only unless stated. No firmware,
partition, bootloader or root changes were made.

## 1. Windows USB enumeration

Present devices (Windows PnP):

```
Qualcomm USB Composite Device 90B6 (0031)
  InstanceId: USB\VID_05C6&PID_90B6\<redacted-serial>
  Bus reported description: Android
  Children:
    MI_00  Remote NDIS based Internet Sharing Device   (usbrndis6)     RNDIS
    MI_02  Qualcomm HS-USB Modem 90B6                  (Modem)         AT/data modem function
    MI_03  Qualcomm HS-USB Diagnostics 90B6 (COM12)     (Ports)         DIAG
    MI_04  ADB Interface                               (WINUSB)        ADB
```

No VID/PID was assumed; these values were read from Windows. The same modem can expose a
different composition after a mode change, so detection is based on the ADB interface and
the serial number, not on a fixed ID.

## 2. ADB

`adb devices -l` reports:

```
<redacted-serial>  device product:msm8916_32_512 model:UZ801 device:msm8916_32_512
```

Properties:

```
ro.product.model          UZ801
ro.product.manufacturer   Qualcomm Technology
ro.build.version.release  4.4.4
ro.build.version.sdk      19
ro.build.display.id       V2.3.11
ro.board.platform         msm8916
ro.serialno               <redacted>
gsm.version.baseband      N958St_Z85_CN_JSXPH1IDN1H213
```

`id` reports `uid=0(root)`. Root ADB is what makes SMS content-provider access and the local
helper install possible on this stock image. The application treats root as a discovered
capability, not an assumption.

Hardware revision `FY_UZ801_V3.2` is a product-label value; the firmware does not expose an
equivalent property. The UI labels the source accordingly rather than inventing a value.

## 3. SMS

The device runs Android 4.4.4 and has the standard SMS provider. With root ADB:

```
content query --uri content://sms/inbox --projection _id:address:date:read:body
```

returned real messages after a SIM was inserted. The SMS database is
`/data/data/com.android.providers.telephony/databases/mmssms.db` and was inspected only via a
read-only pull. Inbox contents were not written to logs.

Deletion and read flags use `content update/delete` with explicit confirmation.
Sending is not exposed by a supported shell command on this firmware, so a minimal helper
agent (`tools/agent`) uses Android's `SmsManager`. It was built with the installed Android SDK
and JDK, installed over ADB, and its ping and dump actions were verified. On Android 4.4 the
`SmsManager` PendingIntent can report `RESULT_ERROR_GENERIC_FAILURE` after the message has
already been written to `content://sms/sent`. The helper therefore confirms a reported
failure against the device Sent folder, and the desktop app waits for the terminal agent
result (ignoring the immediate `queued` record) and performs the same Sent-folder check.
A self-test to the SIM's own number verified that the UI reports **Sent** and the message is
present in both Sent and Inbox.

## 4. SIM / network

With the Ncell SIM inserted:

```
gsm.sim.state       READY
gsm.operator.alpha  Ncell
gsm.operator.numeric 42902
gsm.network.type    LTE
mServiceState       0 0 home Ncell Ncell 42902 LTE LTE ... EmergOnly=false
```

The SIM phonebook (`content://icc/adn`) returned no contacts. SIM writes were not attempted.
Signal strength was reported by the firmware as unknown values
(`99` / `2147483647`), so the application shows `Not available` rather than estimating a dBm.

## 5. Stock control channel (USB RNDIS)

The stock MifiService exposes an HTTP JSON endpoint on the modem's bridge address (discovered
as `192.168.100.1` from the device's `br0`; the Windows RNDIS adapter is on the same subnet).
The endpoint was exercised from the Windows host:

- `funcNo 1001` — operator, network type, connection status, RSSI level.
- `funcNo 1002` — connection IP/DNS and Wi-Fi status fields.
- `funcNo 1003` — upload/download counters and client count.
- `funcNo 1004` — connect/disconnect (`conn_mode` string `"0"`/`"1"`), verified to change
  `netstatus` in both directions.
- `funcNo 1006` — Wi-Fi status/SSID/max clients.
- `funcNo 1016` — APN profile list.
- `funcNo 1017` — save APN profile; `auth` must be a string.
- `funcNo 1018` — connection action for a profile.
- `funcNo 1005` — network mode (`net_mode` string; `"1"` selects 3G).

An Ncell profile (`name=Ncell, apn=web, auth=0`) was saved and the connection came up with a
real `rmnet0` address and DNS servers. The web interface is used only as a control channel
after USB/ADB detection succeeds; it is never used as a substitute for USB detection.

## 5b. USSD

The Android dialer/MMI path is not functional on this stock image. Even the local code
`*#06#` returns `出现连接问题或 MMI 码无效。` (“connection problem or invalid MMI code”),
and the hidden `ITelephony.handlePinMmi` returns `false` for every tested code.

The modem exposes a second serial endpoint on **COM11** (`MI_02`, Qualcomm HS-USB Modem).
It answers `AT` at 115200 baud (the modem auto-bauds) and supports USSD directly:

```
AT+CUSD=1,"*901#",15
+CUSD: 1,"Balance : Rs. 60.44 Expiry date: 2026-12-05.
1 >Rs50-Unlimited Data+100SMS-1D
2 >Rs50 100 mins-3Day
3 >Rs30-Upto Rs10,000 hospitalization insurance",15
```

USSD over LTE returns `+CME ERROR: no network service` on this modem. The app therefore
switches temporarily to 3G (`funcNo 1005`, `net_mode "1"`), waits for HSPA registration,
sends the CUSD request, and restores the saved network mode (Auto or 4G) afterwards. The
balance result is read from the modem and shown in English; the AT port is not exposed in
the normal UI.

## 6. Wi-Fi

`hostapd` is running as a stock process, with SSID `4G-UFI-465` visible to Windows. The stock
web UI reports status and allows SSID/password changes. It does **not** expose a reliable
ON/OFF action.

Tests:

- `svc wifi disable/enable` does not stop hostapd.
- Killing hostapd stops it briefly, but the vendor service restarts it within a few seconds.
- Freezing the vendor service and killing hostapd also does not keep the AP off; another
  system component restarts it.
- `hostapd_cli` has no disable/enable command in this build; `iwpriv wlan0 stopbss` did not
  remove the visible SSID.
- Starting hostapd manually works and is used for the ON action.

Therefore UZConnect shows Wi-Fi status and a real ON action, while OFF reports that the stock
firmware restarts the access point. This is deliberately not faked.

## 7. Other observations

- The only confirmed serial endpoint used by the app is the Qualcomm modem command port on
  COM11 (`MI_02`). It answers `AT` and supports `AT+CUSD`; the COM12 diagnostics port does not
  answer AT.
- The RNDIS interface is present but is not used as proof that the modem is connected.
- SMS was read and sent while the modem was on the USB ADB transport.
- The user's SIM was inserted during testing; the app never sends messages or modifies SMS
  data without an explicit user action.
