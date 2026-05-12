using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Serilog;

namespace Recents.App.Services.Preview;

public sealed class ExternalPreviewSelectionService
{
    public string? TryGetSelectedPath(IntPtr hwnd, ExternalPreviewTargetKind targetKind)
    {
        return targetKind switch
        {
            ExternalPreviewTargetKind.Explorer => TryGetExplorerSelectedPath(hwnd) ?? TryGetUiAutomationPath(hwnd),
            ExternalPreviewTargetKind.Desktop => TryGetUiAutomationPath(hwnd, GetDesktopDirectories()),
            ExternalPreviewTargetKind.FileDialog => TryGetUiAutomationPath(hwnd),
            _ => null,
        };
    }

    public ExternalPreviewFocusedControlKind GetFocusedControlKind()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
                return ExternalPreviewFocusedControlKind.Other;

            var controlType = focused.Current.ControlType;
            if (controlType == ControlType.Edit)
                return ExternalPreviewFocusedControlKind.TextInput;

            return ExternalPreviewFocusedControlKind.Other;
        }
        catch
        {
            return ExternalPreviewFocusedControlKind.Other;
        }
    }

    private static string? TryGetExplorerSelectedPath(IntPtr hwnd)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic window in shell.Windows())
            {
                var windowHwnd = new IntPtr((int)window.HWND);
                if (windowHwnd != hwnd)
                    continue;

                dynamic selectedItems = window.Document.SelectedItems();
                if ((int)selectedItems.Count <= 0)
                    return null;

                var path = (string?)selectedItems.Item(0).Path;
                return IsPreviewablePath(path) ? path : null;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "External preview: Explorer selected path lookup failed");
        }

        return null;
    }

    private static string? TryGetUiAutomationPath(IntPtr hwnd, IEnumerable<string>? fallbackDirectories = null)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null)
                return null;

            var selected = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(SelectionItemPattern.IsSelectedProperty, true));

            return TryGetPreviewablePath(selected, root, fallbackDirectories) ??
                   TryGetPreviewablePath(AutomationElement.FocusedElement, root, fallbackDirectories);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "External preview: UI Automation selected path lookup failed");
            return null;
        }
    }

    private static string? TryGetPreviewablePath(
        AutomationElement? element,
        AutomationElement root,
        IEnumerable<string>? fallbackDirectories)
    {
        if (element is null)
            return null;

        var values = ReadCandidateValues(element).ToList();
        foreach (var candidate in values)
        {
            if (IsPreviewablePath(candidate))
                return candidate;
        }

        var directories = EnumerateCandidateDirectories(root)
            .Concat(fallbackDirectories ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var candidate in values)
        {
            var resolved = ResolveNameAgainstDirectories(candidate, directories);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    internal static string? ResolveNameAgainstDirectories(string? name, IEnumerable<string> directories)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathFullyQualified(name))
            return null;

        var fileName = name.Trim();
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            var path = Path.Combine(directory, fileName);
            if (File.Exists(path) || Directory.Exists(path))
                return path;
        }

        return null;
    }

    internal static IEnumerable<string> ExtractPathFragments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        foreach (Match match in Regex.Matches(value, @"[A-Za-z]:\\[^<>:""|?*\r\n]*"))
        {
            var path = match.Value.Trim().TrimEnd('\\');
            if (Directory.Exists(path))
                yield return path;
        }

        foreach (Match match in Regex.Matches(value, @"\\\\[^<>:""|?*\r\n]+"))
        {
            var path = match.Value.Trim().TrimEnd('\\');
            if (Directory.Exists(path))
                yield return path;
        }
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(AutomationElement root)
    {
        var candidates = new List<string>();

        foreach (var value in ReadCandidateValues(root))
            candidates.AddRange(ExtractPathFragments(value));

        AutomationElementCollection descendants;
        try
        {
            descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        }
        catch
        {
            return candidates;
        }

        foreach (AutomationElement element in descendants)
        {
            foreach (var value in ReadCandidateValues(element))
                candidates.AddRange(ExtractPathFragments(value));
        }

        return candidates;
    }

    private static IEnumerable<string> GetDesktopDirectories()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static IEnumerable<string?> ReadCandidateValues(AutomationElement element)
    {
        yield return element.Current.Name;
        yield return element.Current.HelpText;

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern) &&
            valuePattern is ValuePattern value)
        {
            yield return value.Current.Value;
        }

    }

    private static bool IsPreviewablePath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               Path.IsPathFullyQualified(path) &&
               (File.Exists(path) || Directory.Exists(path));
    }
}

internal delegate IntPtr ExternalSpacePreviewKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

internal interface IExternalPreviewKeyboardHook
{
    IntPtr Install(ExternalSpacePreviewKeyboardProc proc);
    void Uninstall(IntPtr hook);
    IntPtr CallNext(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    bool IsKeyDown(int virtualKey);
    IntPtr GetForegroundWindow();
    string GetClassName(IntPtr hwnd);
}

public sealed class ExternalSpacePreviewService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int VkSpace = 0x20;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;

