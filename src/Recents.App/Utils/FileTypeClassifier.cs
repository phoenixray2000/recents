namespace Recents.App.Utils;

// PRD §6.7 / §16 #13 文件类型分类工具
public static class FileTypeClassifier
{
    public static string Classify(string extension, bool isFolder, IReadOnlyDictionary<string, List<string>> groups)
    {
        if (isFolder) return ""; // 文件夹不展示分类名

        if (string.IsNullOrEmpty(extension)) return "Other";

        var ext = extension.ToLowerInvariant();
        if (!ext.StartsWith('.')) ext = "." + ext;

        foreach (var group in groups)
        {
            if (group.Value.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                return group.Key;
            }
        }

        return "Other";
    }
}
