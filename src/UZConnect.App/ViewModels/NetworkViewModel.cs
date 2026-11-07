using System.Collections.ObjectModel;
using System.Windows.Input;
using UZConnect.Core.Models;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed class NetworkViewModel : ObservableObject, IPageViewModel
{
    private readonly ModemSession _session;
    private bool _isBusy;
    private bool _imeiRevealed;
    private string _statusText = "Network information comes from the modem's telephony service.";
    private int _signalLevel;

    public NetworkViewModel(ModemSession session)
    {
        _session = session;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, onError: ReportError);
        ToggleImeiCommand = new RelayCommand(() =>
        {
            ImeiRevealed = !ImeiRevealed;
        });
    }

    public string Title => "Network";
    public string Subtitle => "Registration, radio technology, signal and APN configuration.";

    public ObservableCollection<ApnRow> Apns { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand ToggleImeiCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string Registration => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.RegistrationState) : "Not available";
    public string Operator => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.Operator) : "Not available";
    public string NetworkType => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.NetworkType) : "Not available";
    public string SignalDbm => IsConnected ? _session.NetworkInfo.SignalDisplay : "Not available";
    public string SignalLevelText => IsConnected ? _session.NetworkInfo.SignalLevelDisplay : "Not available";
    public string SignalRaw => IsConnected ? _session.NetworkInfo.SignalSource : "Not available";
    public string DataState => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.DataState) : "Not available";
    public string ApnName => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.ApnName) : "Not available";
    public string Apn => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.Apn) : "Not available";
    public string IpAddress => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.IpAddress) : "Not available";
    public string Country => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.NetworkCountryIso) : "Not available";
    public string SimState => IsConnected ? Display.OrNotAvailable(_session.SimInfo.State) : "Not available";
    public string Roaming => IsConnected ? Display.OrNotAvailable(_session.NetworkInfo.Roaming) : "Not available";
    public string Imei => IsConnected ? _session.DeviceInfo.Imei : "Not available";
    public string MaskedImei => IsConnected ? _session.DeviceInfo.MaskedImei : "Not available";
    public string ImeiDisplay => ImeiRevealed ? Display.OrNotAvailable(Imei) : MaskedImei;
    public string ImeiHint => ImeiRevealed ? "Visible until you leave this page" : "Masked by default";

    public bool ImeiRevealed
    {
        get => _imeiRevealed;
        private set
        {
            if (SetProperty(ref _imeiRevealed, value))
            {
                Raise(nameof(ImeiDisplay), nameof(ImeiHint));
            }
        }
    }

    public int SignalLevel
    {
        get => _signalLevel;
        private set
        {
            if (SetProperty(ref _signalLevel, value))
            {
                Raise(nameof(SignalLevelOne), nameof(SignalLevelTwo), nameof(SignalLevelThree), nameof(SignalLevelFour));
            }
        }
    }

    public bool SignalLevelOne => SignalLevel >= 1;
    public bool SignalLevelTwo => SignalLevel >= 2;
    public bool SignalLevelThree => SignalLevel >= 3;
    public bool SignalLevelFour => SignalLevel >= 4;

    public bool IsConnected => _session.IsConnected;
    public string ApnEmptyText => IsConnected
        ? "No APN entry was reported for the current operator."
        : "Connect the modem to read the APN configuration.";

    public async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        ImeiRevealed = false;
        await RefreshFromSessionAsync(cancellationToken);
        if (IsConnected)
        {
            await LoadApnsAsync(cancellationToken);
        }
    }

    public async Task RefreshFromSessionAsync(CancellationToken cancellationToken = default)
    {
        SignalLevel = IsConnected ? _session.NetworkInfo.SignalLevel ?? 0 : 0;
        Raise(nameof(IsConnected), nameof(Registration), nameof(Operator), nameof(NetworkType), nameof(SignalDbm),
            nameof(SignalLevelText), nameof(SignalRaw), nameof(DataState), nameof(ApnName), nameof(Apn),
            nameof(IpAddress), nameof(Country), nameof(SimState), nameof(Roaming), nameof(Imei), nameof(MaskedImei),
            nameof(ImeiDisplay), nameof(ApnEmptyText));
        await Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Refreshing network information…";
        try
        {
            await _session.RefreshAsync();
            await RefreshFromSessionAsync();
            await LoadApnsAsync();
            StatusText = IsConnected ? "Network information refreshed." : "Connect the modem to read network information.";
        }
        catch (Exception ex)
        {
            StatusText = $"Network refresh failed: {ex.Message}";
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
        StatusText = $"Network operation failed: {ex.Message}";
        IsBusy = false;
    }
}

