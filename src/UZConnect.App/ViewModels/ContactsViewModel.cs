using System.Collections.ObjectModel;
using System.Windows.Input;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed class ContactsViewModel : ObservableObject, IPageViewModel
{
    private readonly ModemSession _session;
    private bool _isBusy;
    private string _statusText = "SIM contacts are read-only on this firmware.";
    private bool _simPresent;
    private string _simState = "Not available";
    private string _operator = "Not available";
    private string _phoneNumber = "Not available";
    private string _storageSummary = "Not available";
    private int _contactCount;

    public ContactsViewModel(ModemSession session)
    {
        _session = session;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), onError: ReportError);
    }

    public string Title => "Contacts";
    public string Subtitle => "SIM identity and phonebook. Writes stay disabled until verified on this firmware.";

    public ObservableCollection<SimContactRow> Contacts { get; } = new();
    public ICommand RefreshCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    public bool SimPresent
    {
        get => _simPresent;
        private set
        {
            if (SetProperty(ref _simPresent, value))
            {
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    public string SimState
    {
        get => _simState;
        private set => SetProperty(ref _simState, value);
    }

    public string Operator
    {
        get => _operator;
        private set => SetProperty(ref _operator, value);
    }

    public string PhoneNumber
    {
        get => _phoneNumber;
        private set => SetProperty(ref _phoneNumber, value);
    }

    public string StorageSummary
    {
        get => _storageSummary;
        private set => SetProperty(ref _storageSummary, value);
    }

    public int ContactCount
    {
        get => _contactCount;
        private set
        {
            if (SetProperty(ref _contactCount, value))
            {
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    public bool ShowEmpty => !IsBusy && ContactCount == 0;
    public string EmptyText => !_session.IsConnected
        ? "Connect the modem to read SIM contacts."
        : SimPresent
            ? "No contacts stored on the SIM."
            : "No SIM detected. Insert a SIM and refresh.";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string WriteSupportText =>
        "Adding and deleting SIM contacts is intentionally disabled: the stock V2.3.11 firmware has not been verified to support it, and a wrong write could remove the wrong SIM record.";

    public async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
        => await RefreshAsync(cancellationToken);

    public async Task RefreshFromSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_session.IsConnected)
        {
            await RefreshAsync(cancellationToken);
        }
        else
        {
            Contacts.Clear();
            ContactCount = 0;
            SimPresent = false;
            SimState = "Not available";
            Operator = "Not available";
            PhoneNumber = "Not available";
            StorageSummary = "Not available";
            StatusText = "Connect the modem to read SIM contacts.";
            Raise(nameof(ShowEmpty), nameof(EmptyText), nameof(SimState), nameof(Operator), nameof(PhoneNumber),
                nameof(StorageSummary), nameof(ContactCount));
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.IsConnected)
        {
            StatusText = "Connect the modem to read SIM contacts.";
            SimPresent = false;
            SimState = "Not available";
            Contacts.Clear();
            ContactCount = 0;
            Raise(nameof(ShowEmpty), nameof(EmptyText));
            return;
        }

        IsBusy = true;
        StatusText = "Reading SIM information…";
        try
        {
            var sim = _session.SimInfo;
            SimPresent = sim.IsPresent;
            SimState = sim.State;
            Operator = Display.OrNotAvailable(sim.Operator);
            PhoneNumber = string.IsNullOrWhiteSpace(sim.PhoneNumber) ? "Not available" : sim.PhoneNumber!;

            var contacts = await _session.GetSimContactsAsync(cancellationToken);
            Contacts.Clear();
            foreach (var contact in contacts)
            {
                Contacts.Add(new SimContactRow(contact));
            }

            ContactCount = Contacts.Count;
            StorageSummary = contacts.Count == 0
                ? "No SIM contacts reported"
                : $"{contacts.Count} contact(s) read from SIM · capacity not reported";
            StatusText = sim.IsPresent
                ? $"SIM ready. {Contacts.Count} contact(s) loaded."
                : $"SIM state: {sim.State}.";
        }
        catch (Exception ex)
        {
            StatusText = $"SIM read failed: {ex.Message}";
            Contacts.Clear();
            ContactCount = 0;
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(ShowEmpty), nameof(EmptyText), nameof(Operator), nameof(PhoneNumber),
                nameof(SimState), nameof(StorageSummary));
        }
    }

    private void ReportError(Exception ex)
    {
        StatusText = $"SIM read failed: {ex.Message}";
        IsBusy = false;
    }
}
