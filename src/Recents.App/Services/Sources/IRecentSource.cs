using Recents.App.Models;

namespace Recents.App.Services.Sources;

public record RecentChange(
    RecentChangeKind Kind,
    RecentItem Item
);

public enum RecentChangeKind
{
    Added,
    Removed,
    SourceUnavailable,
}

public interface IRecentSource
{
    SourceKinds Kind { get; }

    Task InitialScanAsync(CancellationToken ct);

    IObservable<RecentChange> Watch();
}
