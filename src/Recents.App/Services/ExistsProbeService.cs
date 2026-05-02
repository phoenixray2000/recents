using System.IO;
using Recents.App.Models;
using Serilog;

namespace Recents.App.Services;

// PRD §6.12 存量探测服务。
// 当条目显示在 UI 列表时，探测真实磁盘存在性。
// 探测结果（Missing/Found）写回 DB 并通知 UI 刷新。
public class ExistsProbeService
{
    private readonly RecentIndexService _indexService;

    public ExistsProbeService(RecentIndexService indexService)
    {
        _indexService = indexService;
    }

    public async Task ProbeAsync(RecentItem item)
    {
        if (item.Exists != ExistsState.Unknown) return;

        await Task.Run(() =>
        {
            try
            {
                var exists = File.Exists(item.NormalizedPath) || Directory.Exists(item.NormalizedPath);
                item.Exists = exists ? ExistsState.Found : ExistsState.Missing;
                
                // Note: Updating DB and notifying UI is handled by RecentIndexService or via property updates
                // However, item itself is just a model. We need to notify the VM.
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ExistsProbeService: 探测失败 {Path}", item.NormalizedPath);
            }
        });
    }
}
