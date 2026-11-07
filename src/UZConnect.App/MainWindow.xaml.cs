using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using UZConnect.App.Themes;
using UZConnect.App.ViewModels;

namespace UZConnect.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _initialized;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            FitToWorkArea();
            if (!_initialized)
            {
                _initialized = true;
                Dispatcher.BeginInvoke(() => _viewModel.SelectedNav = _viewModel.NavItems[0]);
            }
        };
        StateChanged += (_, _) => UpdateWindowStateVisuals();
    }

    public void ApplyDarkTitleBar(bool isDark)
    {
        // The classic layout keeps a fixed blue title bar, matching the reference.
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
