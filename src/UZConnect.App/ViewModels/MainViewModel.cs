using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UZConnect.App.Themes;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ModemSession _session;
    private readonly DispatcherTimer _refreshTimer;
    private NavigationItem? _selectedNav;
    private IPageViewModel _currentPage;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private string? _lastError;

    public MainViewModel()
    {
        _session = new ModemSession();
        Dashboard = new DashboardViewModel(_session);
        Messages = new MessagesViewModel(_session);
        Contacts = new ContactsViewModel(_session);
        Network = new NetworkViewModel(_session);
        Connection = new ConnectionViewModel(_session);
        Diagnostics = new DiagnosticsViewModel(_session);
        Settings = new SettingsViewModel(_session);
        Ussd = new UssdViewModel(_session);
        Statistics = new StatisticsViewModel(_session);
        Help = new HelpViewModel(_session);

        NavItems = new ObservableCollection<NavigationItem>
        {
            new("connection", "Connection", "pack://application:,,,/UZConnect;component/Assets/icons/connection-white.png", Dashboard),
            new("messages", "SMS", "pack://application:,,,/UZConnect;component/Assets/icons/sms-white.png", Messages),
            new("contacts", "Contacts", "pack://application:,,,/UZConnect;component/Assets/icons/contacts-white.png", Contacts),
            new("ussd", "USSD", "pack://application:,,,/UZConnect;component/Assets/icons/ussd-white.png", Ussd),
            new("statistics", "Statistics", "pack://application:,,,/UZConnect;component/Assets/icons/statistics-white.png", Statistics),
            new("settings", "Settings", "pack://application:,,,/UZConnect;component/Assets/icons/settings-white.png", Settings),
            new("help", "Help", "pack://application:,,,/UZConnect;component/Assets/icons/help-white.png", Help)
        };

        _currentPage = Dashboard;
        _selectedNav = NavItems[0];

        NavigateCommand = new RelayCommand(parameter =>
        {
            var target = parameter switch
            {
                NavigationItem item => item,
                string key => NavItems.FirstOrDefault(n => n.Key == key),
                _ => null
            };
            if (target is not null)
            {
                SelectedNav = target;
            }
        });
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy, ReportError);
        CycleThemeCommand = new RelayCommand(CycleTheme);
        DismissErrorCommand = new RelayCommand(() => LastError = null);

        _session.StateChanged += OnSessionStateChanged;
        Messages.PropertyChanged += OnMessagesPropertyChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_session.IsBusy) return;
            try
            {
                if (_session.IsConnected) await _session.RefreshAsync();
                else await _session.ScanAsync();
            }
            catch
            {
                // Errors are surfaced through session diagnostics.
            }
        };
    }

    public DashboardViewModel Dashboard { get; }
    public MessagesViewModel Messages { get; }
    public ContactsViewModel Contacts { get; }
    public NetworkViewModel Network { get; }
    public ConnectionViewModel Connection { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public SettingsViewModel Settings { get; }
    public UssdViewModel Ussd { get; }
    public StatisticsViewModel Statistics { get; }
    public HelpViewModel Help { get; }

    public ObservableCollection<NavigationItem> NavItems { get; }

    public ICommand NavigateCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CycleThemeCommand { get; }
    public ICommand DismissErrorCommand { get; }

    public NavigationItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (!SetProperty(ref _selectedNav, value) || value is null)
            {
                return;
            }

            var previousPage = _currentPage;
            _currentPage = value.Page;
            if (previousPage is UssdViewModel ussd && !ReferenceEquals(previousPage, _currentPage))
            {
                _ = ussd.OnNavigatedFromAsync();
            }

            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            _ = value.Page.OnNavigatedToAsync();
        }
    }

    public IPageViewModel CurrentPage => _currentPage;
    public string PageTitle => _currentPage.Title;
    public string PageSubtitle => _currentPage.Subtitle;

    public bool IsConnected => _session.IsConnected;
    public bool HasUsbDevice => _session.Devices.Count > 0;
    public string StatusDeviceText => _session.IsConnected ? "Device ready" : "No device";
    public string StatusConnectionText => _session.ConnectionStatus.DataConnected
        ? "Connected"
        : _session.MifiStatus?.NetStatus ?? "Disconnected";
    public string StatusSignalText => _session.IsConnected
        ? $"{_session.MifiStatus?.NetworkType ?? _session.NetworkInfo.NetworkType} � {_session.MifiStatus?.Operator ?? _session.NetworkInfo.Operator}"
        : "No service";
    public string StatusSmsText => $"{Messages.UnreadTotal} unread";
    public string HeaderStatus => _session.IsConnected
        ? $"Connected · {Display.OrNotAvailable(_session.DeviceInfo.Model)}"
        : HasUsbDevice ? "USB detected · ADB not ready" : "No modem detected";
    public string HeaderDetail => _session.IsConnected
        ? $"{Display.OrNotAvailable(_session.DeviceInfo.FirmwareVersion)} · {_session.NetworkInfo.SignalDisplay}"
        : LastError ?? "Plug in the UZ801 over USB";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(ShowBusy));
            }
        }
    }

    public bool ShowBusy => IsBusy;
    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (SetProperty(ref _lastError, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(HeaderDetail));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    public string ThemeLabel => ThemeManager.Current switch
    {
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        _ => "System"
    };

    public string ThemeGlyph => ThemeManager.IsDarkEffective ? "\uE706" : "\uE708";

    public async Task InitializeAsync()
    {
        try
        {
            await _session.InitializeAsync();
            UpdateHeader();
            await Messages.RefreshUnreadCountAsync();
            await _currentPage.OnNavigatedToAsync();
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _session.Log.Error("App", "Initialization failed.", ex);
        }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        BusyText = "Refreshing device state…";
        try
        {
            if (_session.IsConnected)
            {
                await _session.RefreshAsync();
            }
            else
            {
                await _session.ScanAsync();
            }

            await _currentPage.RefreshFromSessionAsync();
            UpdateHeader();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CycleTheme()
    {
        var next = ThemeManager.Current switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System
        };
        _ = _session.Settings.SetAsync(ModemSession.SettingTheme, next.ToString());
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            UpdateHeader();
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            UpdateHeader();
            LastError = _session.LastError;
            if (e.Reason is "connected" or "disconnected" or "scan" or "refresh" or "scan-error" or "connect-error")
            {
                _ = SafeRefreshCurrentPageAsync();
            }
        });
    }

    private async Task SafeRefreshCurrentPageAsync()
    {
        try
        {
            await _currentPage.RefreshFromSessionAsync();
        }
        catch
        {
            // Page-specific errors are surfaced in that page's status text.
        }
    }

    private void UpdateHeader()
    {
        Raise(nameof(IsConnected), nameof(HasUsbDevice), nameof(HeaderStatus), nameof(HeaderDetail),
            nameof(StatusDeviceText), nameof(StatusConnectionText), nameof(StatusSignalText), nameof(StatusSmsText));
        IsBusy = _session.IsBusy;
        BusyText = _session.IsBusy ? "Working…" : string.Empty;
    }

    private void OnMessagesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessagesViewModel.UnreadTotal))
        {
            var item = NavItems.FirstOrDefault(n => n.Key == "messages");
            if (item is not null)
            {
                item.Badge = Messages.UnreadTotal;
                OnPropertyChanged(nameof(StatusSmsText));
            }
        }
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        Raise(nameof(ThemeLabel), nameof(ThemeGlyph));
    }

    private void ReportError(Exception ex)
    {
        LastError = ex.Message;
        IsBusy = false;
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _session.StateChanged -= OnSessionStateChanged;
        Messages.PropertyChanged -= OnMessagesPropertyChanged;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        Diagnostics.Stop();
        try
        {
            _session.CancelUssdSessionAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // The modem will time the USSD session out; do not block shutdown.
        }

        _session.Dispose();
    }
}






