using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using Serilog;

namespace Recents.App.Services.Clipboard;

internal static class ClipboardPasteTarget
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint WM_PASTE = 0x0302;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL_KEY = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int SW_RESTORE = 9;
    private const ushort VK_V = 0x56;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly object Gate = new();
    private static readonly WinEventDelegate ForegroundHookProc = OnForegroundChanged;
    private static IntPtr _foregroundHook;
    private static IntPtr _lastExternalForeground;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    public static void StartTracking()
    {
        lock (Gate)
        {
            if (_foregroundHook != IntPtr.Zero)
                return;

            _foregroundHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                ForegroundHookProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            RememberIfExternal(GetForegroundWindow());
            Log.Debug("ClipboardPasteTarget: foreground tracking {Status}", _foregroundHook == IntPtr.Zero ? "failed" : "started");
        }
    }

    public static void StopTracking()
    {
        lock (Gate)
        {
            if (_foregroundHook == IntPtr.Zero)
                return;

            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
    }

    public static IntPtr GetExternalForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (RememberIfExternal(hwnd))
            return hwnd;

        return IsUsableExternalWindow(_lastExternalForeground) ? _lastExternalForeground : IntPtr.Zero;
    }

    public static bool IsUsableExternalWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
            return false;

        GetWindowThreadProcessId(hwnd, out var processId);
        return processId != 0 && processId != Environment.ProcessId;
    }

    public static bool TryPaste(IntPtr targetHwnd, TimeSpan activationTimeout)
    {
        if (targetHwnd == IntPtr.Zero)
            targetHwnd = GetExternalForegroundWindow();

        if (!TryPrepareTarget(targetHwnd, activationTimeout, out var activeTarget))
        {
            var failedForeground = GetForegroundWindow();
            Log.Warning("ClipboardPasteTarget: target activation failed target={Target} targetClass={TargetClass} real={Real} realClass={RealClass} last={Last} lastClass={LastClass}",
                targetHwnd,
                GetWindowClassName(targetHwnd),
                failedForeground,
                GetWindowClassName(failedForeground),
                _lastExternalForeground,
                GetWindowClassName(_lastExternalForeground));
            return false;
        }

        targetHwnd = activeTarget;
        var realForeground = GetForegroundWindow();
        if (realForeground != targetHwnd)
        {
            Log.Warning("ClipboardPasteTarget: paste blocked because foreground changed target={Target} targetClass={TargetClass} real={Real} realClass={RealClass}",
                targetHwnd,
                GetWindowClassName(targetHwnd),
                realForeground,
                GetWindowClassName(realForeground));
            return false;
        }

        if (IsTargetHigherIntegrity(targetHwnd, out var currentIntegrity, out var targetIntegrity))
        {
            Log.Warning("ClipboardPasteTarget: paste blocked by UIPI currentIntegrity={CurrentIntegrity} targetIntegrity={TargetIntegrity} target={Target} class={Class}",
                currentIntegrity,
                targetIntegrity,
                targetHwnd,
                GetWindowClassName(targetHwnd));
            return false;
        }

        if (TryPasteViaOfficeCom(targetHwnd, out var officeRoute))
        {
            Log.Debug("ClipboardPasteTarget: pasted via {Route} target={Target} class={Class}",
                officeRoute,
                targetHwnd,
                GetWindowClassName(targetHwnd));
            return true;
        }

        var recipient = GetPasteRecipient(targetHwnd);
        var className = GetWindowClassName(recipient);
        if (ShouldUsePasteMessage(className) && TrySendPasteMessage(recipient))
        {
            Log.Debug("ClipboardPasteTarget: pasted via WM_PASTE target={Target} recipient={Recipient} class={Class}",
                targetHwnd, recipient, className);
            return true;
        }

        ReleasePressedModifiers();
        Log.Debug("ClipboardPasteTarget: pasted via SendInput target={Target} process={Process} recipient={Recipient} class={Class}",
            targetHwnd, GetWindowProcessName(targetHwnd), recipient, className);
        return SendCtrlV();
    }

    public static bool PasteWithKeystrokeOnly()
    {
        ReleasePressedModifiers();
        var sent = SendRightCtrlV(out var lastError);
        var foreground = GetForegroundWindow();
        Log.Debug("ClipboardPasteTarget: simple RightCtrl+V sent={Sent} lastError={LastError} inputSize={InputSize} foreground={Foreground} class={Class} process={Process}",
            sent,
            lastError,
            Marshal.SizeOf<INPUT>(),
            foreground,
            GetWindowClassName(foreground),
            GetWindowProcessName(foreground));
        return sent;
    }

    private static bool TryPrepareTarget(IntPtr targetHwnd, TimeSpan activationTimeout, out IntPtr activeTarget)
    {
        activeTarget = IntPtr.Zero;
        if (targetHwnd != IntPtr.Zero && !IsUsableExternalWindow(targetHwnd))
            targetHwnd = IntPtr.Zero;

        var initialForeground = GetForegroundWindow();
        RememberIfExternal(initialForeground);
        if (targetHwnd == IntPtr.Zero && IsUsableExternalWindow(initialForeground))
        {
            activeTarget = initialForeground;
            return true;
        }

        if (targetHwnd != IntPtr.Zero && initialForeground == targetHwnd)
        {
            activeTarget = targetHwnd;
            return true;
        }

        if (targetHwnd != IntPtr.Zero)
            ForceForegroundWindow(targetHwnd);

        var deadline = DateTime.UtcNow + activationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var foreground = GetForegroundWindow();
            RememberIfExternal(foreground);

            if (targetHwnd == IntPtr.Zero)
            {
                if (IsUsableExternalWindow(foreground))
                {
                    activeTarget = foreground;
                    return true;
                }
            }
            else if (foreground == targetHwnd)
            {
                activeTarget = targetHwnd;
                return true;
            }

            if (targetHwnd != IntPtr.Zero && IsUsableExternalWindow(targetHwnd))
                ForceForegroundWindow(targetHwnd);

            Thread.Sleep(25);
        }

        var finalForeground = GetForegroundWindow();
        RememberIfExternal(finalForeground);
        if (targetHwnd == IntPtr.Zero)
        {
            if (IsUsableExternalWindow(finalForeground))
            {
                activeTarget = finalForeground;
                return true;
            }
        }
        else if (finalForeground == targetHwnd)
        {
            activeTarget = targetHwnd;
            return true;
        }

        return false;
    }

    private static void ForceForegroundWindow(IntPtr targetHwnd)
    {
        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(targetHwnd, out _);
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);

        var attachedTarget = false;
        var attachedForeground = false;
        try
        {
            if (targetThread != 0 && targetThread != currentThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);

            if (foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread)
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);

            ShowWindowAsync(targetHwnd, SW_RESTORE);
            BringWindowToTop(targetHwnd);
            SetForegroundWindow(targetHwnd);
        }
        finally
        {
            if (attachedForeground)
                AttachThreadInput(currentThread, foregroundThread, false);
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
        }
    }

    private static IntPtr GetPasteRecipient(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero)
            return IntPtr.Zero;

        var targetThread = GetWindowThreadProcessId(targetHwnd, out _);
        if (targetThread == 0)
            return targetHwnd;

        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(targetThread, ref info) && info.hwndFocus != IntPtr.Zero && IsWindow(info.hwndFocus))
            return info.hwndFocus;

        return targetHwnd;
    }

    private static bool TrySendPasteMessage(IntPtr recipient)
    {
        if (recipient == IntPtr.Zero)
            return false;

        return SendMessageTimeout(
            recipient,
            WM_PASTE,
            IntPtr.Zero,
            IntPtr.Zero,
            SMTO_ABORTIFHUNG,
            80,
            out _) != IntPtr.Zero;
    }

    private static bool ShouldUsePasteMessage(string className)
    {
        if (string.IsNullOrWhiteSpace(className))
            return false;

        return className.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
               className.Contains("TextBox", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return string.Empty;

        var builder = new StringBuilder(256);
        return GetClassName(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    private static bool TryPasteViaOfficeCom(IntPtr hwnd, out string route)
    {
        if (TryPasteViaWordCom(hwnd))
        {
            route = "Word COM";
            return true;
        }

        if (TryPasteViaExcelCom(hwnd))
        {
            route = "Excel COM";
            return true;
        }

        route = string.Empty;
        return false;
    }

    private static bool TryPasteViaWordCom(IntPtr hwnd)
    {
        if (!IsTargetProcess(hwnd, "WINWORD"))
            return false;

        object? app = null;
        object? selection = null;
        try
        {
            app = GetRunningComObject("Word.Application");
            if (app is null)
                return false;

            dynamic word = app;
            selection = word.Selection;
            if (selection is null)
                return false;

            dynamic wordSelection = selection;
            wordSelection.Paste();
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ClipboardPasteTarget: Word COM paste failed");
            return false;
        }
        finally
        {
            ReleaseComObject(selection);
            ReleaseComObject(app);
        }
    }

    private static bool TryPasteViaExcelCom(IntPtr hwnd)
    {
        if (!IsTargetProcess(hwnd, "EXCEL"))
            return false;

        object? app = null;
        try
        {
            app = GetRunningComObject("Excel.Application");
            if (app is null)
                return false;

            dynamic excel = app;
            try
            {
                excel.CommandBars.ExecuteMso("Paste");
                return true;
            }
            catch
            {
                // Excel can reject ExecuteMso in edit/protected states; try the sheet paste API.
            }

            try
            {
                excel.ActiveSheet.Paste();
                return true;
            }
            catch
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ClipboardPasteTarget: Excel COM paste failed");
            return false;
        }
        finally
        {
            ReleaseComObject(app);
        }
    }

    private static object? GetRunningComObject(string progId)
    {
        if (CLSIDFromProgID(progId, out var clsid) != 0)
            return null;

        var hr = GetActiveObject(ref clsid, IntPtr.Zero, out var app);
        return hr == 0 ? app : null;
    }

    private static bool IsTargetProcess(IntPtr hwnd, string expectedProcessName)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
            return false;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
            return string.Empty;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;

        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch
        {
        }
    }

    private static bool IsTargetHigherIntegrity(IntPtr hwnd, out string currentIntegrity, out string targetIntegrity)
    {
        currentIntegrity = "unknown";
        targetIntegrity = "unknown";

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
            return false;

        var currentRid = GetProcessIntegrityRid(GetCurrentProcess());
        currentIntegrity = FormatIntegrity(currentRid);

        var targetProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (targetProcess == IntPtr.Zero)
            return false;

        try
        {
            var targetRid = GetProcessIntegrityRid(targetProcess);
            targetIntegrity = FormatIntegrity(targetRid);
            return currentRid.HasValue && targetRid.HasValue && targetRid.Value > currentRid.Value;
        }
        finally
        {
            CloseHandle(targetProcess);
        }
    }

    private static int? GetProcessIntegrityRid(IntPtr processHandle)
    {
        if (!OpenProcessToken(processHandle, TOKEN_QUERY, out var tokenHandle))
            return null;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            GetTokenInformation(tokenHandle, TokenIntegrityLevel, IntPtr.Zero, 0, out var requiredLength);
            if (requiredLength <= 0)
                return null;

            buffer = Marshal.AllocHGlobal(requiredLength);
            if (!GetTokenInformation(tokenHandle, TokenIntegrityLevel, buffer, requiredLength, out _))
                return null;

            var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer);
            var subAuthorityCountPtr = GetSidSubAuthorityCount(label.Label.Sid);
            if (subAuthorityCountPtr == IntPtr.Zero)
                return null;

            var subAuthorityCount = Marshal.ReadByte(subAuthorityCountPtr);
            if (subAuthorityCount == 0)
                return null;

            var ridPtr = GetSidSubAuthority(label.Label.Sid, (uint)(subAuthorityCount - 1));
            return ridPtr == IntPtr.Zero ? null : Marshal.ReadInt32(ridPtr);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
            CloseHandle(tokenHandle);
        }
    }

    private static string FormatIntegrity(int? rid)
    {
        if (!rid.HasValue)
            return "unknown";

        return rid.Value switch
        {
            < 0x2000 => $"low({rid.Value})",
            < 0x3000 => $"medium({rid.Value})",
            < 0x4000 => $"high({rid.Value})",
            _ => $"system({rid.Value})"
        };
    }

    private static void ReleasePressedModifiers()
    {
        var inputs = new List<INPUT>();
        AddKeyUpIfPressed(inputs, VK_SHIFT);
        AddKeyUpIfPressed(inputs, VK_LSHIFT);
        AddKeyUpIfPressed(inputs, VK_RSHIFT);
        AddKeyUpIfPressed(inputs, VK_CONTROL_KEY);
        AddKeyUpIfPressed(inputs, VK_LCONTROL);
        AddKeyUpIfPressed(inputs, VK_RCONTROL);
        AddKeyUpIfPressed(inputs, VK_MENU);
        AddKeyUpIfPressed(inputs, VK_LMENU);
        AddKeyUpIfPressed(inputs, VK_RMENU);
        AddKeyUpIfPressed(inputs, VK_LWIN);
        AddKeyUpIfPressed(inputs, VK_RWIN);
        if (inputs.Count > 0)
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static void AddKeyUpIfPressed(List<INPUT> inputs, int key)
    {
        if ((GetAsyncKeyState(key) & 0x8000) != 0)
            inputs.Add(KeyInput((ushort)key, KEYEVENTF_KEYUP));
    }

    private static void OnForegroundChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        RememberIfExternal(hwnd);
    }

    private static bool RememberIfExternal(IntPtr hwnd)
    {
        if (!IsUsableExternalWindow(hwnd))
            return false;

        _lastExternalForeground = hwnd;
        return true;
    }

    private static bool SendCtrlV() => SendRightCtrlV(out _);

    private static bool SendRightCtrlV(out int lastError) =>
        SendShortcut(new ushort[] { (ushort)VK_RCONTROL }, VK_V, out lastError);

    private static bool SendShortcut(IReadOnlyList<ushort> modifiers, ushort key, out int lastError)
    {
        var inputs = new List<INPUT>(modifiers.Count * 2 + 2);
        foreach (var modifier in modifiers)
            inputs.Add(KeyInput(modifier, 0));

        inputs.Add(KeyInput(key, 0));
        inputs.Add(KeyInput(key, KEYEVENTF_KEYUP));

        for (var i = modifiers.Count - 1; i >= 0; i--)
            inputs.Add(KeyInput(modifiers[i], KEYEVENTF_KEYUP));

        lastError = 0;
        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent != inputs.Count)
            lastError = Marshal.GetLastWin32Error();
        return sent == inputs.Count;
    }

    private static INPUT KeyInput(ushort key, uint flags)
    {
        if (IsExtendedKey(key))
            flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = key,
                    dwFlags = flags
                }
            }
        };
    }

    private static bool IsExtendedKey(ushort key) =>
        key == (ushort)VK_RCONTROL ||
        key == (ushort)VK_RMENU ||
        key == (ushort)VK_LWIN ||
        key == (ushort)VK_RWIN;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
