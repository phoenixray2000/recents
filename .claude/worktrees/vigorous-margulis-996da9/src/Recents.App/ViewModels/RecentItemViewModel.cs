using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Media;
using Recents.App.Models;
using Recents.App.Services;

namespace Recents.App.ViewModels;

// PRD §9 单条 RecentItem 的 VM 包装。
// 负责：Exists 三态展示、来源 Tooltip、图标占位（真实图标在 P1 实现）。
public partial class RecentItemViewModel : ObservableObject
{
    private readonly RecentIndexService _indexService;
    public RecentItem Item { get; }

    // 显示名称（直接透传，后续支持省略中段长路径）
    public string DisplayName  => Item.DisplayName;
    public string DisplayPath  => Item.NormalizedPath;
    public string Extension            => Item.Extension;
    public string ClassificationSource => Item.ClassificationSource;
    // PRD §6.7：类型徽章仅当分类非 Other 且非文件夹时显示
    public System.Windows.Visibility BadgeVisibility =>
        (!Item.IsFolder && Item.ClassificationSource != "Other" && !string.IsNullOrEmpty(Item.ClassificationSource))
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    // 时间格式化（本地时间）
    public string RecentTimeDisplay =>
        Item.RecentTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    // 大小格式化
    public string SizeDisplay => Item.SizeBytes.HasValue
        ? FormatBytes(Item.SizeBytes.Value)
        : string.Empty;

    // 存在状态
    public bool   IsMissing  => Item.Exists == ExistsState.Missing;
    public bool   IsUnknown  => Item.Exists == ExistsState.Unknown;
    public bool   IsFolder   => Item.IsFolder;

    // 真实图标（SHGetFileInfo）
    public ImageSource? Icon => FileIconService.GetIcon(Item.NormalizedPath, Item.IsFolder, true);
    public ImageSource? SmallIcon => FileIconService.GetIcon(Item.NormalizedPath, Item.IsFolder, false);

    // 来源标签（Tooltip 用）
    public string SourcesLabel => BuildSourcesLabel(Item.Sources);

    public RecentItemViewModel(RecentItem item, RecentIndexService indexService)
    {
        Item = item;
        _indexService = indexService;
    }

    [RelayCommand]
    private void Open() => FileActionService.OpenFile(Item.NormalizedPath);

    [RelayCommand]
    private void Reveal() => FileActionService.RevealInExplorer(Item.NormalizedPath);

    [RelayCommand]
    private async Task TogglePin()
    {
        Item.IsFavorite = !Item.IsFavorite;
        await _indexService.UpdateFavoriteAsync(Item.NormalizedPath, Item.IsFavorite);
    }

    [RelayCommand]
    private void CopyPath() => FileActionService.CopyPath(Item.NormalizedPath);

    [RelayCommand]
    private void CopyFileName() => FileActionService.CopyFileName(Item.NormalizedPath);

    // 当底层 Item 字段变化后，通知 UI 刷新所有属性
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);  // 通知所有属性
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024              => $"{bytes} B",
        < 1024 * 1024       => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _                   => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    private static string BuildSourcesLabel(SourceKinds sources)
    {
        var parts = new List<string>();
        if (sources.HasFlag(SourceKinds.KnownFolderWatch)) parts.Add("KnownFolder");
        if (sources.HasFlag(SourceKinds.UserFolderWatch))  parts.Add("UserFolder");
        if (sources.HasFlag(SourceKinds.UncFolderWatch))   parts.Add("UNC");
        if (sources.HasFlag(SourceKinds.RecentLnk))        parts.Add("RecentLnk");
        if (sources.HasFlag(SourceKinds.JumpListAuto))     parts.Add("JumpList");
        if (sources.HasFlag(SourceKinds.OfficeMru))        parts.Add("OfficeMRU");
        if (sources.HasFlag(SourceKinds.OpenSavePidlMru))  parts.Add("OpenSave");
        return string.Join(", ", parts);
    }
}
