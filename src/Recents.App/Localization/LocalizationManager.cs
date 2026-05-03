using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Recents.App.Localization;

// Runtime language switching. Bindings to this[key] refresh on PropertyChanged("Item[]").
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    private static readonly ResourceManager _rm =
        new("Recents.App.Localization.Strings", typeof(LocalizationManager).Assembly);

    public string this[string key]
    {
        get
        {
            try { return _rm.GetString(key, CultureInfo.CurrentUICulture) ?? key; }
            catch { return key; }
        }
    }

    public string CurrentCulture => CultureInfo.CurrentUICulture.Name;

    public void SetLanguage(string? cultureName)
    {
        CultureInfo culture;
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            culture = CultureInfo.InstalledUICulture;
        }
        else
        {
            try { culture = new CultureInfo(cultureName); }
            catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
        }

        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        // Keep CurrentCulture as system default to avoid number/date format surprises;
        // only UICulture drives resource lookup.

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;
}
