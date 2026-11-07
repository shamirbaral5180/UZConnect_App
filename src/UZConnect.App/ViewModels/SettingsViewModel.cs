using System.Collections.ObjectModel;
using System.IO;
using UZConnect.Windows.Mifi;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using UZConnect.App.Themes;
using UZConnect.Windows;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IPageViewModel
{
    private readonly ModemSession _session;
    private bool _isBusy;
    private bool _agentInstalled;
    private bool _showClearPrompt;
    private bool _autoConnect;
    private bool _maskImei = true;
    private string _adbPathInput = string.Empty;
    private string _statusText = "Settings are stored locally in the UZConnect data directory.";
    private AppTheme _theme = AppTheme.System;
    private SettingsSection? _selectedSection;
    private string _wifiStatusText = "Not available";
    private string _wifiFeedback = string.Empty;
    private bool _wifiBusy;
    private bool _connectSoundPrompt;
    private bool _connectSoundDisconnect;
    private bool _autoReconnect;
    private string _profileName = string.Empty;
    private string _profileApn = string.Empty;
    private string _profileUser = string.Empty;
    private string _profilePassword = string.Empty;
    private string _profileNumber = "1";
    private string _networkMode = "Auto";
    private string _diagnosticsText = string.Empty;
    private bool _promptNewMessage;
    private string _soundFileName = @"C:\Program Files\UZConnect\sound\newtextmsg.wav";
    private bool _smsDeliveryReport = true;
    private bool _saveMessagesOnSim;
    private ApnProfileRow? _selectedProfile;
    private string _profileAccessNumber = string.Empty;
    private string _profileAuthType = "None";
    private bool _connectionTypeNdis = true;
    private bool _connectionTypeRas;
    private bool _loadingSettings;
    private readonly Dictionary<int, string> _accessNumbers = new();

    public SettingsViewModel(ModemSession session)
    {
        _session = session;
        Wifi = new WifiManagementViewModel(session);
        BrowseAdbCommand = new RelayCommand(BrowseAdb);
        ApplyAdbCommand = new AsyncRelayCommand(ApplyAdbAsync, () => !IsBusy, ReportError);
        InstallAgentCommand = new AsyncRelayCommand(InstallAgentAsync, () => IsConnected && !IsBusy, ReportError);
        ClearCacheCommand = new RelayCommand(() => ShowClearPrompt = true);
        ConfirmClearCacheCommand = new AsyncRelayCommand(ClearCacheAsync, () => !IsBusy, ReportError);
        CancelClearCacheCommand = new RelayCommand(() => ShowClearPrompt = false);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.Root));
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.LogsDirectory));
        ExportReportCommand = new AsyncRelayCommand(ExportAsync, onError: ReportError);
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), onError: ReportError);
        WifiOnCommand = new AsyncRelayCommand(() => SetWifiAsync(true), () => false, ReportError);
        WifiOffCommand = new AsyncRelayCommand(() => SetWifiAsync(false), () => false, ReportError);
        RefreshWifiCommand = new AsyncRelayCommand(RefreshWifiAsync, onError: ReportError);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => !IsBusy, ReportError);
        ConnectProfileCommand = new AsyncRelayCommand(ConnectProfileAsync, () => !IsBusy, ReportError);
        RefreshProfilesCommand = new AsyncRelayCommand(RefreshProfilesAsync, onError: ReportError);
        ApplyNetworkModeCommand = new AsyncRelayCommand(ApplyNetworkModeAsync, onError: ReportError);
        BrowseSoundCommand = new RelayCommand(BrowseSound);
        NewProfileCommand = new RelayCommand(NewProfile);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile is not null);
        EditProfileCommand = new RelayCommand(EditProfile, () => SelectedProfile is not null);
        UndoProfileCommand = new RelayCommand(UndoProfile);
        SetDefaultProfileCommand = new RelayCommand(SetDefaultProfile, () => SelectedProfile is not null);
        ImportProfilesCommand = new AsyncRelayCommand(ImportProfilesAsync, () => !IsBusy, ReportError);
        Sections = new ObservableCollection<SettingsSection>
        {
            new("General", "General"),
            new("TextMessage", "Text Message Setting"),
            new("Profiles", "Profile Management"),
            new("Network", "Network"),
            new("Statistics", "Statistics"),
            new("Diagnostics", "Diagnostics"),
            new("ConnectionType", "Connection Type"),
            new("WiFi", "Wi-Fi Management")
        };
        _selectedSection = Sections[0];
    }

    public string Title => "Settings";
    public string Subtitle => "Theme, ADB path, helper agent, privacy and local data.";

    public ICommand BrowseAdbCommand { get; }
    public ICommand ApplyAdbCommand { get; }
    public ICommand InstallAgentCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand ConfirmClearCacheCommand { get; }
    public ICommand CancelClearCacheCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogsFolderCommand { get; }
    public ICommand ExportReportCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand WifiOnCommand { get; }
    public ICommand WifiOffCommand { get; }
    public ICommand RefreshWifiCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand ConnectProfileCommand { get; }
    public ICommand RefreshProfilesCommand { get; }
    public ICommand ApplyNetworkModeCommand { get; }
    public ICommand BrowseSoundCommand { get; }
    public ICommand NewProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand EditProfileCommand { get; }
    public ICommand UndoProfileCommand { get; }
    public ICommand SetDefaultProfileCommand { get; }
    public ICommand ImportProfilesCommand { get; }
    public ObservableCollection<SettingsSection> Sections { get; }
    public ObservableCollection<ApnProfileRow> Profiles { get; } = new();

    public SettingsSection? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (SetProperty(ref _selectedSection, value) && value is not null)
            {
                OnPropertyChanged(nameof(SelectedSectionKey));
                if (value.Key == "Profiles") _ = RefreshProfilesAsync();
                if (value.Key == "WiFi") _ = Wifi.RefreshAsync();
            }
        }
    }

    public string SelectedSectionKey => SelectedSection?.Key ?? "General";
    public WifiManagementViewModel Wifi { get; }
    public string WifiStatusText
    {
        get => _wifiStatusText;
        private set => SetProperty(ref _wifiStatusText, value);
    }
    public string WifiFeedback
    {
        get => _wifiFeedback;
        private set => SetProperty(ref _wifiFeedback, value);
    }
    public bool WifiBusy
    {
        get => _wifiBusy;
        private set => SetProperty(ref _wifiBusy, value);
    }
    public bool ConnectSoundPrompt
    {
        get => _connectSoundPrompt;
        set => SetProperty(ref _connectSoundPrompt, value);
    }
    public bool ConnectSoundDisconnect
    {
        get => _connectSoundDisconnect;
        set => SetProperty(ref _connectSoundDisconnect, value);
    }
    public bool AutoReconnect
    {
        get => _autoReconnect;
        set
        {
            if (SetProperty(ref _autoReconnect, value))
            {
                _ = _session.SetAutoConnectAsync(value);
            }
        }
    }
    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }
    public string ProfileApn
    {
        get => _profileApn;
        set => SetProperty(ref _profileApn, value);
    }
    public string ProfileUser
    {
        get => _profileUser;
        set => SetProperty(ref _profileUser, value);
    }
    public string ProfilePassword
    {
        get => _profilePassword;
        set => SetProperty(ref _profilePassword, value);
    }
    public string ProfileNumber
    {
        get => _profileNumber;
        set => SetProperty(ref _profileNumber, value);
    }
    public string ProfileAccessNumber
    {
        get => _profileAccessNumber;
        set => SetProperty(ref _profileAccessNumber, value);
    }
    public string ProfileAuthType
    {
        get => _profileAuthType;
        set => SetProperty(ref _profileAuthType, value);
    }
    public ApnProfileRow? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value) && value is not null)
            {
                LoadProfile(value);
            }
        }
    }
    public bool PromptNewMessage
    {
        get => _promptNewMessage;
        set
        {
            if (SetProperty(ref _promptNewMessage, value) && !_loadingSettings)
            {
                _ = _session.Settings.SetAsync("sms.promptNew", value ? "true" : "false");
            }
        }
    }
    public string SoundFileName
    {
        get => _soundFileName;
        set
        {
            if (SetProperty(ref _soundFileName, value) && !_loadingSettings)
            {
                _ = _session.Settings.SetAsync("sms.soundFile", value);
            }
        }
    }
    public bool SmsDeliveryReport
    {
        get => _smsDeliveryReport;
        set
        {
            if (SetProperty(ref _smsDeliveryReport, value) && !_loadingSettings)
            {
                _ = _session.Settings.SetAsync("sms.deliveryReport", value ? "true" : "false");
            }
        }
    }
    public bool SaveMessagesOnSim
    {
        get => _saveMessagesOnSim;
        set
        {
            if (SetProperty(ref _saveMessagesOnSim, value) && !_loadingSettings)
            {
                _ = _session.Settings.SetAsync("sms.saveMode", value ? "sim" : "local");
                OnPropertyChanged(nameof(SaveModeNote));
                OnPropertyChanged(nameof(SaveMessagesLocal));
            }
        }
    }
    public bool SaveMessagesLocal
    {
        get => !_saveMessagesOnSim;
        set
        {
            if (value)
            {
                SaveMessagesOnSim = false;
            }
        }
    }
    public string SaveModeNote => SaveMessagesOnSim
        ? "Android stores sent messages in the device SMS store; SIM storage is not available on this firmware."
        : "Messages are stored in the device SMS store and cached locally.";
    public bool ConnectionTypeNdis
    {
        get => _connectionTypeNdis;
        set
        {
            if (!SetProperty(ref _connectionTypeNdis, value))
            {
                return;
            }

            if (value)
            {
                _connectionTypeRas = false;
                OnPropertyChanged(nameof(ConnectionTypeRas));
            }

            if (!_loadingSettings)
            {
                _ = _session.Settings.SetAsync("connection.type", value ? "NDIS" : "RAS");
            }

            OnPropertyChanged(nameof(ConnectionTypeNote));
        }
    }
    public bool ConnectionTypeRas
    {
        get => _connectionTypeRas;
        set
        {
            if (!SetProperty(ref _connectionTypeRas, value))
            {
                return;
            }

            if (value)
            {
                _connectionTypeNdis = false;
                OnPropertyChanged(nameof(ConnectionTypeNdis));
            }

            if (!_loadingSettings)
            {
                _ = _session.Settings.SetAsync("connection.type", value ? "RAS" : "NDIS");
            }

            OnPropertyChanged(nameof(ConnectionTypeNote));
        }
    }
    public string ConnectionTypeNote => ConnectionTypeRas
        ? "RAS is not available on this stock firmware. Select NDIS to use the USB modem network adapter."
        : "NDIS is the USB modem network adapter used by UZConnect.";
    public string NetworkMode
    {
        get => _networkMode;
        set => SetProperty(ref _networkMode, value);
    }
    public string DiagnosticsText
    {
        get => _diagnosticsText;
        private set => SetProperty(ref _diagnosticsText, value);
    }

    public bool IsConnected => _session.IsConnected;
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool AgentInstalled
    {
        get => _agentInstalled;
        private set
        {
            if (SetProperty(ref _agentInstalled, value))
            {
                OnPropertyChanged(nameof(AgentStatusText));
            }
        }
    }

    public string AgentStatusText => AgentInstalled
        ? "Installed. It only handles messages you explicitly send."
        : IsConnected ? "Not installed. SMS reading works without it; sending needs it." : "Connect the modem to check.";

    public bool ShowClearPrompt
    {
        get => _showClearPrompt;
        private set => SetProperty(ref _showClearPrompt, value);
    }

    public string AdbPathInput
    {
        get => _adbPathInput;
        set => SetProperty(ref _adbPathInput, value);
    }

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

    public bool MaskImei
    {
        get => _maskImei;
        set
        {
            if (SetProperty(ref _maskImei, value))
            {
                _ = _session.Settings.SetAsync(ModemSession.SettingMaskImei, value ? "true" : "false");
            }
        }
    }

    public AppTheme SelectedTheme
    {
        get => _theme;
        private set
        {
            if (SetProperty(ref _theme, value))
            {
                OnPropertyChanged(nameof(ThemeSystem));
                OnPropertyChanged(nameof(ThemeLight));
                OnPropertyChanged(nameof(ThemeDark));
                OnPropertyChanged(nameof(ThemeLabel));
            }
        }
    }

    public bool ThemeSystem
    {
        get => SelectedTheme == AppTheme.System;
        set { if (value) SetTheme(AppTheme.System); }
    }

    public bool ThemeLight
    {
        get => SelectedTheme == AppTheme.Light;
        set { if (value) SetTheme(AppTheme.Light); }
    }

    public bool ThemeDark
    {
        get => SelectedTheme == AppTheme.Dark;
        set { if (value) SetTheme(AppTheme.Dark); }
    }

    public string ThemeLabel => SelectedTheme switch
    {
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        _ => "Follow system"
    };

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DataFolder => AppPaths.Root;
    public string DatabasePath => AppPaths.DatabasePath;
    public string LogsFolder => AppPaths.LogsDirectory;
    public string AdbDetectedPath => _session.AdbPath ?? "Not found";
    public string AppVersion => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "0.1.0";
    public string AgentPackage => "com.uzconnect.agent";

    public string SecurityNotes =>
        "• No firmware flashing, partition writes, rooting or bootloader changes.\n" +
        "• SMS deletion is always confirmed first and is removed from the modem's SMS provider.\n" +
        "• Logs never contain SMS bodies, phone numbers or full IMEIs.\n" +
        "• The SMS helper is a normal Android app installed over ADB; it is not part of the firmware.\n" +
        "• IMEI is masked by default and logs redact long identifiers.";

    public async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        _autoReconnect = _session.AutoConnect;
        OnPropertyChanged(nameof(AutoReconnect));
        UpdateDiagnostics();
        await RefreshWifiAsync();

        _loadingSettings = true;
        try
        {
            _promptNewMessage = await GetBoolAsync("sms.promptNew", false, cancellationToken);
            _smsDeliveryReport = await GetBoolAsync("sms.deliveryReport", true, cancellationToken);
            _saveMessagesOnSim = string.Equals(
                await _session.Settings.GetAsync("sms.saveMode", cancellationToken),
                "sim",
                StringComparison.OrdinalIgnoreCase);
            _soundFileName = await _session.Settings.GetAsync("sms.soundFile", cancellationToken)
                             ?? @"C:\Program Files\UZConnect\sound\newtextmsg.wav";
            var connectionType = await _session.Settings.GetAsync("connection.type", cancellationToken);
            _connectionTypeNdis = !string.Equals(connectionType, "RAS", StringComparison.OrdinalIgnoreCase);
            _connectionTypeRas = !_connectionTypeNdis;
            _networkMode = await _session.Settings.GetAsync("network.mode", cancellationToken) ?? "Auto";
            Raise(nameof(PromptNewMessage), nameof(SmsDeliveryReport), nameof(SaveMessagesOnSim),
                nameof(SaveMessagesLocal), nameof(SoundFileName), nameof(SaveModeNote),
                nameof(ConnectionTypeNdis), nameof(ConnectionTypeRas), nameof(ConnectionTypeNote),
                nameof(NetworkMode));
        }
        finally
        {
            _loadingSettings = false;
        }

        _adbPathInput = _session.AdbPath ?? string.Empty;
        OnPropertyChanged(nameof(AdbPathInput));
        _autoConnect = _session.AutoConnect;
        OnPropertyChanged(nameof(AutoConnect));
        var mask = await _session.Settings.GetAsync(ModemSession.SettingMaskImei, cancellationToken);
        _maskImei = !string.Equals(mask, "false", StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(MaskImei));

        await RefreshAsync(cancellationToken);
        await RefreshProfilesAsync();
    }

    private async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken cancellationToken)
    {
        var value = await _session.Settings.GetAsync(key, cancellationToken);
        return value is null ? fallback : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task RefreshFromSessionAsync(CancellationToken cancellationToken = default)
    {
        Wifi.ConnectionChanged();
        UpdateDiagnostics();
        WifiStatusText = _session.WifiStatus is null ? WifiStatusText : _session.WifiStatus.Enabled ? "ON" : "OFF";
        Raise(nameof(IsConnected), nameof(AgentStatusText), nameof(AdbDetectedPath), nameof(DiagnosticsText));
        await Task.CompletedTask;
    }

    private void SetTheme(AppTheme theme)
    {
        SelectedTheme = theme;
        _ = _session.Settings.SetAsync(ModemSession.SettingTheme, theme.ToString());
        StatusText = $"Theme set to {ThemeLabel}.";
    }

    private void BrowseAdb()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select adb.exe",
            Filter = "ADB (adb.exe)|adb.exe|Executables (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            AdbPathInput = dialog.FileName;
        }
    }

    private async Task ApplyAdbAsync()
    {
        IsBusy = true;
        try
        {
            await _session.SetAdbPathAsync(string.IsNullOrWhiteSpace(AdbPathInput) ? null : AdbPathInput.Trim());
            OnPropertyChanged(nameof(AdbDetectedPath));
            StatusText = _session.AdbAvailable
                ? $"ADB path applied: {_session.AdbPath}"
                : "ADB was not found at that path. USB-only detection remains available.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAgentAsync()
    {
        IsBusy = true;
        StatusText = "Installing the one-time SMS helper…";
        try
        {
            await _session.InstallSmsAgentAsync();
            AgentInstalled = await _session.IsSmsAgentInstalledAsync();
            StatusText = AgentInstalled ? "SMS helper installed." : "Install did not complete.";
        }
        catch (Exception ex)
        {
            StatusText = $"Helper install failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearCacheAsync()
    {
        ShowClearPrompt = false;
        IsBusy = true;
        try
        {
            await _session.ClearCacheAsync();
            StatusText = "Local message cache and send history cleared. Device data was not touched.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportAsync()
    {
        IsBusy = true;
        try
        {
            var path = await _session.ExportReportAsync();
            StatusText = $"Diagnostic report saved: {path}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            AgentInstalled = _session.IsConnected && await _session.IsSmsAgentInstalledAsync(cancellationToken);
        }
        catch
        {
            AgentInstalled = false;
        }

        Raise(nameof(IsConnected), nameof(AdbDetectedPath), nameof(AgentStatusText));
    }

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open folder: {ex.Message}";
        }
    }

    private async Task RefreshWifiAsync()
    {
        try
        {
            await _session.RefreshMifiAsync();
            WifiStatusText = _session.WifiStatus is null
                ? "Not available"
                : _session.WifiStatus.Enabled ? "ON" : "OFF";
            if (_session.WifiStatus is null)
            {
                WifiFeedback = "The modem did not report Wi-Fi status.";
            }
        }
        finally
        {
        }
    }

    private async Task SetWifiAsync(bool enabled)
    {
        WifiBusy = true;
        WifiFeedback = enabled ? "Turning Wi-Fi on" : "Turning Wi-Fi off";
        try
        {
            var result = await _session.SetWifiEnabledAsync(enabled);
            WifiStatusText = result.Enabled ? "ON" : "OFF";
            WifiFeedback = result.Supported
                ? (result.Enabled == enabled ? "Wi-Fi state changed." : "The modem did not confirm the change.")
                : result.Message;
        }
        catch (Exception ex)
        {
            WifiFeedback = $"Wi-Fi operation failed: {ex.Message}";
        }
        finally
        {
            WifiBusy = false;
        }
    }

    private async Task RefreshProfilesAsync()
    {
        Profiles.Clear();
        _accessNumbers.Clear();
        var profiles = await _session.GetProfilesAsync();
        foreach (var profile in profiles)
        {
            Profiles.Add(new ApnProfileRow(profile));
            var access = await _session.Settings.GetAsync($"profile.access.{profile.Number}");
            if (!string.IsNullOrWhiteSpace(access))
            {
                _accessNumbers[profile.Number] = access;
            }
        }

        if (SelectedProfile is not null &&
            !Profiles.Any(p => p.Profile.Number == SelectedProfile.Profile.Number))
        {
            SelectedProfile = null;
        }
    }

    private async Task SaveProfileAsync()
    {
        if (!int.TryParse(ProfileNumber, out var number) || number is < 1 or > 5)
        {
            StatusText = "Profile number must be between 1 and 5.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ProfileName) || string.IsNullOrWhiteSpace(ProfileApn))
        {
            StatusText = "Enter a profile name and APN.";
            return;
        }

        var auth = ProfileAuthType switch
        {
            "PAP" => 1,
            "CHAP" => 2,
            _ => 0
        };
        var saved = await _session.SaveProfileAsync(new MifiProfile(
            number,
            ProfileName.Trim(),
            ProfileApn.Trim(),
            ProfileUser.Trim(),
            ProfilePassword,
            auth));
        if (saved)
        {
            await _session.Settings.SetAsync($"profile.access.{number}", ProfileAccessNumber.Trim());
        }

        StatusText = saved ? "APN profile saved." : "The modem rejected the APN profile.";
        await RefreshProfilesAsync();
        var savedRow = Profiles.FirstOrDefault(p => p.Profile.Number == number);
        if (savedRow is not null)
        {
            SelectedProfile = savedRow;
        }
    }

    private void BrowseSound()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a message sound",
            Filter = "Wave files (*.wav)|*.wav|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            SoundFileName = dialog.FileName;
        }
    }

    private void NewProfile()
    {
        SelectedProfile = null;
        ProfileNumber = NextProfileNumber().ToString();
        ProfileName = string.Empty;
        ProfileApn = string.Empty;
        ProfileUser = string.Empty;
        ProfilePassword = string.Empty;
        ProfileAccessNumber = string.Empty;
        ProfileAuthType = "None";
        StatusText = "Enter the new profile details and press Save.";
    }

    private void DeleteProfile()
    {
        StatusText = "Deleting an APN profile is not supported by the stock firmware. Edit the profile instead.";
    }

    private void EditProfile()
    {
        if (SelectedProfile is not null)
        {
            LoadProfile(SelectedProfile);
        }
    }

    private void UndoProfile()
    {
        if (SelectedProfile is not null)
        {
            LoadProfile(SelectedProfile);
            StatusText = "Profile changes discarded.";
        }
        else
        {
            NewProfile();
        }
    }

    private void SetDefaultProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        _ = _session.Settings.SetAsync("profile.default", SelectedProfile.Profile.Number.ToString());
        StatusText = $"Profile {SelectedProfile.Profile.Number} is the default for manual connects.";
    }

    private async Task ImportProfilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import APN profiles",
            Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        var imported = 0;
        try
        {
            var lines = await File.ReadAllLinesAsync(dialog.FileName);
            var number = 1;
            foreach (var raw in lines)
            {
                if (number > 5)
                {
                    break;
                }

                var line = raw.Trim();
                if (line.Length == 0 ||
                    line.StartsWith("name", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith('#'))
                {
                    continue;
                }

                var columns = line.Split(',');
                if (columns.Length < 2)
                {
                    continue;
                }

                var name = columns[0].Trim();
                var apn = columns[1].Trim();
                var user = columns.Length > 2 ? columns[2].Trim() : string.Empty;
                var password = columns.Length > 3 ? columns[3].Trim() : string.Empty;
                var auth = columns.Length > 4 && int.TryParse(columns[4].Trim(), out var parsedAuth)
                    ? parsedAuth
                    : 0;
                if (name.Length == 0 || apn.Length == 0)
                {
                    continue;
                }

                if (await _session.SaveProfileAsync(new MifiProfile(number, name, apn, user, password, auth)))
                {
                    imported++;
                }

                number++;
            }

            StatusText = imported == 0
                ? "No profiles were imported. Use: name,apn,user,password,auth"
                : $"Imported {imported} profile(s).";
            await RefreshProfilesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private int NextProfileNumber()
    {
        for (var candidate = 1; candidate <= 5; candidate++)
        {
            if (!Profiles.Any(p => p.Profile.Number == candidate))
            {
                return candidate;
            }
        }

        return 1;
    }

    private void LoadProfile(ApnProfileRow row)
    {
        ProfileNumber = row.Profile.Number.ToString();
        ProfileName = row.Profile.Name;
        ProfileApn = row.Profile.Apn;
        ProfileUser = row.Profile.User;
        ProfilePassword = row.Profile.Password;
        ProfileAuthType = row.Profile.Auth switch
        {
            1 => "PAP",
            2 => "CHAP",
            _ => "None"
        };
        ProfileAccessNumber = _accessNumbers.TryGetValue(row.Profile.Number, out var access) ? access : string.Empty;
    }

    private async Task ConnectProfileAsync()
    {
        if (!int.TryParse(ProfileNumber, out var number) || number is < 1 or > 5)
        {
            StatusText = "Select a profile first.";
            return;
        }

        var result = await _session.ConnectProfileAsync(number);
        StatusText = result ? "Connection request sent." : "The modem rejected the connection request.";
    }

    private async Task ApplyNetworkModeAsync()
    {
        var mode = NetworkMode switch
        {
            "3G only" => 1,
            "4G only" => 2,
            _ => 0
        };
        var result = await _session.SetNetworkModeAsync(mode);
        if (result)
        {
            await _session.Settings.SetAsync("network.mode", NetworkMode);
        }

        StatusText = result ? "Network mode applied." : "The modem rejected the network mode.";
    }

    private void UpdateDiagnostics()
    {
        DiagnosticsText = _session.IsConnected
            ? $"Device: {Display.OrNotAvailable(_session.DeviceInfo.Model)}\\n" +
              $"Firmware: {Display.OrNotAvailable(_session.DeviceInfo.FirmwareVersion)}\\n" +
              $"SIM: {Display.OrNotAvailable(_session.SimInfo.State)}\\n" +
              $"Network: {Display.OrNotAvailable(_session.MifiStatus?.Operator ?? _session.NetworkInfo.Operator)} � {Display.OrNotAvailable(_session.MifiStatus?.NetworkType ?? _session.NetworkInfo.NetworkType)}\\n" +
              $"Signal: {_session.NetworkInfo.SignalDisplay}\\n" +
              $"Connection: {_session.ConnectionStatus.State}"
            : "No device is connected.";
    }

    private void ReportError(Exception ex)
    {
        StatusText = $"Settings operation failed: {ex.Message}";
        IsBusy = false;
    }
}






public sealed record SettingsSection(string Key, string Label)
{
    public string DisplayName => Label;
}

public sealed class ApnProfileRow
{
    public ApnProfileRow(MifiProfile profile)
    {
        Profile = profile;
    }

    public MifiProfile Profile { get; }
    public string Name => Profile.Name;
    public string Apn => Profile.Apn;
    public string Detail => $"{Profile.Name}  |  {Profile.Apn}";
}





