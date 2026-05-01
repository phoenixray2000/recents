using System.IO;
using ShellLink;

namespace Recents.App.Services;

// PRD §6.4 .lnk 解析器
// 使用 Securify.ShellLink 纯托管解析，避免 COM 的 STA 限制，可并发。
public static class ShellLinkResolver
{
    public record ResolveResult(
        string TargetPath,
        string WorkingDir,
        string Arguments,
        DateTime LastWriteTime
    );

    // 解析 .lnk 文件，若失败或目标为非真实路径（如特殊 GUID 路径）则返回 null
    public static ResolveResult? Resolve(string lnkPath)
    {
        try
        {
            var bytes    = File.ReadAllBytes(lnkPath);
            var shortcut = ShellLink.Shortcut.FromByteArray(bytes);
            var lwt      = File.GetLastWriteTime(lnkPath);

            var target = string.Empty;

            // 优先尝试读取本地路径
            if (shortcut.LinkInfo?.LocalBasePath != null)
            {
                target = shortcut.LinkInfo.LocalBasePath;
                if (!string.IsNullOrEmpty(shortcut.LinkInfo.CommonPathSuffix))
                {
                    target = Path.Combine(target, shortcut.LinkInfo.CommonPathSuffix);
                }
            }
            // 如果是 UNC 路径，尝试从 NetworkBasePath 读取
            else if (shortcut.LinkInfo?.CommonNetworkRelativeLink?.DeviceName != null)
            {
                target = shortcut.LinkInfo.CommonNetworkRelativeLink.DeviceName;
                if (!string.IsNullOrEmpty(shortcut.LinkInfo.CommonPathSuffix))
                {
                    target = Path.Combine(target, shortcut.LinkInfo.CommonPathSuffix);
                }
            }
            // 兜底尝试相对路径
            else if (shortcut.StringData?.RelativePath != null)
            {
                target = shortcut.StringData.RelativePath;
            }

            if (string.IsNullOrWhiteSpace(target))
                return null;

            return new ResolveResult(
                TargetPath: target,
                WorkingDir: shortcut.StringData?.WorkingDir ?? string.Empty,
                Arguments:  shortcut.StringData?.CommandLineArguments ?? string.Empty,
                LastWriteTime: lwt
            );
        }
        catch
        {
            return null;
        }
    }
}
