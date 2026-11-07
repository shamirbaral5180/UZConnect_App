# UI reference and implementation mapping

Reference: D-Link DWM-222 Connection Manager screenshots supplied by the project owner and the
DWM-222 user manual (`DWM-222_man_reva1_Manual_en.pdf`).

## Window chrome

- Square classic window, narrow dark-blue gradient title bar.
- Title: **UZConnect Connection Manager** with the UZConnect/globe icon.
- Standard minimize/maximize/close controls at the right.
- No sidebar, no ribbon, no modern cards, no rounded dashboard.

## Toolbar

A single horizontal icon bar immediately below the title bar, on a dark indigo→blue gradient.
Seven white line-art icons in the original order:

1. Connection (globe)
2. SMS
3. Contacts
4. USSD
5. Statistics
6. Settings
7. Help

The icons are the original D-Link artwork extracted from the manual, converted to white line
art. The selected icon has a subtle light overlay; labels are tooltips.

## Connection screen

- Pale blue/white gradient content with faint globe watermarks at bottom-left and top-right.
- Large centered **Connect/Disconnect** button with globe/check artwork.
- Connection result/status and IP address directly below the button.
- Lower-right upload/download arrows and live KB/s values.
- Bottom status bar: device status, network/operator, connection state, unread SMS count.

## SMS screen

- Blue section header `Text Message: <folder>`.
- Left folder tree: Inbox, Outbox, Draft, Recycle; `Local`/`DataCard` labels.
- Message table columns: checkbox, Name/Number, Content, Date/Time, with classic grid header.
- Bottom detail strip: Name, Number (and date in the data model).
- Small action bar: Find, New, Reply, Forward, Delete, Export, Refresh.
- Compose is a classic child dialog with To, message body, character/segment count, Send,
  Save to Draft, Cancel. Recipients can be picked from the SIM contact list (`Send to…`).
- No chat bubbles or conversation cards.

## Contacts screen

- Blue `Contacts: DataCard` header and a classic list.
- SIM phonebook is read-only; the requested scope excludes contact management.
- New/Edit/Delete are intentionally disabled. No import/export/copy controls were added.

## USSD screen

- Left: `USSD result` box, `Enter USSD Number`, Add/Send.
- Right: `USSD command list` table (Name/Code) with Edit/Delete.
- Commands are stored locally. Default Ncell commands (Balance *901#, Data packs *101#,
  Own number *903#) are added when they are not already present.
- Sending uses the modem's own `AT+CUSD` command channel, not the broken Android dialer/MMI
  handler. The app switches temporarily to 3G because this modem rejects CUSD over LTE, keeps
  3G while a menu is open, and restores the saved network mode when the session ends or is
  cancelled. The real balance text is displayed; nothing is fabricated.
- After a menu response the Send button becomes **Reply** and the input is cleared for the
  option number. A **Cancel** button ends the session and restores the network mode. Typing
  `1`, `2`, `0`, and so on navigates the operator menu, including data-package purchase.

## Statistics screen

- Left classic tree/list: Transfer time, Upload/Download speed and data, Daily/Monthly/Yearly
  totals, last reset note.
- Right: black chart with cyan grid, yellow upload line and green download line, legend.
- Only values reported by the modem are shown; monthly/yearly values that the modem does not
  expose are shown as `Not available`.

## Settings screen

- Left folder list, matching the D-Link order: General, Text Message Setting, Profile
  Management, Network, Statistics, Diagnostics, Connection Type. Wi-Fi Management is the
  only added section and is placed at the end.
- General: connect sound prompts and auto reconnect.
- Text Message Setting: “Text message handling”, prompt-on-new-message checkbox, sound file
  name with a **Browser...** button, SMS Delivery Report, and New Message Save Mode
  (Save in local / Save on (U)SIM). Preferences are stored locally.
- Profile Management: a “Connection configuration” tab with Profile Name, Parameter, APN,
  Access number, User name, Password, Authentication type, and the D-Link button column
  (New, Delete, Edit, Save, Undo, Set as default, Import). Profiles are stored by the modem;
  the access number and default marker are local settings.
- Connection Type: RAS / NDIS radio buttons, with NDIS selected because that is the USB
  modem network adapter used by UZConnect.
- Network: network mode Auto/3G/4G.
- Diagnostics: ordinary device/SIM/network status only. No USB IDs, COM ports, ADB details,
  AT commands or debug logs appear in the normal UI.
- Wi-Fi Management: current status, Turn Wi-Fi ON, Turn Wi-Fi OFF, Refresh and operation
  feedback. ON uses the stock hostapd process. OFF reports the firmware's automatic restart
  limitation rather than showing a false success.
- The rest of the original organization is kept; unsupported PIN management is labelled
  unsupported.

## Intentional differences

- D-Link branding is replaced with UZConnect branding.
- Wi-Fi Management is the only added section, as requested.
- Contact editing/import/export is omitted per the latest scope instruction.
- The faint earth watermarks are approximated with the D-Link globe artwork because the
  original raster background was not supplied as an asset.
- Header action icons in SMS/Contacts are small classic icon buttons generated as PNG assets so
  they render reliably on Windows; the surrounding layout and colors follow the reference.
