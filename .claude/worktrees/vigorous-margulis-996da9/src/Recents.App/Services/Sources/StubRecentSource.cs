using Recents.App.Models;

namespace Recents.App.Services.Sources;

// 所有 IRecentSource 骨架类的默认 stub 基类。
// P1/P2 阶段的 Source 未实现时暂时继承此类，保证编译通过。
// 已在 P0 中完整实现的类（KnownFolderWatchSource / RecentLnkSource）直接实现 IRecentSource。
public abstract class StubRecentSource : IRecentSource
{
    public virtual SourceKinds Kind => SourceKinds.None;

    public virtual Task InitialScanAsync(CancellationToken ct) => Task.CompletedTask;

    // 返回一个永不发射的 Observable（stub 实现）
    public virtual IObservable<RecentChange> Watch() => NullObservable<RecentChange>.Instance;
}

// 永不发射、永不完成的 Observable stub（避免引入 System.Reactive 依赖）
internal sealed class NullObservable<T> : IObservable<T>
{
    public static readonly NullObservable<T> Instance = new();
    private NullObservable() { }
    public IDisposable Subscribe(IObserver<T> observer) => NullDisposable.Instance;
}

internal sealed class NullDisposable : IDisposable
{
    public static readonly NullDisposable Instance = new();
    private NullDisposable() { }
    public void Dispose() { }
}