    private readonly SettingsService _settings;
    private readonly Action<string> _previewPath;
    private readonly IExternalPreviewKeyboardHook _keyboardHook;
    private readonly ExternalPreviewSelectionService _selectionService = new();
    private readonly ExternalSpacePreviewKeyboardProc _proc;
    private IntPtr _hookId;
    private bool _disposed;

    public ExternalSpacePreviewService(SettingsService settings, Action<string> previewPath)
        : this(settings, previewPath, new Win32ExternalPreviewKeyboardHook())
    {
    }

    internal ExternalSpacePreviewService(
        SettingsService settings,
        Action<string> previewPath,
        IExternalPreviewKeyboardHook keyboardHook)
    {
        _settings = settings;
        _previewPath = previewPath;
        _keyboardHook = keyboardHook;
        _proc = HookCallback;
    }

    internal bool IsHookInstalled => _hookId != IntPtr.Zero;

    public void Refresh()
    {
        if (_disposed)
            return;

        Log.Debug(
            "External preview: refresh preview={PreviewEnabled} external={ExternalEnabled} installed={Installed}",
            _settings.Current.PreviewEnabled,
            _settings.Current.ExternalSpacePreviewEnabled,
            IsHookInstalled);

        if (_settings.Current.PreviewEnabled && _settings.Current.ExternalSpacePreviewEnabled)
            Start();
        else
            Stop();
    }

    private void Start()
    {
        if (_hookId != IntPtr.Zero)
            return;

        _hookId = _keyboardHook.Install(_proc);
        if (_hookId == IntPtr.Zero)
        {
            Log.Warning("External preview: failed to install keyboard hook, Win32={Err}", Marshal.GetLastWin32Error());
            return;
        }

        Log.Information("External preview: keyboard hook installed");
    }

    private void Stop()
    {
        if (_hookId == IntPtr.Zero)
            return;

        _keyboardHook.Uninstall(_hookId);
        _hookId = IntPtr.Zero;
        Log.Information("External preview: keyboard hook stopped");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode < 0 || wParam.ToInt32() != WmKeydown)
                return _keyboardHook.CallNext(_hookId, nCode, wParam, lParam);

            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (info.vkCode != VkSpace || HasModifierDown())
                return _keyboardHook.CallNext(_hookId, nCode, wParam, lParam);

            var hwnd = _keyboardHook.GetForegroundWindow();
            var className = _keyboardHook.GetClassName(hwnd);
            var targetKind = ExternalPreviewWindowClassifier.Classify(className);
            var focusedControlKind = _selectionService.GetFocusedControlKind();

            if (!ExternalPreviewWindowClassifier.ShouldHandleSpace(
                    _settings.Current.PreviewEnabled,
                    _settings.Current.ExternalSpacePreviewEnabled,
                    targetKind,
                    focusedControlKind))
            {
                return _keyboardHook.CallNext(_hookId, nCode, wParam, lParam);
            }

            DispatchPreviewPath(hwnd, targetKind);
            return new IntPtr(1);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "External preview: keyboard hook callback failed");
            return _keyboardHook.CallNext(_hookId, nCode, wParam, lParam);
        }
    }

    private void DispatchPreviewPath(IntPtr hwnd, ExternalPreviewTargetKind targetKind)
    {
        try
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_disposed)
                        return;

                    var path = _selectionService.TryGetSelectedPath(hwnd, targetKind);
                    if (!string.IsNullOrWhiteSpace(path))
                        _previewPath(path);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "External preview: preview dispatch failed");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "External preview: failed to schedule preview dispatch");
        }
    }

    private bool HasModifierDown() =>
        IsKeyDown(VkShift) || IsKeyDown(VkControl) || IsKeyDown(VkMenu) ||
        IsKeyDown(VkLwin) || IsKeyDown(VkRwin);

    private bool IsKeyDown(int virtualKey) => _keyboardHook.IsKeyDown(virtualKey);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    private sealed class Win32ExternalPreviewKeyboardHook : IExternalPreviewKeyboardHook
    {
        public IntPtr Install(ExternalSpacePreviewKeyboardProc proc) =>
            SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(null), 0);

        public void Uninstall(IntPtr hook) => UnhookWindowsHookEx(hook);

        public IntPtr CallNext(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam) =>
            CallNextHookEx(hook, nCode, wParam, lParam);

        public bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

        public IntPtr GetForegroundWindow() => Win32GetForegroundWindow();

        public string GetClassName(IntPtr hwnd)
        {
            var builder = new StringBuilder(256);
            return Win32GetClassName(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook,
            ExternalSpacePreviewKeyboardProc lpfn,
            IntPtr hMod,
            uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr Win32GetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "GetClassName", CharSet = CharSet.Unicode)]
        private static extern int Win32GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    }
}
