using UZConnect.Core.Abstractions;
using UZConnect.Core.Models;
using UZConnect.Windows.Adb;
using UZConnect.Windows.Data;
using UZConnect.Windows.Serial;
using UZConnect.Windows.Mifi;
using UZConnect.Windows.Ussd;
using UZConnect.Windows.Usb;

namespace UZConnect.Windows.Services;

public sealed class SessionStateChangedEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Application-level facade over USB detection, ADB, SMS cache and diagnostics.
/// The UI never talks to a transport directly, which keeps experimental paths isolated.
/// </summary>
public sealed class ModemSession : IDisposable
{
    public const string SettingAdbPath = "adb.path";
    public const string SettingAutoConnect = "connection.auto";
    public const string SettingLastSerial = "device.lastSerial";
    public const string SettingTheme = "ui.theme";
    public const string SettingMaskImei = "ui.maskImei";
    public const string SettingRefreshSeconds = "ui.refreshSeconds";

    private readonly UsbDeviceService _usb;
    private readonly AdbClient _adb;
    private readonly AdbModemProvider _adbProvider;
    private readonly SerialUssdService _serialUssd;
    private readonly SerialAtModemProvider _serialProvider;
    private readonly MifiWebClient _mifi;
    private readonly UssdService _ussd;
    private readonly SqliteMessageStore _messageStore;
    private readonly SqliteSettingsStore _settings;
    private readonly DiagnosticsLog _log;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly SemaphoreSlim _mifiGate = new(1, 1);
    private string? _lastSerial;
    private bool _initialized;
    private bool _busy;
    private DateTimeOffset? _connectedSince;
    private DateTimeOffset? _lastSampleAt;
    private long _lastRxBytes;
    private bool _ussdNetworkSwitched;
    private int _ussdRestoreMode;
    private bool _apnDefaultsEnsured;
    private DateTimeOffset _lastStockRecoveryAt = DateTimeOffset.MinValue;

    public string? CurrentOperator => MifiStatus?.Operator ?? NetworkInfo.Operator;
    private long _lastTxBytes;

    public ModemSession()
    {
        _log = new DiagnosticsLog();
        _usb = new UsbDeviceService(_log);
        _adb = new AdbClient(_log);
        _adbProvider = new AdbModemProvider(_adb, _usb, _log);
        _serialProvider = new SerialAtModemProvider(_usb, _log);
        _mifi = new MifiWebClient(_adb, _log);
        _ussd = new UssdService(_adb, _log);
        _serialUssd = new SerialUssdService(_log);
        _messageStore = new SqliteMessageStore();
        _settings = new SqliteSettingsStore();
    }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<UsbDevicesChangedEventArgs>? UsbDevicesChanged;

    public IReadOnlyList<ModemDevice> Devices { get; private set; } = Array.Empty<ModemDevice>();
    public IReadOnlyList<UsbDeviceInfo> UsbDevices { get; private set; } = Array.Empty<UsbDeviceInfo>();
    public IReadOnlyList<string> ComPorts { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<AtProbeResult> AtProbes => _serialProvider.Probes;
    public ModemDevice? ActiveDevice { get; private set; }
    public DeviceInformation DeviceInfo { get; private set; } = DeviceInformation.Unknown;
    public ModemCapabilities Capabilities { get; private set; } = ModemCapabilities.None;
    public SimInfo SimInfo { get; private set; } = SimInfo.NotAvailable;
    public NetworkInfo NetworkInfo { get; private set; } = NetworkInfo.NotAvailable;
    public ConnectionStatus ConnectionStatus { get; private set; } = ConnectionStatus.Unknown;
    public bool IsConnected => ActiveDevice is not null;
    public bool IsBusy => _busy;
    public string? LastError { get; private set; }
    public bool AutoConnect { get; private set; } = true;
    public string? AdbPath => _adb.AdbPath;
    public bool AdbAvailable => _adb.IsAvailable;
    public DateTimeOffset? LastSuccessfulCommunication => _log.LastSuccessfulCommunication;
    public IReadOnlyList<DiagnosticEntry> Diagnostics => _log.Entries;
    public string LogFilePath => _log.LogFilePath;
    public MifiStatus? MifiStatus { get; private set; }
    public MifiTraffic? MifiTraffic { get; private set; }
    public MifiConnectionInfo? MifiConnection { get; private set; }
    public MifiWifiStatus? WifiStatus { get; private set; }
    public bool WifiControlSupported => true;
    public double RxRateKbps { get; private set; }
    public double TxRateKbps { get; private set; }
    public DateTimeOffset? SessionStart => _connectedSince;
    public string SerialForCache => ActiveDevice?.AdbSerial ?? _lastSerial ?? "unknown";

    public AdbClient Adb => _adb;
    public IMessageStore MessageStore => _messageStore;
    public ISettingsStore Settings => _settings;
    public IDiagnosticsLog Log => _log;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await _settings.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _messageStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var adbPath = await _settings.GetAsync(SettingAdbPath, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(adbPath))
        {
            _adb.Configure(adbPath);
        }

        var auto = await _settings.GetAsync(SettingAutoConnect, cancellationToken).ConfigureAwait(false);
        AutoConnect = !string.Equals(auto, "false", StringComparison.OrdinalIgnoreCase);
        _lastSerial = await _settings.GetAsync(SettingLastSerial, cancellationToken).ConfigureAwait(false);

        _usb.DevicesChanged += OnUsbDevicesChanged;
        _usb.Start();
        _log.Info("App", $"Data directory: {AppPaths.Root}");
        _log.Info("ADB", _adb.IsAvailable ? $"ADB: {_adb.AdbPath}" : "ADB not found.");

        await ScanAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnUsbDevicesChanged(object? sender, UsbDevicesChangedEventArgs e)
    {
        UsbDevices = e.Devices;
        UsbDevicesChanged?.Invoke(this, e);
        _ = ScanAsync();
    }

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            return;
        }

