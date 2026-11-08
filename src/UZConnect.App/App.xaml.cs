using System.Windows;
using System.Windows.Threading;
using UZConnect.App.Services;
using UZConnect.App.ViewModels;

namespace UZConnect.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine(args.ExceptionObject);
        };

        AppPreferences.StartMinimized = e.Args.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        _viewModel = new MainViewModel();
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();

        await _viewModel.InitializeAsync();
        await LoadPreferencesAsync();
    }

    private async Task LoadPreferencesAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            AppPreferences.CloseToTray = await ReadBoolAsync(
                AppPreferences.SettingCloseToTray, fallback: true);
            AppPreferences.ConnectOnStart = await ReadBoolAsync(
                AppPreferences.SettingConnectOnStart, fallback: false);
            AppPreferences.StartWithWindows = await ReadBoolAsync(
                AppPreferences.SettingStartWithWindows, fallback: false);

            // Keep the startup entry in sync with the saved preference.
            if (AppPreferences.StartWithWindows)
            {
                StartupRegistration.SetEnabled(true);
            }
            else if (StartupRegistration.IsEnabled())
            {
                StartupRegistration.SetEnabled(false);
            }

            if (AppPreferences.ConnectOnStart && AppPreferences.StartMinimized)
            {
                _viewModel.EnableAutoConnectOnStart();
            }
        }
        catch
        {
            // Defaults apply when local settings cannot be read.
        }
    }

    private async Task<bool> ReadBoolAsync(string key, bool fallback)
    {
        var value = await _viewModel!.Session.Settings.GetAsync(key);
        return value is null
            ? fallback
            : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(UZConnect.Windows.AppPaths.Root, "ui-errors.log"), DateTimeOffset.Now.ToString("O") + Environment.NewLine + e.Exception + Environment.NewLine + Environment.NewLine); } catch { }
        MessageBox.Show(
            $"UZConnect encountered an unexpected error:\n\n{e.Exception.Message}\n\n" +
            "Details were written to the local diagnostics log.",
            "UZConnect",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is MainWindow window)
        {
            window.DisposeTray();
        }

        _viewModel?.Dispose();
        base.OnExit(e);
    }
}
