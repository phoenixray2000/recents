namespace Recents.App.Localization;

// Static helper for C# string lookup. Call like Loc.T("Action_Open").
public static class Loc
{
    public static string T(string key) => LocalizationManager.Instance[key];

    public static string T(string key, params object?[] args)
    {
        var format = LocalizationManager.Instance[key];
        try { return string.Format(format, args); }
        catch { return format; }
    }
}
