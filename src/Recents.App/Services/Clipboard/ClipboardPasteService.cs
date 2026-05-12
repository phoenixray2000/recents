using Recents.App.Models;
using Recents.App.Localization;
using Recents.App.ViewModels;
using Recents.App.Views;
using System.Diagnostics;
using Serilog;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;

namespace Recents.App.Services.Clipboard;

public sealed class ClipboardPasteService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private const int SW_HIDE = 0;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_BACK = 0x08;
    private const int VK_TAB = 0x09;
    private const int VK_RETURN = 0x0D;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int VK_PRIOR = 0x21;
    private const int VK_NEXT = 0x22;
    private const int VK_END = 0x23;
    private const int VK_HOME = 0x24;
    private const int VK_LEFT = 0x25;
    private const int VK_UP = 0x26;
    private const int VK_RIGHT = 0x27;
    private const int VK_DOWN = 0x28;
    private const int VK_DELETE = 0x2E;
    private readonly SettingsService _settings;
    private readonly ClipboardStoreService _store;
    private readonly ClipboardActionService _actions;
    private readonly IWindowGroupFocusService _windowGroupFocusService;
    private readonly StatusHintService? _status;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private ClipboardPopupWindow? _window;
    private IntPtr _keyboardHook;
    private IntPtr _targetHwnd;
    private bool _acceptingFromHook;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    public ClipboardPasteService(
        SettingsService settings,
        ClipboardStoreService store,
        ClipboardActionService actions,
        IWindowGroupFocusService windowGroupFocusService,
        StatusHintService? status = null)
    {
        _settings = settings;
        _store = store;
        _actions = actions;
        _windowGroupFocusService = windowGroupFocusService;
        _status = status;
        _keyboardProc = KeyboardHookCallback;
    }

    public void ShowPopup()
    {
        if (_window?.IsVisible == true)
        {
            _window.Topmost = false;
            _window.Topmost = true;
            return;
        }

        _targetHwnd = _actions.CapturePasteTargetFromForeground();
        var viewModel = new ClipboardPopupViewModel(_store, _settings.Current.PopPasteMaxRows);
        _window = new ClipboardPopupWindow(viewModel, this);
        _status?.SetStatus(StatusHintService.AppStatus.PopPasteActive);
        _window.Closed += (_, _) =>
        {
            if (_window is not null)
                _windowGroupFocusService.UnregisterWindow(_window);
            _window = null;
            UninstallKeyboardHook();
            _status?.SetStatus(StatusHintService.AppStatus.Watching);
        };
        _windowGroupFocusService.RegisterWindow(_window);
        InstallKeyboardHook();
        _window.Show();
    }

    public async Task AcceptAsync(ClipboardItemViewModel? item, bool pastePlainText = false)
    {
        if (item is null)
            return;

        if (pastePlainText)
        {
            await _actions.PastePlainTextToActiveAppAsync(
                item.Item,
                () =>
                {
                    UninstallKeyboardHook();
                    HidePopupImmediately();
                },
                respectPopPasteEnterBehavior: true);
            return;
        }

        await _actions.PasteToActiveAppAsync(
            item.Item,
            () =>
            {
                UninstallKeyboardHook();
                HidePopupImmediately();
            },
            respectPopPasteEnterBehavior: true);
    }

    public bool ShouldPastePlainTextOnClick()
    {
        return ClipboardPasteGesture.ShouldPastePlainTextOnClick();
    }

    public bool IsPreviewEnabled => _settings.Current.PreviewEnabled;

    private void HidePopupImmediately()
    {
        if (_window is null)
            return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, SW_HIDE);
        else
            _window.Hide();
    }

    public void Dispose()
    {
        _window?.Close();
        _window = null;
        UninstallKeyboardHook();
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
            return;

        var moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
        var moduleHandle = string.IsNullOrWhiteSpace(moduleName) ? IntPtr.Zero : GetModuleHandle(moduleName);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
        Log.Debug("ClipboardPasteService: keyboard hook {Status}", _keyboardHook == IntPtr.Zero ? "failed" : "installed");
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHook == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode < 0 || _window?.IsVisible != true)
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            var message = wParam.ToInt32();
            if (message is not (WM_KEYDOWN or WM_SYSKEYDOWN))
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (HandlePopupKey((int)info.vkCode, info.scanCode))
                return new IntPtr(1);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClipboardPasteService: keyboard hook callback failed");
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private bool HandlePopupKey(int vkCode, uint scanCode)
    {
        if (_window is null)
            return false;

        if (ClipboardPasteGesture.IsPassthroughModifierVirtualKey(vkCode))
            return false;

        switch (vkCode)
        {
            case VK_ESCAPE:
                PostWindowAction(window => window.Close());
                return true;
            case VK_RETURN:
                if (_acceptingFromHook)
                    return true;
                _acceptingFromHook = true;
                try
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
                    {
                        try
                        {
                            var window = _window;
                            if (window is not null)
                            {
                                await window.AcceptSelectedAsync();
                                window.Close();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "ClipboardPasteService: accept from keyboard hook failed");
                        }
                        finally
                        {
                            _acceptingFromHook = false;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "ClipboardPasteService: failed to schedule accept from hook");
                    _acceptingFromHook = false;
                }
                return true;
            case VK_UP:
                PostWindowAction(window => window.MoveSelection(-1));
                return true;
            case VK_DOWN:
                PostWindowAction(window => window.MoveSelection(1));
                return true;
            case VK_PRIOR:
            case VK_HOME:
                PostWindowAction(window => window.MoveSelection(-1000));
                return true;
            case VK_NEXT:
            case VK_END:
                PostWindowAction(window => window.MoveSelection(1000));
                return true;
            case VK_BACK:
                PostWindowAction(window => window.BackspaceSearchText());
                return true;
            case VK_DELETE:
                PostWindowAction(window => window.ClearSearchText());
                return true;
            case VK_SPACE:
                if (Keyboard.Modifiers == ModifierKeys.None)
                    PostWindowAction(window => window.HandleSpaceKey());
                return true;
            case VK_LEFT:
            case VK_RIGHT:
            case VK_TAB:
                return true;
        }

        var text = TryTranslateText(vkCode, scanCode);
        if (!string.IsNullOrEmpty(text))
        {
            PostWindowAction(window => window.AppendSearchText(text));
            return true;
        }

        return true;
    }

    private void PostWindowAction(Action<ClipboardPopupWindow> action)
    {
        try
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var window = _window;
                    if (window is null)
                        return;

                    action(window);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "ClipboardPasteService: keyboard hook action failed");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ClipboardPasteService: failed to schedule keyboard hook action");
        }
    }

    private static string TryTranslateText(int vkCode, uint scanCode)
    {
        var keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState))
            return vkCode == VK_SPACE ? " " : string.Empty;

        var buffer = new StringBuilder(8);
        var result = ToUnicode((uint)vkCode, scanCode, keyboardState, buffer, buffer.Capacity, 0);
        if (result > 0)
            return buffer.ToString()[..result];

        return vkCode == VK_SPACE ? " " : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}
