using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UZConnect.Core.Abstractions;
using UZConnect.Core.Models;
using UZConnect.Core.Parsing;

namespace UZConnect.Windows.Adb;

/// <summary>
/// Verified transport for the stock UZ801 firmware: USB ADB plus Android shell services.
/// SMS read/delete uses the platform content provider; SMS send uses a small, separately
/// installed helper agent because the stock shell has no supported SmsManager entry point.
/// </summary>
public sealed partial class AdbModemProvider : IModemProvider, ISmsCapable, ISimCapable, INetworkCapable, IConnectionCapable
{
    public const string AgentPackage = "com.uzconnect.agent";
    public const string AgentReceiver = "com.uzconnect.agent/.AgentReceiver";
    private const int MaxMessagesPerSync = 500;

    private readonly AdbClient _adb;
    private readonly IUsbDeviceService _usb;
    private readonly IDiagnosticsLog _log;
    private ModemDevice? _activeDevice;
    private bool _serverStarted;

    public AdbModemProvider(AdbClient adb, IUsbDeviceService usb, IDiagnosticsLog log)
    {
        _adb = adb;
        _usb = usb;
        _log = log;
    }

    public string Id => "adb";
    public string DisplayName => "Android Debug Bridge (ADB)";
    public ModemProviderKind Kind => ModemProviderKind.Adb;

    /// <summary>
    /// ADB itself is verified on this hardware, but individual capabilities are reported
    /// separately from GetCapabilitiesAsync.
    /// </summary>
    public bool IsVerified => true;

    public bool IsConnected => _activeDevice is not null;
    public ModemDevice? ActiveDevice => _activeDevice;
    public AdbClient Client => _adb;

