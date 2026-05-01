namespace Recents.App.Services;

// PRD §6.9 / §6.10 文件操作。
// 双击打开：ProcessStartInfo { UseShellExecute = true }
// 打开方式：优先 SHOpenWithDialog，失败回退 rundll32 shell32.dll,OpenAs_RunDLL
// 打开所在位置：explorer.exe /select,"path"
// 复制路径 / 复制文件名：通过剪贴板。
// 删除 Recent 记录：仅删除 %APPDATA%\Microsoft\Windows\Recent 下的对应 .lnk；不动其他来源。
public class FileActionService
{
}
