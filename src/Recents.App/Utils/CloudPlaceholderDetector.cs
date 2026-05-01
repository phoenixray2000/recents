using System.IO;

namespace Recents.App.Utils;

// PRD §6.19 占位符保护：检测 FILE_ATTRIBUTE_RECALL_ON_OPEN 等标志
public static class CloudPlaceholderDetector
{
    private const FileAttributes OfflineAttributes = 
        FileAttributes.Offline |
        (FileAttributes)0x00040000 | // FILE_ATTRIBUTE_RECALL_ON_OPEN
        (FileAttributes)0x00400000;  // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS

    // 检查是否为云盘占位符。若返回 true，调用方不应读取 Length 或缩略图。
    public static bool IsPlaceholder(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & OfflineAttributes) != 0;
        }
        catch
        {
            return false;
        }
    }
}
