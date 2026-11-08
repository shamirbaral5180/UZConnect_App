namespace UZConnect.App.Services;

/// <summary>
/// Preferences the window and tray need synchronously. The authoritative copies live in the
/// local settings database; these are hydrated at startup and kept in sync by Settings.
/// </summary>
public static class AppPreferences
{
    public static bool CloseToTray { get; set; } = true;
    public static bool StartWithWindows { get; set; }
    public static bool ConnectOnStart { get; set; }
    public static bool StartMinimized { get; set; }

    public const string SettingCloseToTray = "ui.closeToTray";
    public const string SettingStartWithWindows = "app.startWithWindows";
    public const string SettingConnectOnStart = "connection.autoData";
}
