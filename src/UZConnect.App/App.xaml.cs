using System.Windows;
using System.Windows.Threading;
using UZConnect.App.Themes;
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

        _viewModel = new MainViewModel();
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
        await _viewModel.InitializeAsync();
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
        _viewModel?.Dispose();
        base.OnExit(e);
    }
}

