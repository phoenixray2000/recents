using System.IO;
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

    // 显示名称（直接透传，后续支持省略中段长路径）
    public string DisplayName  => Item.DisplayName;
    public string DisplayPath  => Item.NormalizedPath;
    public string DisplayPathShort => MiddleEllipsize(Item.NormalizedPath);
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

    private ImageSource? _icon;
    private bool _iconLoaded;
    private ImageSource? _smallIcon;
    private bool _smallIconLoaded;

    // 真实图标（SHGetFileInfo）- 异步加载防止 UI 阻塞 (Bug-6)
    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                Task.Run(() => 
                {
                    // P1: 尝试获取缩略图 (PRD §6.12)
                    var thumb = ShellService.GetThumbnail(Item.NormalizedPath, 256, 256);
                    if (thumb != null) return thumb;
                    
                    return FileIconService.GetIcon(Item.NormalizedPath, Item.IsFolder, true);
                })
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        _icon = t.Result;
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(Icon)));
                    }
                }, TaskScheduler.Default);
            }
            return _icon;
        }
    }

    public ImageSource? SmallIcon
    {
        get
        {
            if (!_smallIconLoaded)
            {
                _smallIconLoaded = true;
                Task.Run(() => 
                {
                    // P1: 小图优先使用系统图标以保证清晰度，或使用小尺寸缩略图
                    var thumb = ShellService.GetThumbnail(Item.NormalizedPath, 48, 48);
                    if (thumb != null) return thumb;

                    return FileIconService.GetIcon(Item.NormalizedPath, Item.IsFolder, false);
                })
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        _smallIcon = t.Result;
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SmallIcon)));
                    }
                }, TaskScheduler.Default);
            }
            return _smallIcon;
        }
    }

    // 来源标签（Tooltip 用）
    public string SourcesLabel => BuildSourcesLabel(Item.Sources);

    private readonly RecentIndexService _indexService;
    private readonly ExistsProbeService? _probeService;
    public RecentItem Item { get; }

    public RecentItemViewModel(RecentItem item, RecentIndexService indexService, ExistsProbeService? probeService = null)
    {
        Item = item;
        _indexService = indexService;
        _probeService = probeService;

        if (Item.Exists == ExistsState.Unknown && _probeService != null)
        {
            _ = Task.Run(async () =>
            {
                await _probeService.ProbeAsync(Item);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (Item.Exists == ExistsState.Missing)
                    {
                        _ = _indexService.RemoveAsync(Item.NormalizedPath);
                    }
                    else
                    {
                        Refresh();
                    }
                });
            });
        }
    }

    public string OpenDisabledReason => IsMissing ? "File does not exist or is inaccessible." : string.Empty;

    [RelayCommand(CanExecute = nameof(CanActionExecute))]
    private void Open() => FileActionService.OpenFile(Item.NormalizedPath);

    [RelayCommand(CanExecute = nameof(CanActionExecute))]
    private void Reveal() => FileActionService.RevealInExplorer(Item.NormalizedPath);

    [RelayCommand(CanExecute = nameof(CanActionExecute))]
    private void OpenWith() => ShellService.ShowOpenWithDialog(Item.NormalizedPath);

    private bool CanActionExecute() => !IsMissing;

    [RelayCommand]
    private async Task TogglePin()
    {
        Item.IsFavorite = !Item.IsFavorite;
        await _indexService.UpdateFavoriteAsync(Item.NormalizedPath, Item.IsFavorite);
    }

    [RelayCommand]
    private async Task HideFromList()
    {
        await _indexService.HideItemAsync(Item.NormalizedPath);
    }

    [RelayCommand]
    private void CopyPath() => FileActionService.CopyPath(Item.NormalizedPath);

    [RelayCommand]
    private void CopyFileName() => FileActionService.CopyFileName(Item.NormalizedPath);

    [RelayCommand]
    private async Task RemoveOnce()
    {
        // PRD §6.10.2: Remove Once
        await _indexService.RemoveAsync(Item.NormalizedPath);
        
        // Also try to delete from system recent folder if it's from there
        var recentDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent");
        // This is a simplified implementation; a full one would find the specific .lnk
    }

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

    private static string MiddleEllipsize(string path)
    {
        const int maxLength = 64;
        if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
            return path;

        var fileName = System.IO.Path.GetFileName(path);
        var root = System.IO.Path.GetPathRoot(path) ?? string.Empty;
        if (string.IsNullOrEmpty(fileName) || root.Length + fileName.Length + 4 > maxLength)
        {
            var headLength = Math.Min(18, path.Length / 2);
            var tailLength = Math.Max(12, maxLength - headLength - 3);
            return $"{path[..headLength]}...{path[^tailLength..]}";
        }

        var availableHead = maxLength - fileName.Length - 5;
        var head = path[..Math.Min(path.Length, Math.Max(root.Length, availableHead))].TrimEnd('\\', '/');
        return $"{head}\\...\\{fileName}";
    }

    private static string BuildSourcesLabel(SourceKinds sources)
    {
        var parts = new List<string>();
        if (sources.HasFlag(SourceKinds.KnownFolderWatch)) parts.Add("KnownFolder");
        if (sources.HasFlag(SourceKinds.UserFolderWatch))  parts.Add("UserFolder");
        if (sources.HasFlag(SourceKinds.UncFolderWatch))   parts.Add("UNC");
        if (sources.HasFlag(SourceKinds.RecentLnk))        parts.Add("RecentLnk");
        if (sources.HasFlag(SourceKinds.OfficeMru))        parts.Add("OfficeMRU");
        if (sources.HasFlag(SourceKinds.OpenSavePidlMru))  parts.Add("OpenSave");
        return string.Join(", ", parts);
    }
}
