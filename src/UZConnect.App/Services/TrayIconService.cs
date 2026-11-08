using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace UZConnect.App.Services;

/// <summary>Notification-area icon and its classic context menu.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _connect;
    private readonly Forms.ToolStripMenuItem _disconnect;
    private bool _balloonShown;
    private bool _disposed;

    public TrayIconService()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open UZConnect", null, (_, _) => OpenRequested?.Invoke());
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        _connect = new Forms.ToolStripMenuItem("Connect to Internet", null, (_, _) => ConnectRequested?.Invoke());
        _disconnect = new Forms.ToolStripMenuItem("Disconnect", null, (_, _) => DisconnectRequested?.Invoke());
        var exit = new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke());

        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_connect);
        menu.Items.Add(_disconnect);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "UZConnect - 4G modem manager",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action? ConnectRequested;
    public event Action? DisconnectRequested;

    public void UpdateConnectionState(bool connected, bool dataConnected)
    {
        _connect.Enabled = connected && !dataConnected;
        _disconnect.Enabled = connected && dataConnected;
        _icon.Text = !connected
            ? "UZConnect - modem not connected"
            : dataConnected ? "UZConnect - Internet connected" : "UZConnect - Internet disconnected";
    }

    public void ShowRunningHint()
    {
        if (_balloonShown)
        {
            return;
        }

        _balloonShown = true;
        try
        {
            _icon.ShowBalloonTip(2500, "UZConnect is still running",
                "The modem stays connected in the notification area. Double-click the icon to open UZConnect.",
                Forms.ToolTipIcon.Info);
        }
        catch
        {
            // Balloons are optional on older Windows shells.
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(
                new Uri("pack://application:,,,/UZConnect;component/Assets/uzconnect.ico"));
            if (resource?.Stream is { } stream)
            {
                using (stream)
                {
                    return new Icon(stream);
                }
            }
        }
        catch
        {
            // Fall back to the stock application icon.
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
