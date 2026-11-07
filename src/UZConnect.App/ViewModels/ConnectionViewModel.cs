using System.Collections.ObjectModel;
using System.Windows.Input;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed class ConnectionViewModel : ObservableObject, IPageViewModel
{
    private readonly ModemSession _session;
    private bool _isBusy;
    private bool _showConfirm;
    private bool _pendingEnable;
    private string _statusText = "Mobile data control uses Android's `svc data` command.";
    private bool _autoConnect;
    private string _confirmText = string.Empty;

    public ConnectionViewModel(ModemSession session)
    {
        _session = session;
        _autoConnect = session.AutoConnect;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, onError: ReportError);
        ToggleMobileDataCommand = new RelayCommand(RequestToggle, () => IsConnected);
        ConfirmChangeCommand = new AsyncRelayCommand(ApplyChangeAsync, () => !IsBusy, ReportError);
        CancelChangeCommand = new RelayCommand(() => ShowConfirm = false);
        ToggleAutoConnectCommand = new AsyncRelayCommand(async () =>
        {
            await _session.SetAutoConnectAsync(AutoConnect);
        }, onError: ReportError);
    }

    public string Title => "Connection";
    public string Subtitle => "Mobile data state, APN context and automatic connection preferences.";

    public ObservableCollection<ApnRow> Apns { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand ToggleMobileDataCommand { get; }
    public ICommand ConfirmChangeCommand { get; }
    public ICommand CancelChangeCommand { get; }
    public ICommand ToggleAutoConnectCommand { get; }

    public bool IsConnected => _session.IsConnected;
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool MobileDataEnabled => _session.ConnectionStatus.MobileDataEnabled;
    public bool DataConnected => _session.ConnectionStatus.DataConnected;
    public string DataStatus => !IsConnected
        ? "Not available"
        : DataConnected ? "Connected" : MobileDataEnabled ? "Enabled, no active session" : "Disabled";
    public string State => IsConnected ? Display.OrNotAvailable(_session.ConnectionStatus.State) : "Not available";
    public string Apn => IsConnected ? Display.OrNotAvailable(_session.ConnectionStatus.Apn) : "Not available";
    public string IpAddress => IsConnected ? Display.OrNotAvailable(_session.ConnectionStatus.IpAddress) : "Not available";
    public string Duration => IsConnected ? Display.Duration(_session.ConnectionStatus.DurationSeconds) : "Not available";
    public string RxBytes => IsConnected ? Display.Bytes(_session.ConnectionStatus.RxBytes) : "Not available";
    public string TxBytes => IsConnected ? Display.Bytes(_session.ConnectionStatus.TxBytes) : "Not available";
    public string UsageNote => "Cumulative rmnet interface counters since the modem booted. Not a billing figure.";
    public string DurationNote => "Android 4.4 does not expose a reliable per-session duration through this interface.";
    public string ApnNote => "APN editing is disabled until a write path is verified on stock V2.3.11.";
    public string MobileDataNote => "The `svc data` command was verified to exist, but its end-to-end effect could not be tested without a SIM.";
    public string ApnEmptyText => IsConnected
        ? "No APN entry reported for the current operator."
        : "Connect the modem to read APN records.";

    public bool AutoConnect
    {
        get => _autoConnect;
        set
        {
            if (SetProperty(ref _autoConnect, value))
            {
                _ = _session.SetAutoConnectAsync(value);
            }
        }
    }

    public bool ShowConfirm
    {
        get => _showConfirm;
        private set => SetProperty(ref _showConfirm, value);
    }

    public string ConfirmText
    {
        get => _confirmText;
        private set => SetProperty(ref _confirmText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        _autoConnect = _session.AutoConnect;
        OnPropertyChanged(nameof(AutoConnect));
        await RefreshFromSessionAsync(cancellationToken);
        if (IsConnected)
        {
            await LoadApnsAsync(cancellationToken);
        }
    }

    public async Task RefreshFromSessionAsync(CancellationToken cancellationToken = default)
    {
        Raise(nameof(IsConnected), nameof(MobileDataEnabled), nameof(DataConnected), nameof(DataStatus), nameof(State),
            nameof(Apn), nameof(IpAddress), nameof(Duration), nameof(RxBytes), nameof(TxBytes), nameof(ApnEmptyText));
        await Task.CompletedTask;
    }

    private void RequestToggle()
    {
        if (!IsConnected)
        {
            StatusText = "Connect the modem before changing mobile data.";
            return;
        }

        _pendingEnable = !MobileDataEnabled;
        ConfirmText = _pendingEnable
            ? "Turn mobile data on? The modem may register and start a data session if a SIM is present."
            : "Turn mobile data off? Any active data session will be disconnected.";
        ShowConfirm = true;
    }

    private async Task ApplyChangeAsync()
    {
        ShowConfirm = false;
        IsBusy = true;
        StatusText = _pendingEnable ? "Enabling mobile data…" : "Disabling mobile data…";
        try
        {
            await _session.SetMobileDataAsync(_pendingEnable);
            await RefreshFromSessionAsync();
            StatusText = _pendingEnable ? "Mobile data enable requested." : "Mobile data disable requested.";
        }
        catch (Exception ex)
        {
            StatusText = $"Mobile data change failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await _session.RefreshAsync();
            await RefreshFromSessionAsync();
            await LoadApnsAsync();
            StatusText = "Connection information refreshed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Connection refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadApnsAsync(CancellationToken cancellationToken = default)
    {
        Apns.Clear();
        try
        {
            var apns = await _session.GetApnsAsync(cancellationToken);
            foreach (var apn in apns.OrderByDescending(a => a.IsCurrent).ThenBy(a => a.Name).Take(30))
            {
                Apns.Add(new ApnRow(apn));
            }

            Raise(nameof(ApnEmptyText));
        }
        catch (Exception ex)
        {
            StatusText = $"APN read failed: {ex.Message}";
        }
    }

    private void ReportError(Exception ex)
    {
        StatusText = $"Connection operation failed: {ex.Message}";
        IsBusy = false;
    }
}
