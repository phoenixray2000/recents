using Recents.App.Services.Sources;

namespace Recents.App.Models;

// PRD §8.3 全量应用设置，序列化到 %APPDATA%\Recents\settings.json
public class AppSettings
{
    public enum ViewDensity
    {
        Standard,
        Compact
    }

    // General
    public bool LaunchAtStartup   { get; set; } = false;
    public bool AlwaysOnTop       { get; set; } = true;
    public bool HideOnFocusLost   { get; set; } = false;
    public bool CloseToTray       { get; set; } = true;
    public bool ShowFolders       { get; set; } = true;
    public int  MaxRecentItems    { get; set; } = 200;
    public string DefaultSort     { get; set; } = "RecentTime";
    public ViewDensity CurrentDensity { get; set; } = ViewDensity.Standard;
    public double WindowWidth      { get; set; } = 600;
    public double WindowHeight     { get; set; } = 760;
    public double? WindowTop       { get; set; } = null;
    public double? WindowLeft      { get; set; } = null;
    public bool  StartMinimized   { get; set; } = false;

    // Hotkey（字符串表示，格式 "Ctrl+Alt+R"）
    public string Hotkey { get; set; } = "Alt+Shift+Z";

    // Sources（默认已知文件夹在 SettingsService 初始化时写入）
    public List<SourceConfig> Sources { get; set; } = new();

    public List<SystemSourceConfig> SystemSources { get; set; } = new()
    {
        new() { Kind = SourceKinds.RecentLnk, Enabled = true },
        new() { Kind = SourceKinds.OfficeMru, Enabled = true },
        new() { Kind = SourceKinds.OpenSavePidlMru, Enabled = true },
    };

    // Filters
    public List<string> ExcludedExtensions { get; set; } = new()
    {
        ".tmp", ".temp", ".lnk", ".url", ".ini", ".log",
        ".crdownload", ".partial", ".part", ".opdownload", ".!ut"
    };
    public List<string> ExcludedPaths { get; set; } = new()
    {
        @"AppData\Local\Temp",
        @"Windows\Temp",
        "$Recycle.Bin",
        "node_modules",
        ".git",
        "__pycache__",
        "bin",
        "obj",
        ".vs",
        ".idea",
        ".vscode",
        "dist",
        "target",
        "build",
        "out",
        "artifacts",
        "publish"
    };
    public List<string> ExcludedKeywords  { get; set; } = new();
    public List<string> WhitelistedPaths  { get; set; } = new();

    // 文件类型分组（key=分类名, value=扩展名列表）
    public Dictionary<string, List<string>> ClassificationSourceGroups { get; set; } = new()
    {
        ["Documents"] = new() { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".rtf" },
        ["Images"]    = new() { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".heic" },
        ["Videos"]    = new() { ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm" },
        ["Audio"]     = new() { ".mp3", ".wav", ".flac", ".aac", ".m4a" },
        ["Archives"]  = new() { ".zip", ".rar", ".7z", ".tar", ".gz" },
        ["Code"]      = new() { ".py", ".js", ".ts", ".tsx", ".cs", ".cpp", ".c", ".h", ".java", ".go", ".rs",
                                ".json", ".xml", ".yaml", ".yml", ".html", ".css", ".sql" },
    };

    // 日志级别（false=默认截断路径, true=完整路径, PRD §12）
    public bool VerboseLogging { get; set; } = false;

    // 是否已显示过“关闭到托盘”提示
    public bool ClosedToTrayNoticeShown { get; set; } = false;
}

public class SystemSourceConfig
{
    public SourceKinds Kind { get; set; }
    public bool Enabled { get; set; } = true;
}
