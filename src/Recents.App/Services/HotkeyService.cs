using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace Recents.App.Services;

// PRD §6.1 全局热键服务
// 用 P/Invoke RegisterHotKey / UnregisterHotKey 注册全局快捷键，
// 注册失败时按候选链回退：Alt+Shift+Z → Win+; → Ctrl+Shift+Space → Ctrl+Alt+Space。
// 全部失败时记 Error 日志，触发 RegistrationFailed 事件（供 TrayService 显示气泡）。
public sealed partial class HotkeyService : ObservableObject, IDisposable
{
    public enum HotkeyAction
    {
        Toggle = 0x5265,
        PopPaste = 0x5266
    }

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
    private const int ToggleHotkeyId  = (int)HotkeyAction.Toggle;
    private const int PopPasteHotkeyId = (int)HotkeyAction.PopPaste;

    #endregion

    // 候选热键链（PRD §6.1）
    private static readonly (uint Mod, uint Vk, string Label)[] Candidates =
    {
        (MOD_ALT     | MOD_SHIFT | MOD_NOREPEAT, 0x5A,         "Alt+Shift+Z"), // VK_Z = 0x5A
        (MOD_WIN     | MOD_NOREPEAT,              VK_SEMICOLON, "Win+;"),
        (MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_SPACE,     "Ctrl+Shift+Space"),
    };

    private HwndSource?         _hwndSource;
    private IntPtr              _hwnd;
    private (uint Mod, uint Vk, string Label) _active;
    private readonly Dictionary<int, string> _registeredLabels = new();

    public void UpdateHotkey(string hotkeyString)
    {
        Unregister(ToggleHotkeyId);
        if (TryRegisterExact(ToggleHotkeyId, hotkeyString, "main"))
            ActiveLabel = hotkeyString;
        else
            TryRegisterCandidates();
    }

    public void UpdatePopPasteHotkey(string hotkeyString)
    {
        Unregister(PopPasteHotkeyId);
        if (!TryRegisterExact(PopPasteHotkeyId, hotkeyString, "Pop Paste"))
            RegistrationFailed?.Invoke($"Pop Paste hotkey {hotkeyString} could not be registered. Please choose another hotkey.");
    }

    private static bool TryParseHotkey(string hotkey, out uint mod, out uint vk)
    {
        mod = 0;
        vk = 0;
        if (string.IsNullOrEmpty(hotkey)) return false;

        var parts = hotkey.Split('+');
        foreach (var part in parts.Take(parts.Length - 1))
        {
            switch (part.Trim().ToUpperInvariant())
            {
                case "CTRL": mod |= MOD_CONTROL; break;
                case "ALT": mod |= MOD_ALT; break;
                case "SHIFT": mod |= MOD_SHIFT; break;
                case "WIN": mod |= MOD_WIN; break;
            }
        }

        var keyStr = parts.Last().Trim().ToUpperInvariant();
        if (keyStr.Length == 1)
        {
            vk = (uint)keyStr[0];
            return true;
        }
        
        // Basic mapping for common non-char keys
        switch (keyStr)
        {
            case "SPACE": vk = VK_SPACE; return true;
            case "SEMICOLON": vk = VK_SEMICOLON; return true;
            // Add more if needed, but for P0 this is enough
        }

        return false;
    }

    [ObservableProperty]
    private string _activeLabel = "(Not registered)";

    // 热键触发时通知（UI 层订阅）
    public event Action<HotkeyAction>? HotkeyPressed;

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
            if (RegisterHotKey(_hwnd, ToggleHotkeyId, candidate.Mod, candidate.Vk))
            {
                _active     = candidate;
                _registeredLabels[ToggleHotkeyId] = candidate.Label;
                ActiveLabel = candidate.Label;
                OnPropertyChanged(nameof(ActiveLabel)); // Bug-7 Fix
                Log.Information("HotkeyService: 热键注册成功 → {Label}", candidate.Label);
                return;
            }
            Log.Warning("HotkeyService: 热键 {Label} 注册失败（Win32={Err}）",
                candidate.Label, Marshal.GetLastWin32Error());
        }

        // 全部候选都失败
        var msg = "全局热键注册失败，所有候选组合均被占用。请在设置中手动更改热键。";
        ActiveLabel = "(Not registered)";
        Log.Error("HotkeyService: {Msg}", msg);
        RegistrationFailed?.Invoke(msg);
    }

    private bool TryRegisterExact(int id, string hotkeyString, string scope)
    {
        if (_hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(hotkeyString))
            return false;

        if (!TryParseHotkey(hotkeyString, out var mod, out var vk))
        {
            Log.Warning("HotkeyService: {Scope} hotkey parse failed → {Label}", scope, hotkeyString);
            return false;
        }

        if (RegisterHotKey(_hwnd, id, mod | MOD_NOREPEAT, vk))
        {
            _registeredLabels[id] = hotkeyString;
            Log.Information("HotkeyService: {Scope} hotkey registered → {Label}", scope, hotkeyString);
            return true;
        }

        Log.Warning("HotkeyService: {Scope} hotkey {Label} failed Win32={Err}",
            scope, hotkeyString, Marshal.GetLastWin32Error());
        return false;
    }

    private void Unregister(int id)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!_registeredLabels.ContainsKey(id)) return;
        UnregisterHotKey(_hwnd, id);
        _registeredLabels.Remove(id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registeredLabels.ContainsKey(wParam.ToInt32()))
        {
            HotkeyPressed?.Invoke((HotkeyAction)wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            foreach (var id in _registeredLabels.Keys.ToArray())
                UnregisterHotKey(_hwnd, id);
            _registeredLabels.Clear();
        }
        _hwndSource?.RemoveHook(WndProc);
    }
}
