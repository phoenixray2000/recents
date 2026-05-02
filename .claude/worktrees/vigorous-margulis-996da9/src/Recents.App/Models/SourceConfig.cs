using Recents.App.Models;

namespace Recents.App.Services.Sources;

// PRD §8.2 用户配置的单个数据源（已知文件夹 / 自定义本地目录 / UNC 路径）。
// 注意：SourceConfig 移到 Services.Sources 命名空间以避免循环引用，
// 通过 AppSettings 中的 using 引入。
public class SourceConfig
{
    public string     Id                 { get; set; } = Guid.NewGuid().ToString();
    public SourceKinds Kind              { get; set; } = SourceKinds.KnownFolderWatch;
    public string     Path              { get; set; } = string.Empty;
    public bool       Enabled           { get; set; } = true;
    public int        RecentLookbackDays { get; set; } = 30;
    // 已知文件夹 GUID（空字符串 = 自定义路径）
    public string     KnownFolderGuid   { get; set; } = string.Empty;
    // 显示名称（用于设置页 Sources 列表）
    public string     DisplayName       { get; set; } = string.Empty;
}
