using Recents.App.Services.Sources;

namespace Recents.App.Models;

// PRD §8.3 全量应用设置，序列化到 %APPDATA%\Recents\settings.json
public class AppSettings
{
    public enum ViewDensity { Standard, Compact }
    public enum ThemeMode   { FollowSystem, Dark, Light }

    // General
    public bool LaunchAtStartup   { get; set; } = false;
    public bool AlwaysOnTop       { get; set; } = true;
    public bool HideOnFocusLost   { get; set; } = false;
    public bool CloseToTray       { get; set; } = true;
    public int  MaxRecentItems    { get; set; } = 200;
    public ViewDensity CurrentDensity { get; set; } = ViewDensity.Standard;
    public double WindowWidth      { get; set; } = 600;
    public double WindowHeight     { get; set; } = 760;
    public double? WindowTop       { get; set; } = null;
    public double? WindowLeft      { get; set; } = null;
    public bool  StartMinimized   { get; set; } = false;
    public bool PreviewEnabled { get; set; } = true;
    public bool ExternalSpacePreviewEnabled { get; set; } = false;
    public bool ShowSystemAndHiddenFiles { get; set; } = false;
    public int OpenWithMaxAppsPerType { get; set; } = 3;
    public Dictionary<string, List<OpenWithAppConfig>> OpenWithHistory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FavoriteGroup> FavoriteGroups { get; set; } = new();
    public Dictionary<string, string> FavoriteGroupAssignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // 界面语言（空 = 跟随系统；支持 "en-US"、"zh-CN" 等 BCP 47 标识）
    public string Language { get; set; } = "";

    // 界面主题（FollowSystem = 跟随 Windows 深色/浅色设置）
    public ThemeMode Theme { get; set; } = ThemeMode.FollowSystem;

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
        @"bin\Debug",
        @"bin\Release",
        @"obj\Debug",
        @"obj\Release"
    };
    public List<string> ExcludedKeywords  { get; set; } = new();
    public List<string> WhitelistedPaths  { get; set; } = new();
    public List<string> HiddenPaths { get; set; } = new();

    // 文件类型分组（key=分类名, value=扩展名列表）
    public Dictionary<string, List<string>> ClassificationSourceGroups { get; set; } = new()
    {
        ["Documents"] = new() {
            ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".rtf",
            ".py", ".js", ".ts", ".tsx", ".cs", ".cpp", ".c", ".h", ".java", ".go", ".rs",
            ".json", ".xml", ".yaml", ".yml", ".html", ".css", ".sql"
        },
        ["Images"]    = new() { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".heic" },
        ["Videos"]    = new() { ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm" },
        ["Audio"]     = new() { ".mp3", ".wav", ".flac", ".aac", ".m4a" },
        ["Archives"]  = new() { ".zip", ".rar", ".7z", ".tar", ".gz" },
    };

    // 日志级别（false=默认截断路径, true=完整路径, PRD §12）
    public bool VerboseLogging { get; set; } = false;

    // 是否已显示过“关闭到托盘”提示
    public bool ClosedToTrayNoticeShown { get; set; } = false;

    // Clipboard
    public bool EnableClipboardHistory { get; set; } = false;
    public int MaxClipboardItems { get; set; } = 500;
    public int ClipboardRetentionDays { get; set; } = 30;
    public int MaxClipboardTextChars { get; set; } = 50_000;
    public long MaxClipboardImageBytes { get; set; } = 20L * 1024 * 1024;

    public bool CaptureTextClipboard { get; set; } = true;
    public bool CaptureFileClipboard { get; set; } = true;
    public bool CaptureImageClipboard { get; set; } = true;
    public bool CaptureHtmlClipboard { get; set; } = true;
    public bool CaptureRichTextClipboard { get; set; } = true;

    public bool IgnoreSensitiveText { get; set; } = true;
    public bool IgnoreSystemAndHiddenFilesInClipboard { get; set; } = true;
    public ClipboardWebDavSyncSettings ClipboardWebDavSync { get; set; } = new();
    public List<string> ClipboardSensitivePatterns { get; set; } = new()
    {
        @"(?i)password\s*[:=]",
        @"(?i)token\s*[:=]",
        @"(?i)secret\s*[:=]",
        @"(?i)api[_-]?key\s*[:=]",
        @"-----BEGIN (RSA |OPENSSH |EC |DSA )?PRIVATE KEY-----",
        @"(?i)Authorization:\s*Bearer\s",
    };
    public List<string> ClipboardExcludedSourceApps { get; set; } = new()
    {
        "1Password.exe", "KeePass.exe", "KeePassXC.exe", "Bitwarden.exe", "LastPass.exe"
    };
    public DateTime? ClipboardPausedUntilUtc { get; set; }

    public string PopPasteHotkey { get; set; } = "Alt+Shift+V";
    public string PopPasteEnterBehavior { get; set; } = "PasteToActiveApp";
    public bool RestoreClipboardAfterPaste { get; set; } = false;
    public int PopPasteMaxRows { get; set; } = 8;
}

public class SystemSourceConfig
{
    public SourceKinds Kind { get; set; }
    public bool Enabled { get; set; } = true;
}

public class OpenWithAppConfig
{
    public string DisplayName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string ArgumentsTemplate { get; set; } = "\"{path}\"";
    public string WorkingDirectoryTemplate { get; set; } = "{folder}";
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}
