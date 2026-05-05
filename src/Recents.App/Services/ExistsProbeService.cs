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

        try
        {
            var probe = Task.Run(() => File.Exists(item.NormalizedPath) || Directory.Exists(item.NormalizedPath));
            var completed = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromMilliseconds(1500))).ConfigureAwait(false);
            if (completed != probe)
            {
                item.Exists = ExistsState.Unknown;
                return;
            }

            item.Exists = await probe.ConfigureAwait(false) ? ExistsState.Found : ExistsState.Missing;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ExistsProbeService: 探测失败 {Path}", LogPrivacy.Format(item.NormalizedPath));
            item.Exists = ExistsState.Unknown;
        }
    }
}
