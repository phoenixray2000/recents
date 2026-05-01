namespace Recents.App.Services;

// PRD §6.11 图标显示。
// 两段式：
//   1. SHGetFileInfo + SHGFI_USEFILEATTRIBUTES 立即返回扩展名占位图标（不访问磁盘）
//   2. IShellItemImageFactory.GetImage 异步拉真实缩略图
// 缓存 key：扩展名 + isFolder + DPI；写入 %LOCALAPPDATA%\Recents\icons\
// UNC 路径不预拉真实图标，可视进入再异步加载，超时 3s 放弃。
public class FileIconService
{
}
