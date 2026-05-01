namespace Recents.App.Models;

// PRD §8.1 数据传输对象：一条最近文件记录
public class RecentItem
{
    // 主键：PathNormalizer 规范化后的路径（大写盘符、无尾随分隔符、无 \\?\ 前缀）
    public string      NormalizedPath      { get; set; } = string.Empty;
    // 展示用文件名（不含路径）
    public string      DisplayName         { get; set; } = string.Empty;
    // 扩展名（含 .，如 ".docx"；文件夹为空字符串）
    public string      Extension           { get; set; } = string.Empty;
    // 文件类型分类（"Documents" / "Images" / ... / "Other"）
    public string      FileType            { get; set; } = "Other";
    // 最近操作时间：max(各来源时间)
    public DateTime    RecentTime          { get; set; }
    // 原文件 LastWriteTime（可空：文件夹/未知来源可能缺失）
    public DateTime?   TargetModifiedTime  { get; set; }
    // 文件大小（字节），文件夹 / 占位符不读取时为 null
    public long?       SizeBytes           { get; set; }
    // 文件存在性三态
    public ExistsState Exists              { get; set; } = ExistsState.Unknown;
    // 是否是文件夹
    public bool        IsFolder            { get; set; }
    // 是否已收藏（固定）
    public bool        IsFavorite          { get; set; }
    // 是否被用户隐藏（不展示，但保留索引）
    public bool        IsHidden            { get; set; }
    // 来源位掩码：多来源命中同一路径时，各来源标志位 OR 合并
    public SourceKinds Sources             { get; set; } = SourceKinds.None;
    // 图标缓存键（扩展名 + isFolder + DPI，用于 FileIconService）
    public string?     IconCacheKey        { get; set; }
    // 最近一次被任意来源看到的时间（用于 SQLite last_seen_time 字段）
    public DateTime    LastSeenTime        { get; set; }
}

// 文件存在性三态（PRD §8.1）
public enum ExistsState
{
    Missing = 0,   // 确认不存在
    Exists  = 1,   // 确认存在
    Unknown = 2,   // 未探测 / 网络断开 / 超时
}

// 数据来源位掩码（PRD §8.1 §6.3.1）
[Flags]
public enum SourceKinds
{
    None             = 0,
    KnownFolderWatch = 1 << 0,   // L1：Downloads/Desktop/Documents/Pictures/Videos/Music
    UserFolderWatch  = 1 << 1,   // L1：用户自定义本地目录
    UncFolderWatch   = 1 << 2,   // L1：用户自定义 UNC 路径
    RecentLnk        = 1 << 3,   // L1：%APPDATA%\Microsoft\Windows\Recent .lnk
    JumpListAuto     = 1 << 4,   // L2：AutomaticDestinations
    JumpListCustom   = 1 << 5,   // L3：CustomDestinations
    OfficeMru        = 1 << 6,   // L2：Office MRU 注册表
    OpenSavePidlMru  = 1 << 7,   // L3：OpenSavePidlMRU 注册表
    RecentDocsReg    = 1 << 8,   // L3：RecentDocs 注册表（降级补充）
}
