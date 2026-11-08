using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows.Input;
using System.Windows.Threading;
using UZConnect.Core.Models;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IPageViewModel
{
    private static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ModemSession _session;
    private int _unreadCount;
    private int _cachedCount;
    private string _syncStatus = string.Empty;
    private bool _isSyncing;
    private string? _lastError;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _internetTimer;
    private string _connectionActionStatus = string.Empty;
    private bool _isPcOnline;
    private bool _probeBusy;
    private string _pcInternetDetail = "Checking the internet connection…";

    public DashboardViewModel(ModemSession session)
    {
        _session = session;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), onError: ReportError);
        ScanCommand = new AsyncRelayCommand(() => _session.ScanAsync(), onError: ReportError);
        SyncInboxCommand = new AsyncRelayCommand(SyncInboxAsync, () => IsConnected && !IsSyncing, ReportError);
        ConnectCommand = new AsyncRelayCommand(ToggleConnectionAsync, () => IsConnected, ReportError);
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) =>
        {
            if (_session.IsConnected)
            {
                try
                {
                    await _session.RefreshMifiAsync();
                    await RefreshFromSessionAsync();
                }
                catch
                {
                    // Keep the last known state.
                }
            }
        };
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += async (_, _) =>
        {
            if (!IsDataConnected)
            {
                Raise(nameof(DownloadRateValue), nameof(DownloadRateUnit),
                    nameof(UploadRateValue), nameof(UploadRateUnit));
                return;
            }

            try
            {
                await _session.RefreshDataRatesAsync();
            }
            catch
            {
                // The next tick retries.
            }

            Raise(nameof(DownloadRateValue), nameof(DownloadRateUnit),
                nameof(UploadRateValue), nameof(UploadRateUnit),
                nameof(DownloadTotalText), nameof(UploadTotalText), nameof(TotalTrafficText),
                nameof(SessionDurationText));
        };
        _internetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _internetTimer.Tick += async (_, _) => await ProbeInternetAsync();
        DetectCommand = ScanCommand;
        _liveTimer.Start();
        _internetTimer.Start();
    }

    public string Title => "Dashboard";
    public string Subtitle => "Modem status, SIM, network and transport at a glance.";

    public ICommand RefreshCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand DetectCommand { get; }
    public ICommand SyncInboxCommand { get; }
    public ICommand ConnectCommand { get; }

    public ObservableCollection<InterfaceRow> Interfaces { get; } = new();
    public ObservableCollection<ModemDevice> DetectedDevices { get; } = new();

    public bool IsConnected => _session.IsConnected;
    public bool HasDetectedDevices => DetectedDevices.Count > 0;
    public bool ShowDetectedDevices => HasDetectedDevices && !IsConnected;
    public bool ShowDisconnectedState => !IsConnected;
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);
    public bool IsSyncing
    {
        get => _isSyncing;
        private set
        {
            if (SetProperty(ref _isSyncing, value))
            {
                OnPropertyChanged(nameof(SyncButtonText));
            }
        }
    }

    public string SyncButtonText => IsSyncing ? "Syncing…" : "Sync inbox";

    public bool IsDataConnected => _session.ConnectionStatus.DataConnected;
    public bool IsDataDisconnected => IsConnected && !IsDataConnected;
    public string ConnectionLabel => IsConnected ? (IsDataConnected ? "Connected" : "Disconnected") : "No device";
    public string ConnectButtonText => IsDataConnected ? "Disconnect" : "Connect";
    public string ConnectionStatusText => !IsConnected ? "No device" : IsDataConnected ? "Connected" : "Disconnected";
    public string InternetHeadline => !IsConnected
        ? "Modem not connected"
        : _isPcOnline ? "Internet online"
        : IsDataConnected ? "Connected, no internet" : "Internet disconnected";
    public string InternetDetail => !IsConnected
        ? "Plug in the UZ801 over USB to begin."
        : _isPcOnline
            ? "This PC can reach the internet."
            : IsDataConnected
                ? "The modem is online, but this PC cannot reach the internet. Check Windows network priority or another adapter."
                : "Click Connect to start a mobile data session.";
    public bool IsPcOnline => _isPcOnline;
    public string PcStatusText => _isPcOnline ? "Online" : IsDataConnected ? "No internet" : "Offline";
    public string PcStatusDetail => _pcInternetDetail;
    public string DownloadRateValue => FormatRate(_session.RxRateKbps).value;
    public string DownloadRateUnit => FormatRate(_session.RxRateKbps).unit;
    public string UploadRateValue => FormatRate(_session.TxRateKbps).value;
    public string UploadRateUnit => FormatRate(_session.TxRateKbps).unit;
    public string SignalText => SignalBars <= 0 ? "Not available" : $"{SignalBars}/5";
    public string SignalDescription => SignalBars switch
    {
        >= 5 => "Excellent",
        4 => "Good",
        3 => "Fair",
        2 => "Weak",
        1 => "Very weak",
        _ => "No signal"
    };
    public string NetworkDetail => $"{OperatorText} · {NetworkTypeText}";
    private static (string value, string unit) FormatRate(double kbps)
        => kbps >= 1024
            ? ($"{kbps / 1024d:0.0}", "MB/s")
            : ($"{Math.Max(0, kbps):0}", "KB/s");
    public string ConnectionActionStatus
    {
        get => _connectionActionStatus;
        private set => SetProperty(ref _connectionActionStatus, value);
    }
    public string OperatorText => Display.OrNotAvailable(_session.MifiStatus?.Operator ?? _session.NetworkInfo.Operator);
    public string NetworkTypeText => Display.OrNotAvailable(_session.MifiStatus?.NetworkType ?? _session.NetworkInfo.NetworkType);
    public int SignalBars => _session.MifiStatus?.Rssi ?? _session.NetworkInfo.SignalLevel ?? 0;
    public string RxRateText => $"{_session.RxRateKbps:0.0} KB/s";
    public string TxRateText => $"{_session.TxRateKbps:0.0} KB/s";
    public string SessionDurationText => _session.SessionStart is null ? "00:00:00" : (DateTimeOffset.Now - _session.SessionStart.Value) is { } span ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}" : "00:00:00";
    public string IpText => Display.OrNotAvailable(_session.ConnectionStatus.IpAddress);
    public string UploadTotalText => _session.MifiTraffic is null ? "Not available" : $"{_session.MifiTraffic.UpKb:0.0} KB";
    public string DownloadTotalText => _session.MifiTraffic is null ? "Not available" : $"{_session.MifiTraffic.DownKb:0.0} KB";
    public string TotalTrafficText => _session.MifiTraffic is null ? "Not available" : $"{_session.MifiTraffic.UpKb + _session.MifiTraffic.DownKb:0.0} KB";
    public string ConnectionDetail => IsConnected
        ? "ADB session active over USB"
        : HasDetectedDevices
            ? "USB detected — ADB session not available"
            : "Connect the modem by USB to begin";

    public string Model => IsConnected ? Display.OrNotAvailable(_session.DeviceInfo.Model) : "UZ801";
    public string Hardware => IsConnected ? Display.OrNotAvailable(_session.DeviceInfo.HardwareRevision) : "Not available";
    public string HardwareSource => IsConnected ? _session.DeviceInfo.HardwareRevisionSource : "Not available";
    public string Firmware => IsConnected ? Display.OrNotAvailable(_session.DeviceInfo.FirmwareVersion) : "Not available";
    public string AndroidVersion => IsConnected ? Display.OrNotAvailable(_session.DeviceInfo.AndroidVersion) : "Not available";
    public string BuildId => IsConnected ? Display.OrNotAvailable(_session.DeviceInfo.BuildId) : "Not available";
    public string SerialNumber => IsConnected ? Display.OrNotAvailable(_session.DeviceInfo.SerialNumber) : "Not available";
    public string ImeiMasked => IsConnected ? _session.DeviceInfo.MaskedImei : "Not available";

    public string SimStatus => IsConnected
        ? _session.SimInfo.State switch
        {
            "READY" => "Ready",
            "ABSENT" => "Absent",
            "PIN_REQUIRED" => "PIN required",
            "PUK_REQUIRED" => "PUK required",
            "UNKNOWN" => "Not available",
            _ => Display.OrNotAvailable(_session.SimInfo.State)
        }
        : "Not available";
    public string SimDetail => IsConnected
        ? string.Join(" · ", new[]
        {
            nullIfEmpty(_session.SimInfo.Operator),
            nullIfEmpty(_session.SimInfo.PhoneNumber is null ? null : Display.MaskPhone(_session.SimInfo.PhoneNumber))
        }.Where(x => x is not null)) is { Length: > 0 } details ? details : "No SIM details reported"
        : "Not available";

    public string Operator => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.Operator) : "Not available";
    public string NetworkType => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.NetworkType) : "Not available";
    public string Registration => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.RegistrationState) : "Not available";
    public string SignalDisplay => IsConnected ? _session.NetworkInfo.SignalDisplay : "Not available";
    public string SignalLevelText => IsConnected ? _session.NetworkInfo.SignalLevelDisplay : "Not available";
    public int SignalLevel => IsConnected ? _session.NetworkInfo.SignalLevel ?? 0 : 0;
    public string SignalSource => IsConnected ? _session.NetworkInfo.SignalSource : "Not available";
    public string DataStatus => IsConnected
        ? _session.ConnectionStatus.DataConnected
            ? "Connected"
            : _session.ConnectionStatus.MobileDataEnabled ? "Enabled" : "Disabled"
        : "Not available";
    public string DataDetail => IsConnected
        ? string.Join(" · ", new[]
        {
            nullIfEmpty(_session.ConnectionStatus.State),
            nullIfEmpty(_session.ConnectionStatus.Apn)
        }.Where(x => x is not null)) is { Length: > 0 } detail ? detail : "No active data session"
        : "Not available";
    public string ConnectionDuration => IsConnected ? Display.Duration(_session.ConnectionStatus.DurationSeconds) : "Not available";
    public string DataUsage => IsConnected
        ? $"{Display.Bytes(_session.ConnectionStatus.RxBytes)} down · {Display.Bytes(_session.ConnectionStatus.TxBytes)} up"
        : "Not available";

    public string UsbIdentity => IsConnected ? _session.ActiveDevice?.VidPid ?? "Not available" : "Not available";
    public string UsbSerial => IsConnected ? _session.ActiveDevice?.SerialNumber ?? "Not available" : "Not available";
    public string AdbStatus => _session.AdbAvailable
        ? IsConnected ? "Authorized" : "Available"
        : "Not available";
    public string AdbDetail => _session.AdbPath ?? "adb.exe not found";
    public string ProviderLine => IsConnected && _session.ActiveDevice is not null
        ? $"{_session.ActiveDevice.ProviderLabel} · {_session.ActiveDevice.VidPid}"
        : "No verified transport";
    public string LastCommunication => Display.Timestamp(_session.LastSuccessfulCommunication);
    public string FirmwareDetail => IsConnected
        ? $"{Firmware} · Android {AndroidVersion} (API {_session.DeviceInfo.ApiLevel})"
        : "Not available";
    public string HardwareDetail => IsConnected
        ? $"{Hardware} · {HardwareSource}"
        : "Not available";

    public int UnreadCount
    {
        get => _unreadCount;
        private set
        {
            if (SetProperty(ref _unreadCount, value))
            {
                OnPropertyChanged(nameof(HasUnread));
            }
        }
    }

    public bool HasUnread => UnreadCount > 0;

    public int CachedCount
    {
        get => _cachedCount;
        private set => SetProperty(ref _cachedCount, value);
    }

    public string CachedCountText => CachedCount == 1 ? "1 cached message" : $"{CachedCount} cached messages";

    public string SyncStatus
    {
        get => _syncStatus;
        private set
        {
            if (SetProperty(ref _syncStatus, value))
            {
                OnPropertyChanged(nameof(HasSyncStatus));
            }
        }
    }

    public bool HasSyncStatus => !string.IsNullOrWhiteSpace(SyncStatus);

    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (SetProperty(ref _lastError, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        _statusTimer.Start();
        await RefreshFromSessionAsync(cancellationToken);
        await RefreshCountsAsync(cancellationToken);
    }

    public async Task RefreshFromSessionAsync(CancellationToken cancellationToken = default)
    {
        LastError = _session.LastError;
        Interfaces.Clear();
        var interfaces = _session.ActiveDevice?.Interfaces
                         ?? _session.Devices.FirstOrDefault()?.Interfaces
                         ?? Array.Empty<UsbInterfaceInfo>();
        foreach (var item in interfaces)
        {
            Interfaces.Add(new InterfaceRow(item));
        }

        DetectedDevices.Clear();
        foreach (var device in _session.Devices.Where(d => d.IsAuthorized).Take(8))
        {
            DetectedDevices.Add(device);
        }

        Raise(
            nameof(IsConnected), nameof(HasDetectedDevices), nameof(ShowDetectedDevices), nameof(ShowDisconnectedState), nameof(ConnectionLabel), nameof(ConnectionDetail),
            nameof(Model), nameof(Hardware), nameof(HardwareSource), nameof(Firmware), nameof(AndroidVersion),
            nameof(BuildId), nameof(SerialNumber), nameof(ImeiMasked), nameof(SimStatus), nameof(SimDetail),
            nameof(Operator), nameof(NetworkType), nameof(Registration), nameof(SignalDisplay), nameof(SignalLevelText),
            nameof(SignalLevel), nameof(SignalSource), nameof(DataStatus), nameof(DataDetail),
            nameof(ConnectionDuration), nameof(DataUsage), nameof(UsbIdentity), nameof(UsbSerial),
            nameof(AdbStatus), nameof(AdbDetail), nameof(ProviderLine), nameof(LastCommunication),
            nameof(FirmwareDetail), nameof(HardwareDetail), nameof(IsDataConnected), nameof(IsDataDisconnected),
            nameof(ConnectButtonText), nameof(ConnectionStatusText), nameof(OperatorText), nameof(NetworkTypeText),
            nameof(SignalBars), nameof(RxRateText), nameof(TxRateText), nameof(SessionDurationText), nameof(IpText),
            nameof(UploadTotalText), nameof(DownloadTotalText), nameof(TotalTrafficText), nameof(ConnectButtonText));
        Raise(nameof(InternetHeadline), nameof(InternetDetail), nameof(IsPcOnline), nameof(PcStatusText),
            nameof(PcStatusDetail), nameof(DownloadRateValue), nameof(DownloadRateUnit),
            nameof(UploadRateValue), nameof(UploadRateUnit), nameof(SignalText),
            nameof(SignalDescription), nameof(NetworkDetail));
        await Task.CompletedTask;
    }

    private async Task ToggleConnectionAsync()
    {
        if (!IsConnected)
        {
            ConnectionActionStatus = "No device is connected.";
            return;
        }

        var target = !IsDataConnected;
        ConnectionActionStatus = target ? "Connecting…" : "Disconnecting…";
        try
        {
            await _session.SetMobileDataAsync(target);
            await RefreshFromSessionAsync();
            ConnectionActionStatus = IsDataConnected == target
                ? (target ? "Connected." : "Disconnected.")
                : (target
                    ? "The modem did not connect. Check the APN profile in Settings."
                    : "The modem did not disconnect.");
        }
        catch (Exception ex)
        {
            ConnectionActionStatus = $"Connection request failed: {ex.Message}";
        }
    }

    private async Task ProbeInternetAsync()
    {
        if (_probeBusy)
        {
            return;
        }

        _probeBusy = true;
        var online = false;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("1.1.1.1", 1500);
            online = reply.Status == IPStatus.Success;
            if (!online)
            {
                reply = await ping.SendPingAsync("8.8.8.8", 1500);
                online = reply.Status == IPStatus.Success;
            }

            if (!online)
            {
                try
                {
                    using var response = await ProbeClient.GetAsync("http://connectivitycheck.gstatic.com/generate_204");
                    online = (int)response.StatusCode == 204;
                }
                catch
                {
                    // ICMP and HTTP both failed.
                }
            }
        }
        catch
        {
            online = false;
        }

        _isPcOnline = online;
        _pcInternetDetail = online
            ? "This PC can reach the internet."
            : IsDataConnected
                ? "Modem connected, but Windows has no internet path."
                : "No internet connection.";
        _probeBusy = false;
        Raise(nameof(IsPcOnline), nameof(PcStatusText), nameof(PcStatusDetail),
            nameof(InternetHeadline), nameof(InternetDetail));
    }

    public async Task RefreshCountsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var counts = await _session.GetFolderCountsAsync(cancellationToken);
            var inbox = counts.FirstOrDefault(c => c.Folder == SmsFolder.Inbox);
            UnreadCount = inbox?.Unread ?? 0;
            CachedCount = counts.Sum(c => c.Count);
            OnPropertyChanged(nameof(CachedCountText));
        }
        catch
        {
            UnreadCount = 0;
            CachedCount = 0;
        }
    }

    private async Task RefreshAsync()
    {
        await _session.RefreshAsync();
        await RefreshFromSessionAsync();
        await RefreshCountsAsync();
    }

    private async Task SyncInboxAsync()
    {
        if (!IsConnected)
        {
            SyncStatus = "Connect the modem before syncing.";
            return;
        }

        IsSyncing = true;
        SyncStatus = "Syncing inbox from the device…";
        try
        {
            var result = await _session.SyncMessagesAsync(SmsFolder.Inbox);
            await RefreshCountsAsync();
            SyncStatus = $"Inbox synced: {result.Added} new, {result.Updated} updated, {result.Removed} removed.";
        }
        catch (Exception ex)
        {
            SyncStatus = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void ReportError(Exception ex)
    {
        LastError = ex.Message;
        SyncStatus = $"Operation failed: {ex.Message}";
    }

    private static string? nullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}









