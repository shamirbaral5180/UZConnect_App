using System.Diagnostics;
using Microsoft.Win32;

namespace UZConnect.App.Services;

/// <summary>Registers the current executable in the per-user Windows startup list.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UZConnect";

    public static string ExecutablePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? string.Empty;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var path = ExecutablePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{path}\" --minimized", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
