using UZConnect.Core.Models;
using UZConnect.Windows.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("UZConnect hardware probe");
Console.WriteLine("========================");

using var session = new ModemSession();
session.StateChanged += (_, e) =>
{
    if (e.Reason is not ("scan" or "refresh" or "sms-sync"))
    {
        Console.WriteLine($"[state] {e.Reason}");
    }
};

await session.InitializeAsync();
if (args.Contains("--wifi-off") || args.Contains("--wifi-on"))
{
    var on = args.Contains("--wifi-on");
    var wifi = await session.SetWifiEnabledAsync(on);
    Console.WriteLine($"Wi-Fi {(on ? "ON" : "OFF")}: supported={wifi.Supported} enabled={wifi.Enabled} message={wifi.Message}");
    session.Dispose();
    return;
}
Console.WriteLine($"ADB: {session.AdbPath ?? "not found"}");
Console.WriteLine($"USB devices enumerated: {session.UsbDevices.Count}");
foreach (var usb in session.UsbDevices)
{
    Console.WriteLine($"  - {usb.DisplayName} {usb.VidPid} serial={usb.SerialNumber ?? "-"} adb={usb.HasAdb}");
    foreach (var iface in usb.Interfaces)
    {
        Console.WriteLine($"      {iface.RoleLabel,-12} {iface.Name} {(iface.ComPort is null ? "" : $"({iface.ComPort})")}");
    }
}

Console.WriteLine();
Console.WriteLine($"Candidates: {session.Devices.Count}");
foreach (var device in session.Devices)
{
    Console.WriteLine($"  - {device.DisplayName} authorized={device.IsAuthorized} adbSerial={device.AdbSerial ?? "-"}");
}

Console.WriteLine();
Console.WriteLine($"Active: {session.ActiveDevice?.DisplayName ?? "none"}");
Console.WriteLine($"Connected: {session.IsConnected}");
Console.WriteLine($"Last error: {session.LastError ?? "none"}");

if (session.IsConnected)
{
    var info = session.DeviceInfo;
    Console.WriteLine($"Model: {info.Model}");
    Console.WriteLine($"Manufacturer: {info.Manufacturer}");
    Console.WriteLine($"Hardware: {info.HardwareRevision} ({info.HardwareRevisionSource})");
    Console.WriteLine($"Firmware: {info.FirmwareVersion}");
    Console.WriteLine($"Android: {info.AndroidVersion} API {info.ApiLevel}");
    Console.WriteLine($"Build: {info.BuildId}");
    Console.WriteLine($"Baseband: {info.BasebandVersion}");
    Console.WriteLine($"IMEI: {info.MaskedImei}");
    Console.WriteLine($"SIM: present={session.SimInfo.IsPresent} state={session.SimInfo.State} operator={session.SimInfo.Operator ?? "Not available"}");
    Console.WriteLine($"Network: {session.NetworkInfo.RegistrationState} / {session.NetworkInfo.Operator} / {session.NetworkInfo.NetworkType} / {session.NetworkInfo.SignalDisplay}");
    Console.WriteLine($"Data: enabled={session.ConnectionStatus.MobileDataEnabled} state={session.ConnectionStatus.State}");
    Console.WriteLine($"Capabilities: smsRead={session.Capabilities.CanReadSms} smsSend={session.Capabilities.CanSendSms} smsDelete={session.Capabilities.CanDeleteSms} simContacts={session.Capabilities.CanReadSimContacts}");
    Console.WriteLine($"Agent installed: {await session.IsSmsAgentInstalledAsync()}");

    foreach (var folder in new[] { SmsFolder.Inbox, SmsFolder.Sent })
    {
        var result = await session.SyncMessagesAsync(folder);
        Console.WriteLine($"Sync {folder}: {result.Retrieved} retrieved, {result.Added} added, {result.Updated} updated, {result.Removed} removed");
        var cached = await session.GetCachedMessagesAsync(folder);
        Console.WriteLine($"Cached {folder}: {cached.Count}");
    }

    var contacts = await session.GetSimContactsAsync();
    Console.WriteLine($"SIM contacts: {contacts.Count}");

    var apns = await session.GetApnsAsync();
    Console.WriteLine($"APNs for current operator: {apns.Count}");
    foreach (var apn in apns.Take(5))
    {
        Console.WriteLine($"  - {apn.Name} / {apn.Apn} / {apn.Type} / current={apn.IsCurrent}");
    }

    var report = await session.ExportReportAsync();
    Console.WriteLine($"Report: {report}");
}

Console.WriteLine();
Console.WriteLine("Diagnostics tail:");
foreach (var entry in session.Diagnostics.TakeLast(15))
{
    Console.WriteLine($"  {entry.TimeDisplay} {entry.LevelLabel,-5} [{entry.Category}] {entry.Message}");
}

session.Dispose();

