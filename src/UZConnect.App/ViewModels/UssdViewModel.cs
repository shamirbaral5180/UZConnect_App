using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using UZConnect.Core.Models;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed class UssdViewModel : ObservableObject, IPageViewModel
{
    private readonly ModemSession _session;
    private UssdCommand? _selectedCommand;
    private string _name = string.Empty;
    private string _code = "*901#";
    private string _result = string.Empty;
    private string _status = "Enter a USSD code and press Send.";
    private bool _isBusy;
    private bool _sessionActive;
    private readonly DispatcherTimer _sessionTimer;

    public UssdViewModel(ModemSession session)
    {
        _session = session;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(), onError: ReportError);
        AddCommand = new AsyncRelayCommand(AddAsync, () => !string.IsNullOrWhiteSpace(Code), ReportError);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => SelectedCommand is not null && !string.IsNullOrWhiteSpace(Code), ReportError);
        EditCommand = new RelayCommand(() =>
        {
            if (SelectedCommand is null) return;
            Name = SelectedCommand.Name;
            Code = SelectedCommand.Code;
        });
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedCommand is not null, ReportError);
        SendCommand = new AsyncRelayCommand(SendAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Code), ReportError);
        CancelSessionCommand = new AsyncRelayCommand(
            () => CancelSessionAsync(),
            () => SessionActive && !IsBusy,
            ReportError);
        ClearCommand = new RelayCommand(() => { Result = string.Empty; Status = "Enter a USSD code and press Send."; });

        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        _sessionTimer.Tick += async (_, _) =>
        {
            _sessionTimer.Stop();
            if (SessionActive)
            {
                await CancelSessionAsync(timeout: true);
            }
        };
    }

    public string Title => "USSD";
    public string Subtitle => "USSD";

    public ObservableCollection<UssdCommand> Commands { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand CancelSessionCommand { get; }
    public ICommand ClearCommand { get; }

    public UssdCommand? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (!SetProperty(ref _selectedCommand, value)) return;
            if (value is not null)
            {
                Name = value.Name;
                Code = value.Code;
            }
        }
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Code
    {
        get => _code;
        set => SetProperty(ref _code, value);
    }

    public string Result
    {
        get => _result;
        private set => SetProperty(ref _result, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(SendButtonText));
            }
        }
    }

    public string SendButtonText => IsBusy ? "Sending…" : SessionActive ? "Reply" : "Send";

    public bool SessionActive
    {
        get => _sessionActive;
        private set
        {
            var changed = SetProperty(ref _sessionActive, value);
            if (changed)
            {
                OnPropertyChanged(nameof(SendButtonText));
            }

            _sessionTimer.Stop();
            if (value)
            {
                _sessionTimer.Start();
            }
        }
    }

    public async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken);
        SessionActive = _session.UssdSessionActive;
    }

    public async Task OnNavigatedFromAsync(CancellationToken cancellationToken = default)
    {
        if (SessionActive)
        {
            await CancelSessionAsync(silent: true);
        }
    }

    public Task RefreshFromSessionAsync(CancellationToken cancellationToken = default)
    {
        OnPropertyChanged(nameof(Status));
        return Task.CompletedTask;
    }

    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Commands.Clear();
        var commands = await _session.GetUssdCommandsAsync(cancellationToken);
        foreach (var command in commands)
        {
            Commands.Add(command);
        }

        var defaultCode = OperatorDefaults.DefaultBalanceCode(_session.CurrentOperator);
        SelectedCommand = Commands.FirstOrDefault(c => c.Code == defaultCode)
                          ?? Commands.FirstOrDefault();
        if (SelectedCommand is null)
        {
            Code = defaultCode;
        }
    }

    private async Task AddAsync()
    {
        var command = await _session.SaveUssdCommandAsync(0, Name, Code);
        Commands.Add(command);
        SelectedCommand = command;
        Status = "USSD command added.";
    }

    private async Task SaveAsync()
    {
        if (SelectedCommand is null) return;
        var command = await _session.SaveUssdCommandAsync(SelectedCommand.Id, Name, Code);
        var index = Commands.IndexOf(SelectedCommand);
        if (index >= 0) Commands[index] = command;
        SelectedCommand = command;
        Status = "USSD command saved.";
    }

    private async Task DeleteAsync()
    {
        if (SelectedCommand is null) return;
        await _session.DeleteUssdCommandAsync(SelectedCommand.Id);
        Commands.Remove(SelectedCommand);
        SelectedCommand = null;
        Name = string.Empty;
        Status = "USSD command deleted.";
    }

    private async Task SendAsync()
    {
        if (!_session.IsConnected)
        {
            Status = "No device is connected.";
            return;
        }

        IsBusy = true;
        Status = SessionActive && Code.All(char.IsDigit) ? "Sending menu reply…" : "Sending USSD request…";
        Result = string.Empty;
        try
        {
            var response = await _session.SendUssdAsync(Code);
            Result = response.Output;
            SessionActive = response.NeedsReply;
            if (response.Success)
            {
                if (response.NeedsReply)
                {
                    // Prepare the input for the next menu option. The original
                    // code remains available in the command list.
                    Code = string.Empty;
                    Status = "Menu active. Enter the option number (for example 1) and press Reply.";
                }
                else
                {
                    Status = "USSD response received.";
                }
            }
            else
            {
                SessionActive = false;
                Status = response.Error
                         ?? (response.Unsupported
                             ? "The modem rejected this USSD code. Check the code and try again."
                             : "USSD request failed.");
            }
        }
        catch (Exception ex)
        {
            SessionActive = false;
            Status = $"USSD request failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CancelSessionAsync(bool silent = false, bool timeout = false)
    {
        _sessionTimer.Stop();
        try
        {
            if (_session.IsConnected)
            {
                await _session.CancelUssdSessionAsync();
            }
        }
        catch
        {
            // The network will time the session out even if the cancel fails.
        }

        SessionActive = false;
        if (!silent)
        {
            Status = timeout
                ? "USSD session closed after inactivity."
                : "USSD session cancelled.";
        }
    }

    private void ReportError(Exception ex)
    {
        Status = ex.Message;
        IsBusy = false;
    }
}
