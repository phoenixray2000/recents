namespace Recents.App.Services;

// PRD §6.21 开机自启。
// 写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Recents = "<exe path> --minimized"
// 当前用户级别，不需要管理员权限。
public class StartupService
{
}
