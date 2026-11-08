using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using UZConnect.App.Services;
using UZConnect.App.Themes;
using UZConnect.App.ViewModels;

namespace UZConnect.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TrayIconService _tray;
    private bool _initialized;
    private bool _exitRequested;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _tray = new TrayIconService();
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += ExitApplication;
        _tray.ConnectRequested += () => RunFromTray(() => _viewModel.ConnectInternetAsync());
        _tray.DisconnectRequested += () => RunFromTray(() => _viewModel.DisconnectInternetAsync());
        _viewModel.ConnectionStateChanged += UpdateTrayState;
        UpdateTrayState();

        Loaded += (_, _) =>
        {
            FitToWorkArea();
            if (!_initialized)
            {
                _initialized = true;
                Dispatcher.BeginInvoke(() => _viewModel.SelectedNav = _viewModel.NavItems[0]);
            }

            if (AppPreferences.StartMinimized)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _viewModel.Session.Log.Info("App", "Started minimized to the notification area.");
                    WindowState = WindowState.Minimized;
                    HideToTray(showHint: true);
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        };
        StateChanged += (_, _) => UpdateWindowStateVisuals();
        Closing += OnClosing;
    }

    public void ApplyDarkTitleBar(bool isDark)
    {
        // The classic layout keeps a fixed blue title bar, matching the reference.
    }

    public void DisposeTray() => _tray.Dispose();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        if (AppPreferences.CloseToTray)
        {
            HideToTray(showHint: true);
            return;
        }

        ExitApplication();
    }

    private void HideToTray(bool showHint)
    {
        Hide();
        if (showHint)
        {
            _tray.ShowRunningHint();
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    private void RunFromTray(Func<Task> action)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await action();
            }
            catch
            {
                // Failures are reported by the page that owns the action.
            }
            finally
            {
                UpdateTrayState();
            }
        });
    }

    private void UpdateTrayState()
    {
        _tray.UpdateConnectionState(_viewModel.IsConnected, _viewModel.IsDataConnected);
    }

    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 24);
        Height = Math.Min(Height, area.Height - 24);
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
    }

    private void UpdateWindowStateVisuals()
    {
        if (MaximizeIcon is not null)
        {
            MaximizeIcon.Source = WindowState == WindowState.Maximized
                ? new BitmapImage(new Uri("pack://application:,,,/UZConnect;component/Assets/icons/title-restore.png"))
                : new BitmapImage(new Uri("pack://application:,,,/UZConnect;component/Assets/icons/title-max.png"));
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
