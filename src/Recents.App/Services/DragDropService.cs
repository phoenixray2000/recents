namespace Recents.App.Services;

// PRD §6.8 拖拽。
// DataObject 同时附 DataFormats.FileDrop (string[]) 与 Shell IDList Array (CFSTR_SHELLIDLIST)，
// 兼容微信 / 飞书 / Outlook / Edge / Explorer 五个验收目标。
// Missing 文件禁拖；UNC 文件可拖。
public class DragDropService
{
}
