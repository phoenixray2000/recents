using System.IO;

namespace Recents.App.Services;

public static class LogPrivacy
{
    public static bool VerboseLogging { get; set; }

    public static string Format(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (VerboseLogging) return path;

        try
        {
            var fileName = Path.GetFileName(path.TrimEnd('\\', '/'));
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(fileName))
                return string.IsNullOrWhiteSpace(root) ? "<path>" : $"{root}...";

            return string.IsNullOrWhiteSpace(root) ? fileName : $"{root}...\\{fileName}";
        }
        catch
        {
            return "<path>";
        }
    }
}
