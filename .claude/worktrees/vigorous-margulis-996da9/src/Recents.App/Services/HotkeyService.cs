using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace Recents.App.Services;

// PRD §6.1 全局热键服务
// 用 P/Invoke RegisterHotKey / UnregisterHotKey 注册全局快捷键，
// 注册失败时按候选链回退：Ctrl+Alt+R → Win+; → Ctrl+Shift+Space → Ctrl+Alt+Space。
// 全部失败时记 Error 日志，触发 RegistrationFailed 事件（供 TrayService 显示气泡）。
public sealed class HotkeyService : IDisposable
{
    #region Win32 P/Invoke

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // fsModifiers 标志（可组合）
    private const uint MOD_ALT      = 0x0001;
    private const uint MOD_CONTROL  = 0x0002;
    private const uint MOD_SHIFT    = 0x0004;
    private const uint MOD_WIN      = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    // 虚拟键码
    private const uint VK_R         = 0x52;
    private const uint VK_SEMICOLON = 0xBA; // Win+;
    private const uint VK_SPACE     = 0x20;

    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId  = 0x5265;   // 'Re' 前两字节

    #endregion

    // 候选热键链（PRD §6.1）
    private static readonly (uint Mod, uint Vk, string Label)[] Candidates =
    {
        (MOD_CONTROL | MOD_ALT   | MOD_NOREPEAT, VK_R,         "Ctrl+Alt+R"),
        (MOD_WIN     | MOD_NOREPEAT,              VK_SEMICOLON, "Win+;"),
        (MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_SPACE,     "Ctrl+Shift+Space"),
        (MOD_CONTROL | MOD_ALT   | MOD_NOREPEAT, VK_SPACE,     "Ctrl+Alt+Space"),
    };

    private HwndSource?         _hwndSource;
    private IntPtr              _hwnd;
    private bool                _registered;
    private (uint Mod, uint Vk, string Label) _active;

    // 热键触发时通知（UI 层订阅）
    public event Action? HotkeyPressed;

    // 注册失败（全部候选耗尽）时通知，传入提示文本
    public event Action<string>? RegistrationFailed;

    // 初始化：传入 MainWindow 的窗口句柄，订阅 WM_HOTKEY 消息。
    // 必须在 MainWindow.Loaded 或 SourceInitialized 之后调用（此时 HWND 已创建）。
    public void Initialize(Window window)
    {
        _hwnd       = new WindowInteropHelper(window).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        TryRegisterCandidates();
    }

    private void TryRegisterCandidates()
    {
        foreach (var candidate in Candidates)
        {
            if (RegisterHotKey(_hwnd, HotkeyId, candidate.Mod, candidate.Vk))
            {
                _active     = candidate;
                _registered = true;
                Log.Information("HotkeyService: 热键注册成功 → {Label}", candidate.Label);
                return;
            }
            Log.Warning("HotkeyService: 热键 {Label} 注册失败（Win32={Err}）",
                candidate.Label, Marshal.GetLastWin32Error());
        }

        // 全部候选都失败
        var msg = "全局热键注册失败，所有候选组合均被占用。请在设置中手动更改热键。";
        Log.Error("HotkeyService: {Msg}", msg);
        RegistrationFailed?.Invoke(msg);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // 返回当前已成功注册的热键标签（供 UI 展示）
    public string ActiveLabel => _registered ? _active.Label : "（未注册）";

    public void Dispose()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
        _hwndSource?.RemoveHook(WndProc);
    }
}
