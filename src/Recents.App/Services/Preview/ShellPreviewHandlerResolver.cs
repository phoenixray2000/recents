using Microsoft.Win32;

namespace Recents.App.Services.Preview;

public interface IPreviewHandlerRegistry
{
    string? GetDefaultValue(string subKeyPath);
}

public sealed class WindowsPreviewHandlerRegistry : IPreviewHandlerRegistry
{
    public string? GetDefaultValue(string subKeyPath)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(subKeyPath);
        return key?.GetValue(null) as string;
    }
}

public static class ShellPreviewHandlerResolver
{
    public static readonly Guid PreviewHandlerShellExtensionGuid =
        Guid.Parse("8895b1c6-b41f-4c1c-a562-0d564250836f");

    public static Guid? TryResolve(string extension, IPreviewHandlerRegistry? registry = null)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        registry ??= new WindowsPreviewHandlerRegistry();

        var normalizedExt = extension.StartsWith(".", StringComparison.Ordinal)
            ? extension
            : "." + extension;

        var direct = TryReadHandler(normalizedExt, registry);
        if (direct.HasValue)
            return direct;

        var progId = registry.GetDefaultValue(normalizedExt);
        if (string.IsNullOrWhiteSpace(progId))
            return null;

        return TryReadHandler(progId, registry);
    }

    private static Guid? TryReadHandler(string classKey, IPreviewHandlerRegistry registry)
    {
        var value = registry.GetDefaultValue($@"{classKey}\shellex\{PreviewHandlerShellExtensionGuid:B}");
        return Guid.TryParse(value, out var guid) ? guid : null;
    }
}
