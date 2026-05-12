namespace Recents.App.Services;

public enum StartupCommandKind
{
    ShowMainWindow,
    PreviewPath,
}

public sealed record StartupCommand(StartupCommandKind Kind, string? Path)
{
    public static StartupCommand ShowMainWindow { get; } = new(StartupCommandKind.ShowMainWindow, null);

    public static StartupCommand PreviewPath(string path) =>
        new(StartupCommandKind.PreviewPath, path);

    public static StartupCommand Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], "--preview", StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                return ShowMainWindow;

            return PreviewPath(args[i + 1]);
        }

        return ShowMainWindow;
    }

    public SingleInstanceCommand ToSingleInstanceCommand() =>
        Kind == StartupCommandKind.PreviewPath && !string.IsNullOrWhiteSpace(Path)
            ? SingleInstanceCommand.PreviewPath(Path)
            : SingleInstanceCommand.ShowMainWindow();
}
