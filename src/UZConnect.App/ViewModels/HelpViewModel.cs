using System.Diagnostics;
using System.Windows.Input;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed class HelpViewModel : ObservableObject, IPageViewModel
{
    private readonly ModemSession _session;

    public HelpViewModel(ModemSession session)
    {
        _session = session;
        OpenManualCommand = new RelayCommand(OpenManual);
        RefreshCommand = new RelayCommand(() => Raise(nameof(DeviceModel), nameof(Firmware), nameof(AppVersion)));
    }

    public string Title => "Help";
    public string Subtitle => "Help and about";
    public ICommand OpenManualCommand { get; }
    public ICommand RefreshCommand { get; }

    public string AppVersion => typeof(HelpViewModel).Assembly.GetName().Version?.ToString() ?? "0.1.0";
    public string DeviceModel => _session.IsConnected ? _session.DeviceInfo.Model : "No device";
    public string Firmware => _session.IsConnected ? _session.DeviceInfo.FirmwareVersion : "Not available";

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        Raise(nameof(DeviceModel), nameof(Firmware));
        return Task.CompletedTask;
    }

    public Task RefreshFromSessionAsync(CancellationToken cancellationToken = default)
    {
        Raise(nameof(DeviceModel), nameof(Firmware));
        return Task.CompletedTask;
    }

    private static void OpenManual()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://ftp.dlink.de/dwm/dwm-222/documentation/DWM-222_man_reva1_Manual_en.pdf",
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening the browser is optional.
        }
    }
}
