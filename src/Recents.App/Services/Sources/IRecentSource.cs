using Recents.App.Models;

namespace Recents.App.Services.Sources;

// PRD §6.3 数据源变更事件
public record RecentChange(
    RecentChangeKind Kind,
    RecentItem       Item
);

public enum RecentChangeKind
{
    Added,    // 新建或更新
    Removed,  // 确认删除（仅当来源显式报告 Deleted 时）
}

// PRD §6.3 数据源抽象接口。
// 每个数据源实现：
//   - InitialScanAsync()  启动时增量扫描（仅扫 RecentLookbackDays 内的文件）
//   - Watch()             实时变更流（IObservable，由 FileSystemWatcher / 定时器驱动）
//   - Kind                标识当前来源类型，用于融合时位掩码合并
public interface IRecentSource
{
    SourceKinds Kind { get; }

    Task InitialScanAsync(CancellationToken ct);

    IObservable<RecentChange> Watch();
}
