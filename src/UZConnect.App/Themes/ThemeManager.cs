using System.Windows;
using Microsoft.Win32;

namespace UZConnect.App.Themes;

public enum AppTheme
{
    System,
    Light,
    Dark
}

public static class ThemeManager
{
    public static event EventHandler<AppTheme>? ThemeChanged;

    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static bool IsDarkEffective => Current switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => IsSystemDark()
    };

    public static void Apply(AppTheme theme, bool persist = false)
    {
        Current = theme;
        var isDark = theme == AppTheme.Dark || (theme == AppTheme.System && IsSystemDark());
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(
                isDark ? "pack://application:,,,/Themes/Dark.xaml" : "pack://application:,,,/Themes/Light.xaml",
                UriKind.Absolute)
        };

        var app = Application.Current;
        if (app is not null)
        {
            var merged = app.Resources.MergedDictionaries;
            if (merged.Count > 0)
            {
                merged[0] = dictionary;
            }
            else
            {
                merged.Add(dictionary);
            }

            foreach (Window window in app.Windows)
            {
                if (window is MainWindow mainWindow)
                {
                    mainWindow.ApplyDarkTitleBar(isDark);
                }
            }
        }

        ThemeChanged?.Invoke(null, theme);
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }
}
