using System.Windows.Input;
using System.Runtime.InteropServices;

namespace Recents.App.Services.Clipboard;

internal static class ClipboardPasteGesture
{
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12;
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;
    internal const int VK_LCONTROL = 0xA2;
    internal const int VK_RCONTROL = 0xA3;
    internal const int VK_LMENU = 0xA4;
    internal const int VK_RMENU = 0xA5;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool ShouldPastePlainTextOnDoubleClick() =>
        IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);

    public static bool ShouldPastePlainTextOnDoubleClick(ModifierKeys modifiers) =>
        (modifiers & ModifierKeys.Control) != 0;

    public static bool IsPassthroughModifierVirtualKey(int virtualKey) =>
        virtualKey is VK_CONTROL or VK_LCONTROL or VK_RCONTROL
            or VK_MENU or VK_LMENU or VK_RMENU
            or VK_LWIN or VK_RWIN;

    private static bool IsKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