    public async Task<IReadOnlyList<ModemDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!_adb.IsAvailable)
        {
            _log.Warning("ADB", "ADB executable not found; USB-only view remains available.");
            return Array.Empty<ModemDevice>();
        }

        if (!_serverStarted)
        {
            var server = await _adb.StartServerAsync(cancellationToken).ConfigureAwait(false);
            _serverStarted = true;
            if (!server.Success)
            {
                _log.Warning("ADB", $"ADB server did not start cleanly: {FirstLine(server.StandardError)}");
            }
        }

        var adbDevices = await _adb.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        var usbDevices = await _usb.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ModemDevice>();
        var usedUsb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in adbDevices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usb = FindUsbForAdb(usbDevices, entry, usedUsb);
            if (usb is not null)
            {
                usedUsb.Add(usb.InstanceId);
            }

            var props = entry.IsReady
                ? GetPropParser.Parse(await _adb.ShellTextAsync(entry.Serial, "getprop", cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var model = GetPropParser.Value(props, "ro.product.model", "Not available");
            var displayName = entry.IsReady
                ? $"{model} · {entry.Serial}"
                : $"{usb?.DisplayName ?? "Android USB device"} · {entry.State}";

            results.Add(BuildDevice(entry, usb, displayName, model,
                props.TryGetValue("ro.build.display.id", out var build) ? build : null));
        }

        // Interfaces that enumerate in Windows but are not visible to the adb server
        // (driver missing, adb server unavailable, or authorization pending) are shown
        // as detected hardware, never as a connected modem.
        foreach (var usb in usbDevices.Where(u => !usedUsb.Contains(u.InstanceId)))
        {
            if (!usb.HasAdb && !usb.HasModem)
            {
                continue;
            }

            var model = "Not available";
            results.Add(new ModemDevice(
                ModemDevice.MakeId(ModemProviderKind.Adb, usb.InstanceId),
                $"{usb.DisplayName} · ADB interface present",
                ModemProviderKind.Adb,
                usb.VendorId,
                usb.ProductId,
                usb.SerialNumber,
                null,
                usb.Manufacturer,
                model,
                null,
                usb.Interfaces.FirstOrDefault(i => i.ComPort is not null && i.Role == UsbInterfaceRole.Serial)?.ComPort,
                usb.InstanceId,
                usb.HardwareIds,
                usb.Interfaces,
                false));
        }

        _log.Info("ADB", $"Detection: {adbDevices.Count} adb device(s), {results.Count} modem candidate(s).");
        return results;
    }

    private static UsbDeviceInfo? FindUsbForAdb(
        IReadOnlyList<UsbDeviceInfo> usbDevices,
        AdbDeviceEntry entry,
        HashSet<string> used)
    {
        var bySerial = usbDevices.FirstOrDefault(u =>
            !used.Contains(u.InstanceId) &&
            u.HasAdb &&
            (string.Equals(u.SerialNumber, entry.Serial, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(u.InstanceId.Split('\\').LastOrDefault(), entry.Serial, StringComparison.OrdinalIgnoreCase)));
        if (bySerial is not null)
        {
            return bySerial;
        }

        return usbDevices.FirstOrDefault(u => !used.Contains(u.InstanceId) && u.HasAdb);
    }

    private static ModemDevice BuildDevice(
        AdbDeviceEntry entry,
        UsbDeviceInfo? usb,
        string displayName,
        string model,
        string? firmware)
    {
        return new ModemDevice(
            ModemDevice.MakeId(ModemProviderKind.Adb, entry.Serial),
            displayName,
            ModemProviderKind.Adb,
            usb?.VendorId,
            usb?.ProductId,
            usb?.SerialNumber ?? entry.Serial,
            entry.Serial,
            usb?.Manufacturer,
            model,
            firmware,
            usb?.Interfaces.FirstOrDefault(i => i.ComPort is not null && i.Role == UsbInterfaceRole.Serial)?.ComPort,
            usb?.InstanceId,
            usb?.HardwareIds ?? Array.Empty<string>(),
            usb?.Interfaces ?? Array.Empty<UsbInterfaceInfo>(),
            entry.IsReady);
    }

    public async Task ConnectAsync(ModemDevice device, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(device.AdbSerial))
        {
            throw new InvalidOperationException(
                "The USB device is visible but ADB is not authorized or not running. " +
                "Enable ADB/developer mode on the modem and accept the authorization prompt.");
        }

        var state = await _adb.GetStateAsync(device.AdbSerial, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(state.Trim(), "device", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ADB device state is '{state.Trim()}', not 'device'. USB detection is not the same as a usable modem session.");
        }

        _activeDevice = device;
        _log.MarkCommunicationSuccess("ADB", $"Connected to {device.DisplayName} over ADB.");
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDevice is not null)
        {
            _log.Info("ADB", $"Disconnected session for {_activeDevice.DisplayName}.");
        }

        _activeDevice = null;
        return Task.CompletedTask;
    }

    public async Task<DeviceInformation> GetDeviceInformationAsync(CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();
        var propsOutput = await _adb.ShellTextAsync(serial, "getprop", cancellationToken: cancellationToken).ConfigureAwait(false);
        var props = GetPropParser.Parse(propsOutput);
        var socMachine = FirstLine(await _adb.ShellTextAsync(serial, "cat /sys/devices/soc0/machine", cancellationToken: cancellationToken).ConfigureAwait(false));
        var socRevision = FirstLine(await _adb.ShellTextAsync(serial, "cat /sys/devices/soc0/revision", cancellationToken: cancellationToken).ConfigureAwait(false));
        var imeiDump = await _adb.ShellTextAsync(serial, "dumpsys iphonesubinfo", cancellationToken: cancellationToken).ConfigureAwait(false);
        var imei = IphoneSubInfoParser.ParseImei(imeiDump);

        var model = GetPropParser.Value(props, "ro.product.model");
        var explicitHw = FirstNonEmpty(
            props.TryGetValue("ro.product.hardware", out var hw1) ? hw1 : null,
            props.TryGetValue("ro.hw.revision", out var hw2) ? hw2 : null,
            props.TryGetValue("ro.board.revision", out var hw3) ? hw3 : null,
            props.TryGetValue("persist.radio.hw.revision", out var hw4) ? hw4 : null);

        var hardwareRevision = explicitHw;
        var hardwareSource = "Firmware property";
        if (string.IsNullOrWhiteSpace(hardwareRevision))
        {
            hardwareRevision = string.Equals(model, "UZ801", StringComparison.OrdinalIgnoreCase)
                ? "FY_UZ801_V3.2"
                : "Not available";
            hardwareSource = string.Equals(model, "UZ801", StringComparison.OrdinalIgnoreCase)
                ? "Product label (firmware does not expose a revision)"
                : "Not available";
        }

        if (!string.IsNullOrWhiteSpace(socMachine) || !string.IsNullOrWhiteSpace(socRevision))
        {
            _log.Trace("ADB", $"SoC: {socMachine} revision {socRevision} (not used as a product revision).");
        }

        var info = new DeviceInformation(
            model,
            GetPropParser.Value(props, "ro.product.manufacturer"),
            hardwareRevision,
            hardwareSource,
            GetPropParser.Value(props, "ro.build.display.id"),
            GetPropParser.Value(props, "ro.build.version.release"),
            GetPropParser.Value(props, "ro.build.id"),
            GetPropParser.Value(props, "ro.build.description"),
            GetPropParser.Value(props, "ro.board.platform"),
            GetPropParser.Value(props, "ro.serialno"),
            imei,
            GetPropParser.Value(props, "gsm.version.baseband"),
            int.TryParse(GetPropParser.Value(props, "ro.build.version.sdk", "0"), out var api) ? api : 0,
            GetPropParser.Value(props, "ro.product.name"),
            GetPropParser.Value(props, "ro.board.platform"));

        _log.MarkCommunicationSuccess("ADB", $"Device information refreshed for {info.Model}.");
        return info;
    }

    public async Task<ModemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDevice?.AdbSerial is null)
        {
            return ModemCapabilities.None;
        }

        var serial = _activeDevice.AdbSerial;
        var idOutput = await _adb.ShellTextAsync(serial, "id", cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = idOutput.Contains("uid=0(root)", StringComparison.OrdinalIgnoreCase);

        var smsQuery = await _adb.ShellAsync(
            serial,
            "content query --uri content://sms/inbox --projection _id",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        var simQuery = await _adb.ShellAsync(
            serial,
            "content query --uri content://telephony/siminfo --projection _id",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        var iccQuery = await _adb.ShellAsync(
            serial,
            "content query --uri content://icc/adn --projection _id:name:number",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        var dataHelp = await _adb.ShellAsync(serial, "svc data", TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        var agentInstalled = await IsSmsAgentInstalledAsync(cancellationToken).ConfigureAwait(false);
        var apnQuery = await _adb.ShellAsync(
            serial,
            "content query --uri content://telephony/carriers/preferapn --projection _id:name:apn",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        var notes = new List<string>
        {
            "USB detection and ADB communication are verified on stock firmware V2.3.11.",
            root
                ? "ADB shell is running as root; content-provider reads and deletes are available."
                : "ADB shell is not root; some content-provider operations may be refused.",
            agentInstalled
                ? "SMS sending helper agent is installed."
                : "SMS reading works without installation; sending requires the one-time helper agent.",
            "Data enable/disable uses Android's `svc data` command; the end-to-end effect was not verified because no SIM was present."
        };

        return new ModemCapabilities(
            "ADB",
            true,
            smsQuery.Success,
            agentInstalled,
            smsQuery.Success && root,
            iccQuery.Success,
            false,
            dataHelp.Success || root,
            true,
            apnQuery.Success,
            true,
            notes);
    }

    public async Task<CommandResult> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return CommandResult.Failed(command, "Command is empty.", TimeSpan.Zero);
        }

        if (!IsDiagnosticCommandAllowed(command.Trim()))
        {
            return CommandResult.Failed(
                command,
                "Command blocked by policy. Diagnostics accepts read-only getprop/dumpsys/settings get/content query/ls/cat/id commands.",
                TimeSpan.Zero);
        }

        var result = await _adb.ShellAsync(RequireSerial(), command.Trim(), TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);
        return new CommandResult(result.Success, command, result.StandardOutput, result.Success ? null : FirstLine(result.StandardError), result.Duration);
    }

    public async Task<ConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();
        var propsOutput = await _adb.ShellTextAsync(serial, "getprop", cancellationToken: cancellationToken).ConfigureAwait(false);
        var props = GetPropParser.Parse(propsOutput);
        var telephonyDump = await _adb.ShellTextAsync(serial, "dumpsys telephony.registry", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var telephony = TelephonyParser.Parse(telephonyDump, props);
        var connectivityDump = await _adb.ShellTextAsync(serial, "dumpsys connectivity", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var connectivity = ConnectivityParser.Parse(connectivityDump);
        var mobileData = FirstLine(await _adb.ShellTextAsync(serial, "settings get global mobile_data", cancellationToken: cancellationToken).ConfigureAwait(false));
        var netDev = await _adb.ShellTextAsync(serial, "cat /proc/net/dev", cancellationToken: cancellationToken).ConfigureAwait(false);
        var usage = DataUsageParser.ParseProcNetDev(netDev);
        var ip = await GetIpAddressAsync(serial, cancellationToken).ConfigureAwait(false);

        var dataConnected = string.Equals(connectivity.State, "CONNECTED", StringComparison.OrdinalIgnoreCase) ||
                            telephony.DataConnectionState == 2;
        var enabled = mobileData.Trim() == "1" || connectivity.DataEnabled == true || telephony.DataPossible;

        return new ConnectionStatus(
            enabled,
            dataConnected,
            telephony.DataState,
            telephony.DataApn,
            ip,
            null,
            usage.RxBytes,
            usage.TxBytes);
    }

    public async Task SetMobileDataAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();
        var command = enabled ? "svc data enable" : "svc data disable";
        var result = await _adb.ShellAsync(serial, command, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Mobile data command failed: {FirstLine(result.StandardError)}");
        }

        _log.Info("Connection", $"Requested mobile data {(enabled ? "enable" : "disable")} using `{command}`.");
    }

    public async Task<SimInfo> GetSimInfoAsync(CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();
        var state = FirstLine(await _adb.ShellTextAsync(serial, "getprop gsm.sim.state", cancellationToken: cancellationToken).ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(state))
        {
            state = "UNKNOWN";
        }

        var output = await _adb.ShellTextAsync(
            serial,
            "content query --uri content://telephony/siminfo --projection _id:icc_id:number:display_name:carrier_name:country_iso",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        var rows = ContentQueryParser.Parse(output);
        var props = GetPropParser.Parse(await _adb.ShellTextAsync(serial, "getprop", cancellationToken: cancellationToken).ConfigureAwait(false));
        var operatorFallback = GetPropParser.Value(props, "gsm.operator.alpha", string.Empty);
        var sim = ContentQueryParser.ToSimInfo(rows, state, string.IsNullOrWhiteSpace(operatorFallback) ? null : operatorFallback);

        if (!sim.IsPresent && string.Equals(state, "ABSENT", StringComparison.OrdinalIgnoreCase))
        {
            _log.Info("SIM", "SIM is absent according to gsm.sim.state.");
        }

        return sim;
    }

    public async Task<IReadOnlyList<SimContact>> GetSimContactsAsync(CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();
        var output = await _adb.ShellTextAsync(
            serial,
            "content query --uri content://icc/adn --projection _id:name:number:emails",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        return ContentQueryParser.ToSimContacts(ContentQueryParser.Parse(output));
    }

    public Task AddSimContactAsync(string name, string number, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "Writing SIM contacts has not been verified on the stock V2.3.11 firmware. " +
            "The app exposes SIM contacts read-only until this is tested on hardware with a SIM.");
    }

    public Task DeleteSimContactAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "Deleting SIM contacts has not been verified on the stock V2.3.11 firmware. " +
            "The app keeps SIM storage read-only to avoid deleting the wrong record.");
    }

    public async Task<NetworkInfo> GetNetworkInfoAsync(CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();
        var props = GetPropParser.Parse(await _adb.ShellTextAsync(serial, "getprop", cancellationToken: cancellationToken).ConfigureAwait(false));
        var dump = await _adb.ShellTextAsync(serial, "dumpsys telephony.registry", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var telephony = TelephonyParser.Parse(dump, props);
        var mobileData = FirstLine(await _adb.ShellTextAsync(serial, "settings get global mobile_data", cancellationToken: cancellationToken).ConfigureAwait(false));
        var apn = await GetPreferredApnAsync(serial, props, telephony, cancellationToken).ConfigureAwait(false);
        var ip = await GetIpAddressAsync(serial, cancellationToken).ConfigureAwait(false);
        var sim = await GetSimInfoAsync(cancellationToken).ConfigureAwait(false);

        var operatorName = telephony.OperatorAlpha
                           ?? (string.IsNullOrWhiteSpace(sim.Operator) ? null : sim.Operator)
                           ?? "Not available";
        var networkType = telephony.NetworkType;
        if (string.IsNullOrWhiteSpace(networkType) || networkType.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            networkType = telephony.Signal.Source switch
            {
                "LTE RSRP" => "LTE (inferred from signal report)",
                "GSM RSSI" => "GSM (inferred from signal report)",
                _ => "Not available"
            };
        }

        var registration = !sim.IsPresent && !telephony.IsRegistered
            ? "Not registered (SIM absent)"
            : telephony.RegistrationState;

        return new NetworkInfo(
            registration,
            telephony.IsRegistered,
            operatorName,
            networkType,
            telephony.Signal.Dbm,
            telephony.Signal.Level,
            telephony.Signal.Source,
            apn?.Name,
            apn?.Apn,
            telephony.DataState,
            mobileData.Trim() == "1" || telephony.DataPossible,
            ip,
            GetPropParser.Value(props, "gsm.operator.iso-country", string.Empty) is { Length: > 0 } iso ? iso : null,
            null);
    }

    public async Task<IReadOnlyList<ApnConfig>> GetApnsAsync(CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();
        var props = GetPropParser.Parse(await _adb.ShellTextAsync(serial, "getprop", cancellationToken: cancellationToken).ConfigureAwait(false));
        var numeric = GetPropParser.Value(props, "gsm.operator.numeric", string.Empty);
        var where = string.IsNullOrWhiteSpace(numeric)
            ? string.Empty
            : $" --where numeric={numeric}";
        var output = await _adb.ShellTextAsync(
            serial,
            $"content query --uri content://telephony/carriers --projection _id:name:apn:type:mmsc:proxy:port:current:numeric{where}",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        return ContentQueryParser.ToApns(ContentQueryParser.Parse(output));
    }

    public async Task<IReadOnlyList<SmsMessage>> GetMessagesAsync(SmsFolder folder, CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();

        // The helper agent returns a JSON dump with correct escaping, including newlines
        // and commas in message bodies. Fall back to the shell content provider when the
        // agent is not installed.
        if (await IsSmsAgentInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            var agentMessages = await TryReadMessagesViaAgentAsync(serial, folder, cancellationToken).ConfigureAwait(false);
            if (agentMessages is not null)
            {
                _log.MarkCommunicationSuccess("SMS", $"Read {agentMessages.Count} message(s) from {folder} via helper agent.");
                return agentMessages;
            }
        }

        var uri = SmsFolderUris.For(folder);
        var query = $"content query --uri {uri} --projection _id:thread_id:address:date:read:status:error_code:service_center --sort \"date DESC\"";
        var result = await _adb.ShellAsync(serial, query, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            _log.Warning("SMS", $"Inbox query failed for {folder}: {FirstLine(result.StandardError)}");
            return Array.Empty<SmsMessage>();
        }

        var rows = ContentQueryParser.Parse(result.StandardOutput);
        if (rows.Count > MaxMessagesPerSync)
        {
            rows = rows.Take(MaxMessagesPerSync).ToList();
        }

        var now = DateTimeOffset.Now;
        var messages = new SmsMessage[rows.Count];
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = rows.Select(async (row, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var id = row.GetLong("_id") ?? 0;
                var body = await FetchBodyAsync(serial, uri, id, cancellationToken).ConfigureAwait(false) ?? string.Empty;
                messages[index] = ContentQueryParser.ToMessage(row, body, serial, folder, now);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        _log.MarkCommunicationSuccess("SMS", $"Read {messages.Length} message(s) from {folder}.");
        return messages;
    }

    private async Task<IReadOnlyList<SmsMessage>?> TryReadMessagesViaAgentAsync(
        string serial,
        SmsFolder folder,
        CancellationToken cancellationToken)
    {
        var requestId = "dump-" + Guid.NewGuid().ToString("N");
        var broadcast = await _adb.ShellAsync(
            serial,
            $"am broadcast -n {AgentReceiver} --es action dump-sms --es id {requestId}",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (!broadcast.Success)
        {
            return null;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        var completed = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var marker = await _adb.ShellTextAsync(
                serial,
                $"cat /sdcard/uzconnect/results/{requestId}.json",
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(marker) && marker.Contains("\"ok\"", StringComparison.Ordinal))
            {
                completed = true;
                break;
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        if (!completed)
        {
            _log.Warning("SMS", "Helper agent did not complete the SMS dump in time; falling back to content query.");
            return null;
        }

        var folderName = folder switch
        {
            SmsFolder.Inbox => "inbox",
            SmsFolder.Sent => "sent",
            SmsFolder.Drafts => "draft",
            SmsFolder.Outbox => "outbox",
            SmsFolder.Failed => "failed",
            SmsFolder.Queued => "queued",
            _ => "inbox"
        };

        var json = await _adb.ShellTextAsync(
            serial,
            $"cat /sdcard/uzconnect/sms-{folderName}.json",
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var messages = new List<SmsMessage>();
            var now = DateTimeOffset.Now;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var date = item.TryGetProperty("date", out var dateElement) && dateElement.TryGetInt64(out var dateValue)
                    ? dateValue
                    : 0;
                var id = item.TryGetProperty("_id", out var idElement) && idElement.TryGetInt64(out var idValue)
                    ? idValue
                    : 0;
                var read = item.TryGetProperty("read", out var readElement) && readElement.TryGetInt32(out var readValue) && readValue != 0;
                var thread = item.TryGetProperty("thread_id", out var threadElement) && threadElement.TryGetInt64(out var threadValue)
                    ? threadValue
                    : 0;
                var status = item.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var statusValue)
                    ? statusValue
                    : 0;

                messages.Add(new SmsMessage(
                    serial,
                    id,
                    folder,
                    GetJsonString(item, "address"),
                    GetJsonString(item, "body"),
                    date > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(date) : DateTimeOffset.UnixEpoch,
                    read || folder is SmsFolder.Sent or SmsFolder.Outbox,
                    thread,
                    status,
                    null,
                    GetJsonString(item, "service_center"),
                    now));
            }

            return messages;
        }
        catch (JsonException ex)
        {
            _log.Warning("SMS", $"Helper agent JSON could not be parsed: {ex.Message}");
            return null;
        }
    }

    private static string GetJsonString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private async Task<string?> FetchBodyAsync(string serial, string uri, long id, CancellationToken cancellationToken)
    {
        var output = await _adb.ShellTextAsync(
            serial,
            $"content query --uri {uri} --projection body --where _id={id.ToString(CultureInfo.InvariantCulture)}",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        return ContentQueryParser.ExtractSingleValue(output, "body");
    }

    public async Task MarkReadAsync(long deviceMessageId, bool isRead, CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();
        var result = await _adb.ShellAsync(
            serial,
            $"content update --uri content://sms --bind read:i:{(isRead ? 1 : 0)} --where _id={deviceMessageId.ToString(CultureInfo.InvariantCulture)}",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Marking the message read failed: {FirstLine(result.StandardError)}");
        }

        _log.Info("SMS", $"Message {deviceMessageId} marked {(isRead ? "read" : "unread")} on device.");
    }

    public async Task DeleteMessageAsync(long deviceMessageId, CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();
        var result = await _adb.ShellAsync(
            serial,
            $"content delete --uri content://sms --where _id={deviceMessageId.ToString(CultureInfo.InvariantCulture)}",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Deleting the message failed: {FirstLine(result.StandardError)}");
        }

        _log.Info("SMS", $"Message {deviceMessageId} deleted from device (content provider).");
    }

    public async Task<SmsSendResult> SendSmsAsync(SmsSendRequest request, CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();
        if (string.IsNullOrWhiteSpace(request.To))
        {
            return SmsSendResult.Failed("Invalid recipient", "Recipient phone number is empty.");
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return SmsSendResult.Failed("Invalid message", "Message body is empty.");
        }

        if (!await IsSmsAgentInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            return SmsSendResult.Failed(
                "Agent required",
                "The one-time UZConnect SMS helper is not installed. Install it from Messages or Settings.",
                0);
        }

        var parts = SmsSegmentCalculator.CountSegments(request.Body);
        var id = Guid.NewGuid().ToString("N");
        var sentAt = DateTimeOffset.Now;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Body));
        var command =
            $"am broadcast -n {AgentReceiver} --es action send --es id {id} --es to {AdbClient.Quote(request.To)} --es text_b64 {encoded}";
        var broadcast = await _adb.ShellAsync(serial, command, TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);
        if (!broadcast.Success)
        {
            return SmsSendResult.Failed("Broadcast failed", FirstLine(broadcast.StandardError), parts);
        }

        _log.Info("SMS", $"SMS send handed to helper agent (message id {id}, {parts} segment(s)).");

        var result = await WaitForAgentResultAsync(serial, id, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            // The helper may have sent the message without reporting a terminal
            // result. The device Sent folder is the authoritative record.
            if (await ConfirmSentInDeviceAsync(serial, request.To, request.Body, sentAt, cancellationToken)
                    .ConfigureAwait(false))
            {
                _log.MarkCommunicationSuccess("SMS", $"SMS confirmed in the device Sent folder (message id {id}).");
                return new SmsSendResult(true, "sent", null, id, parts, DateTimeOffset.Now);
            }

            return new SmsSendResult(false, "No agent response",
                "The helper agent did not report a result. The SIM may be absent or the Android SMS service is unavailable.",
                id, parts, DateTimeOffset.Now);
        }

        var status = result.Value.GetPropertyOrNull("status") ?? "unknown";
        var error = result.Value.GetPropertyOrNull("error");
        var success = string.Equals(status, "sent", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(status, "delivered", StringComparison.OrdinalIgnoreCase);

        if (!success)
        {
            // Android 4.4 often reports RESULT_ERROR_GENERIC_FAILURE through the
            // SmsManager PendingIntent even though the message is in Sent. Give
            // the provider a moment, then check the Sent folder.
            await Task.Delay(3500, cancellationToken).ConfigureAwait(false);
            if (await ConfirmSentInDeviceAsync(serial, request.To, request.Body, sentAt, cancellationToken)
                    .ConfigureAwait(false))
            {
                success = true;
                status = "sent";
                error = null;
                _log.MarkCommunicationSuccess("SMS", $"SMS confirmed in the device Sent folder (message id {id}).");
            }
        }

        if (success)
        {
            _log.MarkCommunicationSuccess("SMS", $"SMS send reported success (message id {id}).");
        }
        else
        {
            _log.Warning("SMS", $"SMS send failed (message id {id}): {status} {error}");
        }

        return new SmsSendResult(success, status, error, id, parts, DateTimeOffset.Now);
    }

    private async Task<bool> ConfirmSentInDeviceAsync(
        string serial,
        string to,
        string body,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        var since = sentAt.AddMinutes(-2).ToUnixTimeMilliseconds();
        var where = $"date>={since.ToString(CultureInfo.InvariantCulture)}";
        var output = await _adb.ShellTextAsync(
            serial,
            $"content query --uri content://sms/sent --projection _id:address:body:date --where \"{where}\"",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        var rows = ContentQueryParser.Parse(output);
        if (rows.Count == 0)
        {
            output = await _adb.ShellTextAsync(
                serial,
                "content query --uri content://sms/sent --projection _id:address:body:date",
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            rows = ContentQueryParser.Parse(output);
        }

        foreach (var row in rows)
        {
            var date = row.GetLong("date") ?? 0;
            if (date > 0 && date < since)
            {
                continue;
            }

            if (!SameRecipient(row.Get("address"), to))
            {
                continue;
            }

            if (!SameMessageBody(row.Get("body"), body))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool SameRecipient(string? left, string? right)
    {
        var a = DigitsOnly(left);
        var b = DigitsOnly(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        var aTail = a.Length > 9 ? a[^9..] : a;
        var bTail = b.Length > 9 ? b[^9..] : b;
        return string.Equals(aTail, bTail, StringComparison.Ordinal);
    }

    private static bool SameMessageBody(string? left, string? right)
    {
        var a = NormalizeBody(left);
        var b = NormalizeBody(right);
        return a.Length == 0 || b.Length == 0 ||
               string.Equals(a, b, StringComparison.Ordinal) ||
               a.Contains(b, StringComparison.Ordinal) ||
               b.Contains(a, StringComparison.Ordinal);
    }

    private static string DigitsOnly(string? value)
        => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string NormalizeBody(string? value)
        => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private async Task<JsonElement?> WaitForAgentResultAsync(string serial, string id, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in new[]
                     {
                         $"/sdcard/uzconnect/results/{id}.json",
                         $"/data/data/{AgentPackage}/files/results/{id}.json"
                     })
            {
                var content = await _adb.ShellTextAsync(serial, $"cat {path}", TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content) || content.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(content.Trim());
                    var status = document.RootElement.GetPropertyOrNull("status");
                    if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        // The helper writes an immediate "queued" record before the
                        // Android SMS service reports the real result. Keep waiting.
                        continue;
                    }

                    return document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Partially written file; retry.
                }
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public async Task<bool> IsSmsAgentInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDevice?.AdbSerial is null)
        {
            return false;
        }

        var output = await _adb.ShellTextAsync(
            _activeDevice.AdbSerial,
            $"pm path {AgentPackage}",
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);
        if (output.Contains("package:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Some Android 4.4 images occasionally return an empty path for an
        // installed package. Fall back to the package list before treating the
        // helper as missing.
        var packages = await _adb.ShellTextAsync(
            _activeDevice.AdbSerial,
            $"pm list packages {AgentPackage}",
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);
        return packages.Contains(AgentPackage, StringComparison.OrdinalIgnoreCase);
    }

    public async Task InstallSmsAgentAsync(CancellationToken cancellationToken = default)
    {
        var serial = RequireSerial();
        var apkPath = Path.Combine(AppContext.BaseDirectory, "Assets", "uzconnect-agent.apk");
        if (!File.Exists(apkPath))
        {
            apkPath = Path.Combine(AppContext.BaseDirectory, "uzconnect-agent.apk");
        }

        if (!File.Exists(apkPath))
        {
            throw new FileNotFoundException(
                "The bundled SMS helper APK was not found. Build tools/agent/build-agent.ps1 before running the app.",
                apkPath);
        }

        if (!await _adb.InstallApkAsync(serial, apkPath, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("ADB could not install the SMS helper agent.");
        }

        _log.Info("SMS", "SMS helper agent installed. It only handles messages the user explicitly sends.");
    }

    public async Task<bool> IsSmsSendReadyAsync(CancellationToken cancellationToken = default)
        => await IsSmsAgentInstalledAsync(cancellationToken).ConfigureAwait(false);

    private async Task<ApnConfig?> GetPreferredApnAsync(
        string serial,
        IReadOnlyDictionary<string, string> props,
        TelephonySnapshot telephony,
        CancellationToken cancellationToken)
    {
        var preferred = await _adb.ShellTextAsync(
            serial,
            "content query --uri content://telephony/carriers/preferapn --projection _id:name:apn:type:mmsc:proxy:port:current:numeric",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        var rows = ContentQueryParser.Parse(preferred);
        if (rows.Count > 0)
        {
            return ContentQueryParser.ToApns(rows).FirstOrDefault();
        }

        var numeric = GetPropParser.Value(props, "gsm.operator.numeric", string.Empty);
        if (!string.IsNullOrWhiteSpace(numeric))
        {
            var current = await _adb.ShellTextAsync(
                serial,
                $"content query --uri content://telephony/carriers --projection _id:name:apn:type:mmsc:proxy:port:current:numeric --where numeric={numeric}",
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            var candidates = ContentQueryParser.ToApns(ContentQueryParser.Parse(current));
            return candidates.FirstOrDefault(a => a.IsCurrent) ?? candidates.FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(telephony.DataApn))
        {
            return new ApnConfig("", telephony.DataApn, telephony.DataApn, "default", null, null, null, true, null, false);
        }

        return null;
    }

    private async Task<string?> GetIpAddressAsync(string serial, CancellationToken cancellationToken)
    {
        var ip = await _adb.ShellTextAsync(serial, "ip -f inet addr show rmnet0", TimeSpan.FromSeconds(8), cancellationToken)
            .ConfigureAwait(false);
        var match = IpAddressRegex().Match(ip);
        if (match.Success)
        {
            return match.Groups["ip"].Value;
        }

        var prop = FirstLine(await _adb.ShellTextAsync(serial, "getprop dhcp.rmnet0.ipaddress", TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false));
        return string.IsNullOrWhiteSpace(prop) ? null : prop;
    }

    private static bool IsDiagnosticCommandAllowed(string command)
    {
        var allowedStarts = new[]
        {
            "getprop", "dumpsys ", "settings get ", "content query ", "ls ", "cat /proc/",
            "id", "get-state", "pm path ", "ip -f inet addr", "df "
        };
        return allowedStarts.Any(prefix => command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private string RequireSerial()
    {
        var serial = _activeDevice?.AdbSerial;
        if (string.IsNullOrWhiteSpace(serial))
        {
            throw new InvalidOperationException("No modem is connected. Select a detected device first.");
        }

        return serial;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Split('\n')[0].Trim();
    }

    [GeneratedRegex(@"inet\s+(?<ip>\d+\.\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex IpAddressRegex();
}

internal static class JsonElementExtensions
{
    public static string? GetPropertyOrNull(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }
}
