using System.Text.RegularExpressions;
using Recents.App.Models;
using Serilog;

namespace Recents.App.Services.Clipboard;

internal sealed class ClipboardSensitiveFilter
{
    private readonly AppSettings _settings;

    public ClipboardSensitiveFilter(AppSettings settings)
    {
        _settings = settings;
    }

    public bool ShouldSkip(string? text)
    {
        if (!_settings.IgnoreSensitiveText || string.IsNullOrEmpty(text))
            return false;

        foreach (var pattern in _settings.ClipboardSensitivePatterns)
        {
            try
            {
                if (Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, TimeSpan.FromMilliseconds(80)))
                {
                    Log.Information("Clipboard skipped by sensitive filter");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Clipboard sensitive pattern ignored");
            }
        }

        return false;
    }
}
