using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using UZConnect.Core.Models;
using UZConnect.Windows;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed class DiagnosticsViewModel : ObservableObject, IPageViewModel
{
    private readonly ModemSession _session;
    private bool _isBusy;
    private bool _showProbePrompt;
    private string _statusText = "Diagnostics are read-only unless you explicitly probe or export.";
    private string _commandText = string.Empty;
    private string _commandOutput = string.Empty;
    private DateTimeOffset? _lastCommunication;

    public DiagnosticsViewModel(ModemSession session)
    {
        _session = session;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, onError: ReportError);
        RequestProbeCommand = new RelayCommand(() => ShowProbePrompt = true, () => IsConnected);
        ConfirmProbeCommand = new AsyncRelayCommand(ProbeAsync, () => !IsBusy, ReportError);
        CancelProbeCommand = new RelayCommand(() => ShowProbePrompt = false);
        ExportCommand = new AsyncRelayCommand(ExportAsync, onError: ReportError);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        CopySummaryCommand = new RelayCommand(CopySummary);
        RunCommandCommand = new AsyncRelayCommand(RunCommandAsync, () => !string.IsNullOrWhiteSpace(CommandText), ReportError);
        ClearCommandCommand = new RelayCommand(() => CommandOutput = string.Empty);

        foreach (var entry in session.Diagnostics.TakeLast(300))
        {
            LogEntries.Add(entry);
        }

        session.Log.EntryAdded += OnLogEntryAdded;
    }

    public string Title => "Diagnostics";
    public string Subtitle => "USB evidence, transport state, logs and a shareable redacted report.";

    public ObservableCollection<UsbDeviceRow> UsbDevices { get; } = new();
    public ObservableCollection<AtProbeResult> AtProbes { get; } = new();
    public ObservableCollection<DiagnosticEntry> LogEntries { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand RequestProbeCommand { get; }
    public ICommand ConfirmProbeCommand { get; }
    public ICommand CancelProbeCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopySummaryCommand { get; }
    public ICommand RunCommandCommand { get; }
    public ICommand ClearCommandCommand { get; }

    public bool IsConnected => _session.IsConnected;
    public bool AdbFound => _session.AdbAvailable;
    public string AdbPath => _session.AdbPath ?? "Not found";
    public string AdbState => IsConnected ? "Authorized device" : "No active session";
    public string AdbAuthorizedText => IsConnected ? "Yes" : "No";
    public string Model => IsConnected ? Display.OrNotAvailable(_session.DeviceInfo.Model) : "Not available";
    public string Firmware => IsConnected ? Display.OrNotAvailable(_session.DeviceInfo.FirmwareVersion) : "Not available";
    public string Hardware => IsConnected ? Display.OrNotAvailable(_session.DeviceInfo.HardwareRevision) : "Not available";
    public string Serial => IsConnected ? Display.OrNotAvailable(_session.DeviceInfo.SerialNumber) : "Not available";
    public string Provider => IsConnected && _session.ActiveDevice is not null
        ? $"{_session.ActiveDevice.ProviderLabel} · {_session.ActiveDevice.VidPid}"
        : "Not available";
    public string ComPortsText => _session.ComPorts.Count == 0 ? "No COM ports found" : string.Join(", ", _session.ComPorts);
    public string UsbCountText => $"{UsbDevices.Count} USB device(s)";
    public string LogPath => _session.LogFilePath;
    public string DataFolder => AppPaths.Root;
    public string AppVersion => typeof(DiagnosticsViewModel).Assembly.GetName().Version?.ToString() ?? "0.1.0";
    public string LastCommunication => Display.Timestamp(_lastCommunication);
    public string ProbeSummary => AtProbes.Count == 0
        ? "No AT probe has been run. The stock modem's only Qualcomm serial endpoint is a diagnostics channel; it is not assumed to speak AT."
        : string.Join(Environment.NewLine, AtProbes.Select(p => p.Summary));

    public bool HasCommandOutput => !string.IsNullOrWhiteSpace(CommandOutput);
    public bool HasAtProbes => AtProbes.Count > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool ShowProbePrompt
    {
        get => _showProbePrompt;
        private set => SetProperty(ref _showProbePrompt, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CommandText
    {
        get => _commandText;
        set => SetProperty(ref _commandText, value);
    }

    public string CommandOutput
    {
        get => _commandOutput;
        private set
        {
            if (SetProperty(ref _commandOutput, value))
            {
                OnPropertyChanged(nameof(HasCommandOutput));
            }
        }
    }

    public async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await RefreshFromSessionAsync(cancellationToken);
    }

    public async Task RefreshFromSessionAsync(CancellationToken cancellationToken = default)
    {
        UsbDevices.Clear();
        foreach (var device in _session.UsbDevices)
        {
            UsbDevices.Add(new UsbDeviceRow(device));
        }

        _lastCommunication = _session.LastSuccessfulCommunication;
        Raise(nameof(IsConnected), nameof(AdbFound), nameof(AdbPath), nameof(AdbState), nameof(AdbAuthorizedText),
            nameof(Model), nameof(Firmware), nameof(Hardware), nameof(Serial), nameof(Provider), nameof(ComPortsText),
            nameof(UsbCountText), nameof(LogPath), nameof(DataFolder), nameof(AppVersion), nameof(LastCommunication),
            nameof(ProbeSummary), nameof(HasAtProbes));
        await Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Refreshing diagnostics…";
        try
        {
            await _session.ScanAsync();
            await RefreshFromSessionAsync();
            StatusText = "Diagnostics refreshed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ProbeAsync()
    {
        ShowProbePrompt = false;
        IsBusy = true;
        StatusText = "Probing serial ports for an AT command channel…";
        try
        {
            var results = await _session.ProbeSerialAtAsync();
            AtProbes.Clear();
            foreach (var result in results)
            {
                AtProbes.Add(result);
            }

            Raise(nameof(ProbeSummary), nameof(HasAtProbes));
            StatusText = results.Any(r => r.Success)
                ? "AT channel found. The serial provider remains experimental."
                : "No AT command channel was found. ADB remains the verified transport.";
        }
        catch (Exception ex)
        {
            StatusText = $"AT probe failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportAsync()
    {
        IsBusy = true;
        StatusText = "Writing diagnostic report…";
        try
        {
            var path = await _session.ExportReportAsync();
            StatusText = $"Report saved: {path}";
            try
            {
                Clipboard.SetText(path);
            }
            catch
            {
                // Clipboard may be busy; the path is visible in the status line.
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = AppPaths.ExportsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open folder: {ex.Message}";
        }
    }

    private void CopySummary()
    {
        try
        {
            Clipboard.SetText(BuildSummary());
            StatusText = "Redacted diagnostic summary copied to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusText = $"Clipboard copy failed: {ex.Message}";
        }
    }

    private string BuildSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine("UZConnect diagnostic summary");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        builder.AppendLine($"App: {AppVersion}");
        builder.AppendLine($"ADB found: {AdbFound} ({AdbPath})");
        builder.AppendLine($"ADB state: {AdbState}");
        builder.AppendLine($"Provider: {Provider}");
        builder.AppendLine($"Model: {Model}");
        builder.AppendLine($"Firmware: {Firmware}");
        builder.AppendLine($"Hardware: {Hardware}");
        builder.AppendLine($"Serial: {Serial}");
        builder.AppendLine($"COM ports: {ComPortsText}");
        builder.AppendLine($"Last communication: {LastCommunication}");
        builder.AppendLine($"USB devices: {UsbCountText}");
        builder.AppendLine();
        builder.AppendLine("Recent diagnostics:");
        foreach (var entry in LogEntries.TakeLast(40))
        {
            builder.AppendLine($"{entry.Timestamp:O} {entry.LevelLabel,-5} [{entry.Category}] {entry.Message}");
        }

        builder.AppendLine();
        builder.AppendLine("SMS contents, phone numbers and full IMEIs are intentionally omitted.");
        return builder.ToString();
    }

    private async Task RunCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(CommandText))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _session.RunDiagnosticCommandAsync(CommandText.Trim());
            CommandOutput = result.Success
                ? result.Output
                : $"Blocked or failed: {result.Error}{Environment.NewLine}{result.Output}";
            StatusText = result.Success ? "Read-only command completed." : "Command was rejected or failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnLogEntryAdded(object? sender, DiagnosticEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            AppendLog(entry);
        }
        else
        {
            dispatcher.BeginInvoke(() => AppendLog(entry));
        }
    }

    private void AppendLog(DiagnosticEntry entry)
    {
        LogEntries.Add(entry);
        while (LogEntries.Count > 500)
        {
            LogEntries.RemoveAt(0);
        }

        _lastCommunication = _session.LastSuccessfulCommunication;
        OnPropertyChanged(nameof(LastCommunication));
    }

    private void ReportError(Exception ex)
    {
        StatusText = $"Diagnostics operation failed: {ex.Message}";
        IsBusy = false;
    }

    public void Stop()
    {
        _session.Log.EntryAdded -= OnLogEntryAdded;
    }
}

public sealed class DiagnosticLevelBrushConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var level = value is DiagnosticLevel diagnosticLevel ? diagnosticLevel : DiagnosticLevel.Info;
        var key = level switch
        {
            DiagnosticLevel.Error => "Danger",
            DiagnosticLevel.Warning => "Warning",
            DiagnosticLevel.Trace => "TextTertiary",
            _ => "TextSecondary"
        };
        return Application.Current?.TryFindResource(key) ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
