using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed class StatisticsViewModel : ObservableObject, IPageViewModel
{
    private const int MaxPoints = 60;
    private const double ChartWidth = 620;
    private const double ChartHeight = 260;

    private readonly ModemSession _session;
    private readonly DispatcherTimer _timer;
    private readonly Queue<double> _uploadHistory = new();
    private readonly Queue<double> _downloadHistory = new();
    private bool _isBusy;
    private string _lastReset = "This session";
    private double _uploadSpeed;
    private double _downloadSpeed;
    private long _uploadedKb;
    private long _downloadedKb;

    public StatisticsViewModel(ModemSession session)
    {
        _session = session;
        UploadPoints = new PointCollection();
        DownloadPoints = new PointCollection();
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, onError: ReportError);
        ResetCommand = new RelayCommand(() => { _uploadHistory.Clear(); _downloadHistory.Clear(); UploadPoints.Clear(); DownloadPoints.Clear(); });
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) =>
        {
            if (_session.IsConnected)
            {
                await SampleAsync();
            }
        };
        _timer.Start();
    }

    public string Title => "Statistics";
    public string Subtitle => "Usage statistics";

    public ICommand RefreshCommand { get; }
    public ICommand ResetCommand { get; }

    public PointCollection UploadPoints { get; }
    public PointCollection DownloadPoints { get; }
    public double ChartWidthValue => ChartWidth;
    public double ChartHeightValue => ChartHeight;

    public string TransferTime => _session.SessionStart is null
        ? "00:00:00"
        : (DateTimeOffset.Now - _session.SessionStart.Value) is { } span
            ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : "00:00:00";

    public string UploadSpeed => $"{_uploadSpeed:0.0} KB/s";
    public string DownloadSpeed => $"{_downloadSpeed:0.0} KB/s";
    public string UploadedData => FormatKb(_uploadedKb);
    public string DownloadedData => FormatKb(_downloadedKb);
    public string DailyUpload => UploadedData;
    public string DailyDownload => DownloadedData;
    public string MonthlyUpload => "Not available";
    public string MonthlyDownload => "Not available";
    public string YearlyUpload => "Not available";
    public string YearlyDownload => "Not available";
    public string LastReset => _lastReset;
    public string ChartMaximum { get; private set; } = "60.0";

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await SampleAsync(cancellationToken);
    }

    public Task RefreshFromSessionAsync(CancellationToken cancellationToken = default)
        => SampleAsync(cancellationToken);

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await _session.RefreshAsync();
            await SampleAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SampleAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _session.RefreshMifiAsync(cancellationToken);
        }
        catch
        {
            // Keep the last sample; the status bar shows device state.
        }

        var traffic = _session.MifiTraffic;
        if (traffic is null)
        {
            return;
        }

        _uploadedKb = traffic.UpKb;
        _downloadedKb = traffic.DownKb;
        _uploadSpeed = _session.TxRateKbps;
        _downloadSpeed = _session.RxRateKbps;

        _uploadHistory.Enqueue(_uploadSpeed);
        _downloadHistory.Enqueue(_downloadSpeed);
        while (_uploadHistory.Count > MaxPoints) _uploadHistory.Dequeue();
        while (_downloadHistory.Count > MaxPoints) _downloadHistory.Dequeue();

        RebuildChart();
        Raise(nameof(UploadSpeed), nameof(DownloadSpeed), nameof(UploadedData), nameof(DownloadedData),
            nameof(DailyUpload), nameof(DailyDownload), nameof(TransferTime), nameof(LastReset));
    }

    private void RebuildChart()
    {
        UploadPoints.Clear();
        DownloadPoints.Clear();
        var max = Math.Max(1d, Math.Max(_uploadHistory.DefaultIfEmpty(0).Max(), _downloadHistory.DefaultIfEmpty(0).Max()));
        max = Math.Ceiling(max / 10d) * 10d;
        ChartMaximum = max.ToString("0.0");
        OnPropertyChanged(nameof(ChartMaximum));

        var upload = _uploadHistory.ToArray();
        var download = _downloadHistory.ToArray();
        var step = ChartWidth / Math.Max(1, MaxPoints - 1);
        var offset = MaxPoints - upload.Length;

        for (var i = 0; i < upload.Length; i++)
        {
            var x = (offset + i) * step;
            UploadPoints.Add(new Point(x, ChartHeight - (upload[i] / max * ChartHeight)));
        }

        for (var i = 0; i < download.Length; i++)
        {
            var x = (offset + i) * step;
            DownloadPoints.Add(new Point(x, ChartHeight - (download[i] / max * ChartHeight)));
        }
    }

    private static string FormatKb(long kb)
    {
        if (kb >= 1024 * 1024) return $"{kb / 1024d / 1024d:0.00} GB";
        if (kb >= 1024) return $"{kb / 1024d:0.00} MB";
        return $"{kb:0.00} KB";
    }

    private void ReportError(Exception ex)
    {
        IsBusy = false;
    }
}