        if (!await _scanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            SetBusy(true, "Scanning");
            UsbDevices = await _usb.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            var ports = await _usb.GetSerialPortsAsync(cancellationToken).ConfigureAwait(false);
            ComPorts = ports.Select(p => p.PortName).ToArray();
            var detected = await _adbProvider.DetectDevicesAsync(cancellationToken).ConfigureAwait(false);
            Devices = detected;
            LastError = null;

            if (ActiveDevice is not null)
            {
                var stillPresent = detected.FirstOrDefault(d =>
                    string.Equals(d.AdbSerial, ActiveDevice.AdbSerial, StringComparison.OrdinalIgnoreCase));
                if (stillPresent is null)
                {
                    _log.Warning("USB", "Previously connected modem is no longer present; session cleared.");
                    await DisconnectCoreAsync().ConfigureAwait(false);
                    LastError = "The modem was disconnected.";
                }
                else
                {
                    ActiveDevice = stillPresent;
                }
            }

            if (ActiveDevice is null && AutoConnect)
            {
                var candidate = detected
                    .Where(d => d.IsAuthorized && !string.IsNullOrWhiteSpace(d.AdbSerial))
                    .OrderByDescending(d => string.Equals(d.Model, "UZ801", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (candidate is not null)
                {
                    try
                    {
                        await ConnectCoreAsync(candidate, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                        _log.Error("ADB", "Automatic connection failed.", ex);
                    }
                }
                else if (detected.Count > 0)
                {
                    LastError = "USB device detected, but no authorized ADB session is available.";
                }
            }

            RaiseState("scan");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Error("App", "Device scan failed.", ex);
            RaiseState("scan-error");
        }
        finally
        {
            SetBusy(false, "Scan complete");
            _scanGate.Release();
        }
    }

    public async Task<bool> ConnectAsync(ModemDevice device, CancellationToken cancellationToken = default)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetBusy(true, "Connecting");
            await ConnectCoreAsync(device, cancellationToken).ConfigureAwait(false);
            RaiseState("connected");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Error("ADB", "Connection failed.", ex);
            ActiveDevice = null;
            RaiseState("connect-error");
            return false;
        }
        finally
        {
            SetBusy(false, "Connection attempt finished");
            _actionGate.Release();
        }
    }

    private async Task ConnectCoreAsync(ModemDevice device, CancellationToken cancellationToken)
    {
        await _adbProvider.ConnectAsync(device, cancellationToken).ConfigureAwait(false);
        ActiveDevice = device;
        _lastSerial = device.AdbSerial;
        await _settings.SetAsync(SettingLastSerial, _lastSerial, cancellationToken).ConfigureAwait(false);
        await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        _log.Info("ADB", $"Session active: {device.DisplayName}.");
    }

