using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using UZConnect.Core.Models;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed record WifiNavItem(string Key, string Label, bool IsGroup = false);

public sealed record WifiClientRow(int Id, string Name, string IpAddress, string MacAddress);
public sealed record WifiRangeRow(int Id, string Start, string End, string Lease);
public sealed record WifiFeatureRow(int Id, string Name, string Status, string Reason);

public sealed class WifiManagementViewModel : ObservableObject
{
    private readonly ModemSession _session;
    private WifiSnapshot? _snapshot;
    private bool _busy;
    private string _feedback = "Connect the modem and select Refresh.";
    private string _ssid = "";
    private int _maximum = 10;
    private WifiNavItem? _selectedNav;

    public WifiManagementViewModel(ModemSession session)
    {
        _session = session;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !Busy);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
        NavItems = new ObservableCollection<WifiNavItem>
        {
            new("Status", "Status"),
            new("Wireless2G", "Wireless 2.4G", true),
            new("WirelessSettings", "Wireless Settings"),
            new("MacFiltering", "MAC Filtering"),
            new("WirelessAdvanced", "Wireless Advanced"),
            new("ClientList", "Wireless Client List"),
            new("Network", "Network", true),
            new("Lan", "LAN / DHCP"),
            new("Reservation", "Address Reservation"),
            new("Availability", "Feature Availability")
        };
        SelectedNav = NavItems[0];
        foreach (var feature in WifiCapabilities.Features)
        {
            FeatureRows.Add(new WifiFeatureRow(FeatureRows.Count + 1, feature.Name, feature.Status, feature.Reason));
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ObservableCollection<WifiNavItem> NavItems { get; }
    public IReadOnlyList<WifiFeature> Features => WifiCapabilities.Features;
    public ObservableCollection<WifiClientInfo> Clients { get; } = new();
    public ObservableCollection<WifiDhcpRange> Ranges { get; } = new();
    public ObservableCollection<WifiClientRow> ClientRows { get; } = new();
    public ObservableCollection<WifiRangeRow> RangeRows { get; } = new();
    public ObservableCollection<WifiFeatureRow> FeatureRows { get; } = new();
    public bool HasClientRows => ClientRows.Count > 0;
    public bool HasRangeRows => RangeRows.Count > 0;
    public IEnumerable<int> MaximumChoices => Enumerable.Range(1, 10);
    public IEnumerable<int> ChannelChoices => Enumerable.Range(1, 13);

    public WifiNavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is { IsGroup: false } && SetProperty(ref _selectedNav, value))
            {
                Raise(nameof(SelectedPageKey));
            }
        }
    }

    public string SelectedPageKey => _selectedNav?.Key ?? "Status";
    public string PageTitle => _selectedNav?.Label ?? "Status";

    public string Ssid
    {
        get => _ssid;
        set { if (SetProperty(ref _ssid, value)) { Raise(nameof(CanSave)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public int MaximumClients
    {
        get => _maximum;
        set { if (SetProperty(ref _maximum, value)) { Raise(nameof(CanSave)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public bool Busy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) { Raise(nameof(CanEdit), nameof(CanSave)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public string Feedback { get => _feedback; private set => SetProperty(ref _feedback, value); }
    public bool CanEdit => WifiCapabilities.BasicChangesVerified && !Busy && _snapshot is { ConfigurationAvailable: true } && _session.IsConnected;
    public bool CanSave => CanEdit && new WifiBasicSettings(Ssid, MaximumClients).ValidationError is null &&
        (Ssid != _snapshot?.Ssid || MaximumClients != _snapshot?.MaximumClients);
    public bool WifiEnabled => _snapshot is { ApState: 13, HostapdRunning: true };
    public bool WifiDisabled => !WifiEnabled;
    public bool BroadcastEnabled => _snapshot?.HiddenSsid == false;
    public bool BroadcastDisabled => !BroadcastEnabled;
    public bool IsAes => EncryptionType == "AES";
    public int? ChannelNumber => _snapshot?.Channel;
    public string Status => _snapshot?.Status ?? "Not available";
    public string Security => _snapshot?.Security ?? "Not available";
    public string AuthenticationType => Security == "Open" ? "Open" : Security.StartsWith("WPA", StringComparison.Ordinal) ? "WPA/WPA2-PSK" : "Not available";
    public string EncryptionType => Security == "WPA2 / AES" ? "AES" : Security == "Open" ? "None" : "Not available";
    public string PasswordStatus => _snapshot?.PasswordConfigured switch { true => "Configured", false => "Not configured", _ => "Not available" };
    public string Channel => ChannelNumber?.ToString() ?? "Not available";
    public string Broadcast => _snapshot?.HiddenSsid switch { true => "Hidden", false => "Visible", _ => "Not available" };
    public string Gateway => _snapshot?.Gateway ?? "Not available";
    public string LanAddress => _snapshot?.Gateway ?? "Not available";
    public string DefaultGateway => _snapshot?.Gateway ?? "Not available";
    public string DhcpStatus => _snapshot is { DhcpRanges.Count: > 0 } ? "Enable" : "Not available";
    public string DhcpStart => Ranges.Count > 0 ? Ranges[0].Start : "Not available";
    public string DhcpEnd => Ranges.Count > 0 ? Ranges[0].End : "Not available";
    public string DhcpLease => Ranges.Count > 0 ? Ranges[0].Lease : "Not available";
    public string ConnectedCount => _snapshot?.ClientsAvailable == true ? Clients.Count.ToString() : "Not available";
    public string LastUpdated => _snapshot is null ? "" : $"Last updated {_snapshot.ObservedAt.ToLocalTime():HH:mm:ss}";
    public string ClientSummary => _snapshot?.ClientsAvailable == true
        ? (Clients.Count == 0 ? "No connected Wi-Fi devices reported." : $"{Clients.Count} connected Wi-Fi device(s).")
        : "Connected-device information is unavailable.";
    public string SavedSettingsNote => WifiCapabilities.BasicChangesVerified
        ? "Saving briefly disconnects Wi-Fi devices. The current settings are backed up, and the original is restored if the modem does not confirm the change."
        : "Settings are shown read-only until modem verification and recovery checks are complete.";
    public string ReadOnlyNote => WifiCapabilities.BasicChangesVerified
        ? "Read-only settings have no verified control on this firmware. Nothing is written to the modem."
        : "This page is read-only. Change controls stay disabled until the modem checks pass.";
    public string UnavailableNote => "The stock firmware exposes no verified control for this setting. Nothing is written to the modem.";

    public async Task RefreshAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var device = _session.ActiveDevice?.AdbSerial;
            if (device is null) { Clear(); Feedback = "Connect the modem to view Wi-Fi settings."; return; }
            var snapshot = await _session.ReadWifiManagementAsync();
            if (_session.ActiveDevice?.AdbSerial != device) { Clear(); Feedback = "The modem changed. Select Refresh."; return; }
            _snapshot = snapshot;
            Ssid = snapshot.Ssid ?? "";
            MaximumClients = snapshot.MaximumClients ?? 10;
            Clients.Clear();
            ClientRows.Clear();
            foreach (var client in snapshot.Clients)
            {
                Clients.Add(client);
                ClientRows.Add(new WifiClientRow(ClientRows.Count + 1, client.Name, client.IpAddress, client.MacAddress));
            }

            Ranges.Clear();
            RangeRows.Clear();
            foreach (var range in snapshot.DhcpRanges)
            {
                Ranges.Add(range);
                RangeRows.Add(new WifiRangeRow(RangeRows.Count + 1, range.Start, range.End, range.Lease));
            }
            Feedback = snapshot.ConfigurationAvailable ? "Wi-Fi information refreshed." : "Some Wi-Fi information is unavailable.";
        }
        catch { Clear(); Feedback = "Wi-Fi information could not be read. Check the modem connection and try Refresh."; }
        finally { Busy = false; Notify(); }
    }

    public void ConnectionChanged()
    {
        if (!_session.IsConnected) { Clear(); Feedback = "Connect the modem to view Wi-Fi settings."; Notify(); }
    }

    private async Task SaveAsync()
    {
        if (!CanSave) return;
        if (MessageBox.Show("Save these Wi-Fi settings? Connected Wi-Fi devices will briefly disconnect.", "Wi-Fi Management",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Busy = true;
        string message;
        try { message = (await _session.ApplyWifiBasicAsync(new(Ssid, MaximumClients))).Message; }
        catch { message = "The change could not be confirmed. Refresh the modem status before retrying."; }
        finally { Busy = false; }
        await RefreshAsync();
        Feedback = message;
    }

    private void Clear()
    {
        _snapshot = null;
        Ssid = "";
        Clients.Clear();
        ClientRows.Clear();
        Ranges.Clear();
        RangeRows.Clear();
    }

    private void Notify()
    {
        Raise(nameof(Status), nameof(Security), nameof(AuthenticationType), nameof(EncryptionType),
            nameof(PasswordStatus), nameof(Channel), nameof(ChannelNumber), nameof(Broadcast), nameof(BroadcastEnabled),
            nameof(Gateway), nameof(LanAddress), nameof(DefaultGateway), nameof(DhcpStatus), nameof(DhcpStart),
            nameof(DhcpEnd), nameof(DhcpLease), nameof(ConnectedCount), nameof(WifiEnabled), nameof(LastUpdated),
            nameof(ClientSummary), nameof(CanEdit), nameof(CanSave), nameof(PageTitle),
            nameof(HasClientRows), nameof(HasRangeRows), nameof(WifiDisabled), nameof(BroadcastDisabled), nameof(IsAes));
    }
}
