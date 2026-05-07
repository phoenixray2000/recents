namespace Recents.App.Services.Clipboard;

internal sealed class ClipboardBlobDeletionQueue
{
    public static readonly TimeSpan Delay = TimeSpan.FromHours(24);

    public DateTime CutoffUtc => DateTime.UtcNow.Subtract(Delay);
}
