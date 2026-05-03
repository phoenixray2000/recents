using System.Windows;
using Microsoft.Win32;
using Recents.App.Models;
using ThemeMode = Recents.App.Models.AppSettings.ThemeMode;
using WpfApp = System.Windows.Application;

namespace Recents.App.Services;

public sealed class ThemeManager
{
    public static ThemeManager Instance { get; } = new();

    private ResourceDictionary? _current;
    private ThemeMode _mode = ThemeMode.FollowSystem;

    public event EventHandler? ThemeChanged;
    public ThemeMode Mode => _mode;
    public bool IsDark { get; private set; }

    public void Initialize(ThemeMode mode)
    {
        _mode = mode;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Apply();
    }

    public void SetMode(ThemeMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        Apply();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _mode == ThemeMode.FollowSystem)
            WpfApp.Current.Dispatcher.Invoke(Apply);
    }

    private void Apply()
    {
        bool dark = _mode switch
        {
            ThemeMode.Dark  => true,
            ThemeMode.Light => false,
            _               => DetectSystemDark()
        };
        IsDark = dark;
        var uri  = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        var merged = WpfApp.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        merged.Insert(0, dict);
        _current = dict;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool DetectSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return (key?.GetValue("AppsUseLightTheme") as int? ?? 1) == 0;
    }
}