    public async Task DisconnectAsync()
    {
        await _actionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SetBusy(true, "Disconnecting");
            await DisconnectCoreAsync().ConfigureAwait(false);
            RaiseState("disconnected");
        }
        finally
        {
            SetBusy(false, "Disconnected");
            _actionGate.Release();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        await _adbProvider.DisconnectAsync().ConfigureAwait(false);
        ActiveDevice = null;
        DeviceInfo = DeviceInformation.Unknown;
        Capabilities = ModemCapabilities.None;
        SimInfo = SimInfo.NotAvailable;
        NetworkInfo = NetworkInfo.NotAvailable;
        ConnectionStatus = ConnectionStatus.Unknown;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (ActiveDevice is null)
        {
            await ScanAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await _actionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            SetBusy(true, "Refreshing");
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            LastError = null;
            RaiseState("refresh");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Error("App", "Refresh failed.", ex);
            RaiseState("refresh-error");
        }
        finally
        {
            SetBusy(false, "Refresh complete");
            _actionGate.Release();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (ActiveDevice?.AdbSerial is null)
        {
            return;
        }

        var state = await _adb.GetStateAsync(ActiveDevice.AdbSerial, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(state.Trim(), "device", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ADB state changed to '{state.Trim()}'.");
        }

        DeviceInfo = await _adbProvider.GetDeviceInformationAsync(cancellationToken).ConfigureAwait(false);
        ActiveDevice = ActiveDevice with
        {
            Model = DeviceInfo.Model,
            FirmwareVersion = DeviceInfo.FirmwareVersion,
            Manufacturer = DeviceInfo.Manufacturer
        };

        Capabilities = await _adbProvider.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        SimInfo = await SafeAsync(() => _adbProvider.GetSimInfoAsync(cancellationToken), SimInfo.NotAvailable, "SIM");
        NetworkInfo = await SafeAsync(() => _adbProvider.GetNetworkInfoAsync(cancellationToken), NetworkInfo.NotAvailable, "Network");
        ConnectionStatus = await SafeAsync(() => _adbProvider.GetConnectionStatusAsync(cancellationToken), ConnectionStatus.Unknown, "Connection");
        await RefreshMifiAsync(cancellationToken).ConfigureAwait(false);
        await EnsureDefaultApnProfilesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the stock modem control channel (connection state, traffic, Wi-Fi status).
    /// Failures are non-fatal: the ADB-derived state remains visible.
    /// </summary>
    public async Task RefreshMifiAsync(CancellationToken cancellationToken = default)
    {
        var serial = ActiveDevice?.AdbSerial;
        if (string.IsNullOrWhiteSpace(serial) || !await _mifiGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
        var status = await _mifi.GetStatusAsync(serial, cancellationToken).ConfigureAwait(false);
        var connection = await _mifi.GetConnectionInfoAsync(serial, cancellationToken).ConfigureAwait(false);
        var traffic = await _mifi.GetTrafficAsync(serial, cancellationToken).ConfigureAwait(false);
        var wifi = await _mifi.GetWifiStatusAsync(serial, cancellationToken).ConfigureAwait(false);

        if (status is null &&
            DateTimeOffset.UtcNow - _lastStockRecoveryAt > TimeSpan.FromMinutes(2))
        {
            _lastStockRecoveryAt = DateTimeOffset.UtcNow;
            _ = RecoverStockControlAsync(serial);
        }

        MifiStatus = status;
        MifiConnection = connection;
        MifiTraffic = traffic;
        var hostapdRunning = await IsHostapdRunningAsync(serial, cancellationToken).ConfigureAwait(false);
        WifiStatus = wifi is null
            ? (hostapdRunning ? new MifiWifiStatus(true, "Not available", 0, 0, 0) : null)
            : wifi with { Enabled = hostapdRunning };

        if (status is not null)
        {
            var now = DateTimeOffset.Now;
            // The stock status endpoint can lag behind the connection endpoint
            // after an APN change. A real data IP means the connection is up.
            var hasDataIp = !string.IsNullOrWhiteSpace(connection?.Ip) &&
                            !string.Equals(connection!.Ip, "0.0.0.0", StringComparison.Ordinal) &&
                            !string.Equals(connection.Ip, "192.168.100.1", StringComparison.Ordinal);
            var connectedNow = status.IsConnected || hasDataIp;
            if (connectedNow)
            {
                _connectedSince ??= now;
            }
            else
            {
                _connectedSince = null;
            }

            var duration = _connectedSince is null ? (long?)null : (long)(now - _connectedSince.Value).TotalSeconds;
            long? rx = traffic is null ? null : traffic.DownKb * 1024;
            long? tx = traffic is null ? null : traffic.UpKb * 1024;

            if (traffic is not null && _lastSampleAt is not null)
            {
                var seconds = Math.Max(0.5, (now - _lastSampleAt.Value).TotalSeconds);
                RxRateKbps = Math.Max(0, (rx!.Value - _lastRxBytes) / 1024d / seconds);
                TxRateKbps = Math.Max(0, (tx!.Value - _lastTxBytes) / 1024d / seconds);
                _lastRxBytes = rx.Value;
                _lastTxBytes = tx.Value;
            }

            _lastSampleAt = now;

            ConnectionStatus = ConnectionStatus with
            {
                DataConnected = connectedNow,
                State = connectedNow ? "Connected" : status.NetStatus,
                IpAddress = connectedNow ? (connection?.Ip ?? ConnectionStatus.IpAddress) : null,
                DurationSeconds = duration,
                RxBytes = rx ?? ConnectionStatus.RxBytes,
                TxBytes = tx ?? ConnectionStatus.TxBytes
            };

            if (ConnectionStatus.MobileDataEnabled == false && connectedNow)
            {
                ConnectionStatus = ConnectionStatus with { MobileDataEnabled = true };
            }
        }

        RaiseState("mifi");
        }
        finally
        {
            _mifiGate.Release();
        }
    }

    public async Task<WifiControlResult> SetWifiEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var serial = ActiveDevice?.AdbSerial
                     ?? throw new InvalidOperationException("Connect the modem before changing Wi-Fi.");
        if (!await IsHostapdRunningAsync(serial, cancellationToken).ConfigureAwait(false))
        {
            if (!enabled)
            {
                return new WifiControlResult(true, false, "Wi-Fi is already off.");
            }

            var start = await _adb.ShellAsync(
                serial,
                "nohup /system/bin/hostapd -B -e /data/misc/wifi/entropy.bin /data/misc/wifi/hostapd.conf >/dev/null 2>&1 &",
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            var running = await IsHostapdRunningAsync(serial, cancellationToken).ConfigureAwait(false);
            if (running)
            {
                _log.Info("WiFi", "Wi-Fi access point started with the stock hostapd binary.");
                await RefreshMifiAsync(cancellationToken).ConfigureAwait(false);
                return new WifiControlResult(true, true, "Wi-Fi turned on.");
            }

            return new WifiControlResult(false, false,
                "The stock hostapd process could not be started. " +
                FirstLine(start.StandardError));
        }

        if (enabled)
        {
            return new WifiControlResult(true, true, "Wi-Fi is already on.");
        }

        var ps = await _adb.ShellTextAsync(serial, "ps | grep hostapd", TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        var pid = ps.Split('\n')
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts.Any(p => p.Contains("/system/bin/hostapd", StringComparison.Ordinal)))
            .Select(parts => parts[1])
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(pid))
        {
            return new WifiControlResult(false, true, "Could not identify the Wi-Fi access point process.");
        }

        await _adb.ShellAsync(serial, $"kill -TERM {pid}", TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
        var stillRunning = await IsHostapdRunningAsync(serial, cancellationToken).ConfigureAwait(false);
        if (stillRunning)
        {
            return new WifiControlResult(false, true,
                "The stock firmware restarted the Wi-Fi access point. Turning Wi-Fi OFF is not supported by this firmware.");
        }

        _log.Info("WiFi", "Wi-Fi access point stopped via the stock hostapd process.");
        WifiStatus = WifiStatus is null
            ? new MifiWifiStatus(false, "Not available", 0, 0, 0)
            : WifiStatus with { Enabled = false };
        RaiseState("wifi");
        return new WifiControlResult(true, false, "Wi-Fi turned off.");
    }

    private async Task<bool> IsHostapdRunningAsync(string serial, CancellationToken cancellationToken)
    {
        var ps = await _adb.ShellTextAsync(serial, "ps | grep hostapd", TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        return ps.Split('\n').Any(line =>
            line.Contains("/system/bin/hostapd", StringComparison.Ordinal) &&
            !line.Contains("grep", StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<MifiProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var serial = ActiveDevice?.AdbSerial;
        return string.IsNullOrWhiteSpace(serial)
            ? Array.Empty<MifiProfile>()
            : await _mifi.GetProfilesAsync(serial, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SaveProfileAsync(MifiProfile profile, CancellationToken cancellationToken = default)
    {
        var serial = ActiveDevice?.AdbSerial
                     ?? throw new InvalidOperationException("Connect the modem before saving an APN profile.");
        var result = await _mifi.SaveProfileAsync(serial, profile, cancellationToken).ConfigureAwait(false);
        RaiseState("profile");
        return result;
    }

    private async Task RecoverStockControlAsync(string serial)
    {
        try
        {
            _log.Info("WebControl", "Starting the stock modem control service.");
            await _adb.ShellAsync(
                serial,
                "am start -n com.mifiservice.hello/.MainActivity",
                TimeSpan.FromSeconds(10),
                CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(3000, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning("WebControl", $"Could not start the stock modem control service: {ex.Message}");
        }
    }

    public async Task EnsureDefaultApnProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (_apnDefaultsEnsured || ActiveDevice?.AdbSerial is null)
        {
            return;
        }

        var serial = ActiveDevice.AdbSerial;
        try
        {
            var profiles = (await _mifi.GetProfilesAsync(serial, cancellationToken).ConfigureAwait(false)).ToList();
            if (!_mifi.LastRequestSucceeded)
            {
                await RecoverStockControlAsync(serial).ConfigureAwait(false);
                profiles = (await _mifi.GetProfilesAsync(serial, cancellationToken).ConfigureAwait(false)).ToList();
                if (!_mifi.LastRequestSucceeded)
                {
                    return;
                }
            }

            var required = new[]
            {
                (Name: "NTC", Apn: "ntc", Key: "Nepal Telecom"),
                (Name: "Ncell", Apn: "web", Key: "Ncell")
            };
            foreach (var item in required)
            {
                if (profiles.Any(p =>
                        OperatorDefaults.ApnMatches(p.Apn, item.Key) ||
                        OperatorDefaults.ProfileNameMatches(p.Name, item.Key)))
                {
                    continue;
                }

                var number = Enumerable.Range(1, 5)
                    .FirstOrDefault(candidate => profiles.All(p => p.Number != candidate));
                if (number == 0)
                {
                    break;
                }

                var saved = await _mifi.SaveProfileAsync(
                    serial,
                    new MifiProfile(number, item.Name, item.Apn, string.Empty, string.Empty, 0),
                    cancellationToken).ConfigureAwait(false);
                if (!saved || !_mifi.LastRequestSucceeded)
                {
                    return;
                }

                profiles.Add(new MifiProfile(number, item.Name, item.Apn, string.Empty, string.Empty, 0));
                _log.Info("APN", $"Default {item.Name} APN profile saved.");
            }

            _apnDefaultsEnsured = true;
        }
        catch (Exception ex)
        {
            _log.Warning("APN", $"Could not prepare the default APN profiles: {ex.Message}");
        }
    }

    public async Task<bool> ConnectProfileAsync(int profileNumber, CancellationToken cancellationToken = default)
    {
        var serial = ActiveDevice?.AdbSerial
                     ?? throw new InvalidOperationException("Connect the modem before using an APN profile.");
        var result = await _mifi.ConnectProfileAsync(serial, profileNumber, cancellationToken).ConfigureAwait(false);
        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        await RefreshMifiAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<bool> SetNetworkModeAsync(int mode, CancellationToken cancellationToken = default)
    {
        var serial = ActiveDevice?.AdbSerial
                     ?? throw new InvalidOperationException("Connect the modem before changing network mode.");
        var result = await _mifi.SetNetworkModeAsync(serial, mode, cancellationToken).ConfigureAwait(false);
        RaiseState("network-mode");
        return result;
    }

    private async Task<T> SafeAsync<T>(Func<Task<T>> action, T fallback, string category)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(category, $"Read failed: {ex.Message}");
            return fallback;
        }
    }

    public async Task<SmsSyncResult> SyncMessagesAsync(SmsFolder folder, CancellationToken cancellationToken = default)
    {
        var serial = ActiveDevice?.AdbSerial
                     ?? throw new InvalidOperationException("Connect the modem before syncing messages.");
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetBusy(true, $"Syncing {folder}");
            var messages = await _adbProvider.GetMessagesAsync(folder, cancellationToken).ConfigureAwait(false);
            var result = await _messageStore.ReplaceFolderAsync(serial, folder, messages, cancellationToken).ConfigureAwait(false);
            _log.Info("SMS", $"Sync {folder}: {result.Retrieved} retrieved, {result.Added} new, {result.Removed} removed.");
            RaiseState("sms-sync");
            return result;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Error("SMS", $"Sync failed for {folder}.", ex);
            RaiseState("sms-sync-error");
            throw;
        }
        finally
        {
            SetBusy(false, "Message sync finished");
            _actionGate.Release();
        }
    }

    public Task<IReadOnlyList<SmsMessage>> GetCachedMessagesAsync(SmsFolder folder, CancellationToken cancellationToken = default)
        => _messageStore.GetMessagesAsync(SerialForCache, folder, 1000, cancellationToken);

    public Task<IReadOnlyList<SmsFolderCount>> GetFolderCountsAsync(CancellationToken cancellationToken = default)
        => _messageStore.GetFolderCountsAsync(SerialForCache, cancellationToken);

    public async Task MarkReadAsync(SmsMessage message, bool isRead = true, CancellationToken cancellationToken = default)
    {
        if (ActiveDevice is null)
        {
            throw new InvalidOperationException("Connect the modem to change message state on the device.");
        }

        await _adbProvider.MarkReadAsync(message.DeviceMessageId, isRead, cancellationToken).ConfigureAwait(false);
        await _messageStore.MarkReadAsync(message.DeviceSerial, message.DeviceMessageId, isRead, cancellationToken).ConfigureAwait(false);
        RaiseState("sms-update");
    }

    public async Task<int> DeleteMessagesAsync(IEnumerable<SmsMessage> messages, CancellationToken cancellationToken = default)
    {
        if (ActiveDevice is null)
        {
            throw new InvalidOperationException("Connect the modem to delete messages from the device.");
        }

        var list = messages.ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        // Preserve the content locally in Recycle before removing it from the modem.
        await _messageStore.MoveToRecycleAsync(list[0].DeviceSerial, list, cancellationToken).ConfigureAwait(false);

        var count = 0;
        foreach (var message in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _adbProvider.DeleteMessageAsync(message.DeviceMessageId, cancellationToken).ConfigureAwait(false);
            await _messageStore.DeleteFromFolderAsync(message.DeviceSerial, message.DeviceMessageId, message.Folder, cancellationToken)
                .ConfigureAwait(false);
            count++;
        }

        _log.Info("SMS", $"{count} message(s) moved to Recycle after explicit confirmation.");
        RaiseState("sms-delete");
        return count;
    }

    public Task<IReadOnlyList<SmsMessage>> GetRecycleMessagesAsync(CancellationToken cancellationToken = default)
        => _messageStore.GetMessagesAsync(SerialForCache, SmsFolder.Recycle, 1000, cancellationToken);

    public async Task<int> EmptyRecycleAsync(CancellationToken cancellationToken = default)
    {
        var count = await _messageStore.EmptyRecycleAsync(SerialForCache, cancellationToken).ConfigureAwait(false);
        _log.Info("SMS", $"{count} message(s) permanently removed from the local Recycle.");
        RaiseState("recycle-empty");
        return count;
    }

    public Task<SmsMessage> SaveDraftAsync(string to, string body, CancellationToken cancellationToken = default)
        => _messageStore.SaveDraftAsync(SerialForCache, to, body, cancellationToken);

    public Task DeleteDraftAsync(long deviceMessageId, CancellationToken cancellationToken = default)
        => _messageStore.DeleteDraftAsync(SerialForCache, deviceMessageId, cancellationToken);

    public async Task<IReadOnlyList<UssdCommand>> GetUssdCommandsAsync(CancellationToken cancellationToken = default)
    {
        var commands = (await _messageStore.GetUssdCommandsAsync(cancellationToken).ConfigureAwait(false)).ToList();

        // Add the defaults for the current operator when they are not already
        // present. Existing user commands are preserved, so an Ncell command list
        // is not destroyed when an NTC SIM is inserted.
        var defaults = OperatorDefaults.UssdCommands(CurrentOperator);
        foreach (var item in defaults)
        {
            if (commands.Any(c => string.Equals(c.Code, item.Code, StringComparison.Ordinal)))
            {
                continue;
            }

            commands.Add(await _messageStore
                .SaveUssdCommandAsync(0, item.Name, item.Code, cancellationToken)
                .ConfigureAwait(false));
        }

        return commands
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<UssdCommand> SaveUssdCommandAsync(long id, string name, string code, CancellationToken cancellationToken = default)
        => _messageStore.SaveUssdCommandAsync(id, name, code, cancellationToken);

    public Task DeleteUssdCommandAsync(long id, CancellationToken cancellationToken = default)
        => _messageStore.DeleteUssdCommandAsync(id, cancellationToken);

    public async Task<UssdResult> SendUssdAsync(string code, CancellationToken cancellationToken = default)
    {
        var serial = ActiveDevice?.AdbSerial
                     ?? throw new InvalidOperationException("Connect the modem before sending a USSD command.");

        // The stock Android dialer's MMI handler rejects every code on this
        // firmware. The modem's own AT command port supports USSD correctly, so
        // use it first and keep the old UI-automation path only as a fallback.
        if (ActiveDevice is not null)
        {
            var atResult = await SendUssdOverModemAsync(ActiveDevice, code, cancellationToken).ConfigureAwait(false);
            if (atResult.Success)
            {
                _log.MarkCommunicationSuccess("USSD", "USSD command completed.");
                return atResult;
            }

            var portMissing = atResult.Error?.Contains("port could not be found", StringComparison.OrdinalIgnoreCase) == true;
            if (!portMissing)
            {
                _log.Warning("USSD", $"USSD command failed: {atResult.Error}");
                return atResult;
            }
        }

        var result = await _ussd.SendAsync(serial, code, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            _log.MarkCommunicationSuccess("USSD", "USSD command completed.");
        }
        else
        {
            _log.Warning("USSD", $"USSD command failed: {result.Error}");
        }

        return result;
    }

    public async Task<SmsSendResult> SendSmsAsync(string to, string body, CancellationToken cancellationToken = default)
    {
        var serial = ActiveDevice?.AdbSerial
                     ?? throw new InvalidOperationException("Connect the modem before sending a message.");
        var parts = SmsSegmentCalculator.CountSegments(body);
        var historyId = await _messageStore.AddSendHistoryAsync(serial, to, body, parts, "sending", null, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            SetBusy(true, "Sending SMS");
            var result = await _adbProvider.SendSmsAsync(new SmsSendRequest(to, body), cancellationToken)
                .ConfigureAwait(false);
            await _messageStore.UpdateSendHistoryAsync(historyId, result.Status, result.Error, cancellationToken)
                .ConfigureAwait(false);
            RaiseState("sms-send");
            return result;
        }
        catch (Exception ex)
        {
            await _messageStore.UpdateSendHistoryAsync(historyId, "failed", ex.Message, cancellationToken).ConfigureAwait(false);
            _log.Error("SMS", "SMS send failed.", ex);
            RaiseState("sms-send-error");
            throw;
        }
        finally
        {
            SetBusy(false, "SMS send finished");
        }
    }

    public Task<IReadOnlyList<SmsSendHistoryEntry>> GetSendHistoryAsync(CancellationToken cancellationToken = default)
        => _messageStore.GetSendHistoryAsync(SerialForCache, 200, cancellationToken);

    public async Task SetMobileDataAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (ActiveDevice is null)
        {
            throw new InvalidOperationException("Connect the modem before changing mobile data.");
        }

        if (enabled)
        {
            await EnsureDefaultApnProfilesAsync(cancellationToken).ConfigureAwait(false);
            if (MifiStatus?.IsConnected == true || ConnectionStatus.DataConnected)
            {
                return;
            }

            var profiles = await _mifi.GetProfilesAsync(ActiveDevice.AdbSerial!, cancellationToken)
                .ConfigureAwait(false);
            var match = profiles.FirstOrDefault(p =>
                OperatorDefaults.ApnMatches(p.Apn, CurrentOperator) ||
                OperatorDefaults.ProfileNameMatches(p.Name, CurrentOperator));
            if (match is not null)
            {
                var selected = await _mifi.ConnectProfileAsync(ActiveDevice.AdbSerial!, match.Number, cancellationToken)
                    .ConfigureAwait(false);
                if (selected)
                {
                    _log.Info("Connection", $"Selected {match.Name} APN profile {match.Number}.");
                    await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
                    await RefreshMifiAsync(cancellationToken).ConfigureAwait(false);
                    if (MifiStatus?.IsConnected == true || ConnectionStatus.DataConnected)
                    {
                        return;
                    }
                }
            }
        }

        var webSuccess = false;
        try
        {
            webSuccess = await _mifi.SetConnectionModeAsync(ActiveDevice.AdbSerial!, enabled, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning("Connection", $"Stock connect/disconnect command failed: {ex.Message}");
        }

        if (!webSuccess)
        {
            await _adbProvider.SetMobileDataAsync(enabled, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _log.Info("Connection", $"Stock modem connection set to {(enabled ? "Connect" : "Disconnect")}.");
        }

        await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
        await RefreshMifiAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ApnConfig>> GetApnsAsync(CancellationToken cancellationToken = default)
    {
        if (ActiveDevice is null)
        {
            return Array.Empty<ApnConfig>();
        }

        return await _adbProvider.GetApnsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SimContact>> GetSimContactsAsync(CancellationToken cancellationToken = default)
    {
        if (ActiveDevice is null)
        {
            return Array.Empty<SimContact>();
        }

        return await _adbProvider.GetSimContactsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task InstallSmsAgentAsync(CancellationToken cancellationToken = default)
    {
        if (ActiveDevice is null)
        {
            throw new InvalidOperationException("Connect the modem before installing the SMS helper.");
        }

        await _adbProvider.InstallSmsAgentAsync(cancellationToken).ConfigureAwait(false);
        RaiseState("agent-installed");
    }

    public Task<bool> IsSmsAgentInstalledAsync(CancellationToken cancellationToken = default)
        => _adbProvider.IsSmsAgentInstalledAsync(cancellationToken);

    public async Task<IReadOnlyList<AtProbeResult>> ProbeSerialAtAsync(CancellationToken cancellationToken = default)
    {
        SetBusy(true, "Probing serial ports");
        try
        {
            var results = await _serialProvider.ProbeAllAsync(cancellationToken).ConfigureAwait(false);
            RaiseState("at-probe");
            return results;
        }
        finally
        {
            SetBusy(false, "Serial probe finished");
        }
    }

    public async Task<CommandResult> RunDiagnosticCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (ActiveDevice is null)
        {
            return CommandResult.Failed(command, "No modem connected.", TimeSpan.Zero);
        }

        return await _adbProvider.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiagnosticReportContext> BuildReportContextAsync(CancellationToken cancellationToken = default)
    {
        var adbDevices = await _adb.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        var adb = adbDevices.FirstOrDefault(d =>
            string.Equals(d.Serial, ActiveDevice?.AdbSerial, StringComparison.OrdinalIgnoreCase));
        var usb = UsbDevices.FirstOrDefault(u =>
            string.Equals(u.InstanceId, ActiveDevice?.CompositeInstanceId, StringComparison.OrdinalIgnoreCase));

        return new DiagnosticReportContext(
            typeof(ModemSession).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            DateTimeOffset.Now,
            usb is null
                ? null
                : new ModemDevice(
                    ModemDevice.MakeId(ModemProviderKind.Adb, usb.InstanceId),
                    usb.DisplayName,
                    ModemProviderKind.Adb,
                    usb.VendorId,
                    usb.ProductId,
                    usb.SerialNumber,
                    ActiveDevice?.AdbSerial,
                    usb.Manufacturer,
                    ActiveDevice?.Model,
                    ActiveDevice?.FirmwareVersion,
                    null,
                    usb.InstanceId,
                    usb.HardwareIds,
                    usb.Interfaces,
                    true),
            ActiveDevice,
            DeviceInfo,
            Capabilities,
            UsbDevices,
            ComPorts,
            _adb.IsAvailable,
            _adb.AdbPath,
            adb?.IsReady ?? false,
            adb?.State,
            AtProbes.Select(p => p.Summary).ToArray(),
            Diagnostics);
    }

    public async Task<string> ExportReportAsync(CancellationToken cancellationToken = default)
    {
        var context = await BuildReportContextAsync(cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(AppPaths.ExportsDirectory, $"uzconnect-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await _log.ExportAsync(path, context, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        await _messageStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        RaiseState("cache-cleared");
    }

    public async Task SetAutoConnectAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        AutoConnect = enabled;
        await _settings.SetAsync(SettingAutoConnect, enabled ? "true" : "false", cancellationToken).ConfigureAwait(false);
        RaiseState("settings");
    }

    public async Task SetAdbPathAsync(string? path, CancellationToken cancellationToken = default)
    {
        await _settings.SetAsync(SettingAdbPath, path, cancellationToken).ConfigureAwait(false);
        _adb.Configure(path);
        await ScanAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SetBusy(bool value, string reason)
    {
        _busy = value;
        RaiseState(reason);
    }

    private async Task<UssdResult> SendUssdOverModemAsync(
        ModemDevice device,
        string code,
        CancellationToken cancellationToken)
    {
        var isReply = code.Length > 0 && code.All(char.IsDigit);
        var result = isReply
            ? await _serialUssd.SendAsync(device, code, cancellationToken).ConfigureAwait(false)
            : await StartUssdSessionAsync(device, code, cancellationToken).ConfigureAwait(false);

        if (!result.NeedsReply)
        {
            await RestoreUssdNetworkAsync(device).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<UssdResult> StartUssdSessionAsync(
        ModemDevice device,
        string code,
        CancellationToken cancellationToken)
    {
        var status = await _mifi.GetStatusAsync(device.AdbSerial!, cancellationToken).ConfigureAwait(false);
        if (!_ussdNetworkSwitched && status is not null && IsLte(status.NetworkType))
        {
            var savedMode = await _settings.GetAsync("network.mode", cancellationToken).ConfigureAwait(false);
            _ussdRestoreMode = savedMode switch
            {
                "3G only" => 1,
                "4G only" => 2,
                _ => 0
            };

            _log.Info("USSD", "Switching to 3G for USSD; the modem does not support USSD over LTE.");
            if (!await _mifi.SetNetworkModeAsync(device.AdbSerial!, 1, cancellationToken).ConfigureAwait(false))
            {
                return UssdResult.Failed(
                    "USSD needs a 3G connection on this modem, but the network mode could not be changed.");
            }

            _ussdNetworkSwitched = true;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                var current = await _mifi.GetStatusAsync(device.AdbSerial!, cancellationToken).ConfigureAwait(false);
                if (current is not null && current.IsConnected && !IsLte(current.NetworkType))
                {
                    break;
                }
            }

            // Give the modem a moment to finish registration on 3G.
            await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
        }

        return await _serialUssd.SendAsync(device, code, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreUssdNetworkAsync(ModemDevice device)
    {
        if (!_ussdNetworkSwitched)
        {
            return;
        }

        try
        {
            await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            await _mifi.SetNetworkModeAsync(device.AdbSerial!, _ussdRestoreMode, CancellationToken.None)
                .ConfigureAwait(false);
            _log.Info("USSD", "Restored the saved network mode after USSD.");
        }
        catch (Exception ex)
        {
            _log.Warning("USSD", $"Could not restore the network mode after USSD: {ex.Message}");
        }
        finally
        {
            _ussdNetworkSwitched = false;
        }
    }

    public async Task CancelUssdSessionAsync(CancellationToken cancellationToken = default)
    {
        if (ActiveDevice is null ||
            (!_serialUssd.SessionActive && !_ussdNetworkSwitched))
        {
            return;
        }

        await _serialUssd.CancelAsync(ActiveDevice, cancellationToken).ConfigureAwait(false);
        await RestoreUssdNetworkAsync(ActiveDevice).ConfigureAwait(false);
    }

    public bool UssdSessionActive => _serialUssd.SessionActive;

    private static bool IsLte(string? networkType)
        => networkType is not null &&
           (networkType.Contains("LTE", StringComparison.OrdinalIgnoreCase) ||
            networkType.Contains("4G", StringComparison.OrdinalIgnoreCase));

    private static string FirstLine(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Split('\n')[0].Trim();

    private void RaiseState(string reason)
        => StateChanged?.Invoke(this, new SessionStateChangedEventArgs(reason));

    public void Dispose()
    {
        _usb.DevicesChanged -= OnUsbDevicesChanged;
        _usb.Dispose();
        _mifi.Dispose();
        _log.Dispose();
        _scanGate.Dispose();
        _actionGate.Dispose();
        _mifiGate.Dispose();
    }
}













