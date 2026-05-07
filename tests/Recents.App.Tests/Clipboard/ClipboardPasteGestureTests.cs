using System.Windows.Input;
using Recents.App.Services.Clipboard;
using Xunit;

namespace Recents.App.Tests.Clipboard;

public sealed class ClipboardPasteGestureTests
{
    [Theory]
    [InlineData(ModifierKeys.None, false)]
    [InlineData(ModifierKeys.Alt, false)]
    [InlineData(ModifierKeys.Shift, false)]
    [InlineData(ModifierKeys.Control, true)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, true)]
    public void ShouldPastePlainTextOnClick_OnlyRequiresControl(ModifierKeys modifiers, bool expected)
    {
        Assert.Equal(expected, ClipboardPasteGesture.ShouldPastePlainTextOnClick(modifiers));
    }

    [Theory]
    [InlineData(ClipboardPasteGesture.VK_CONTROL)]
    [InlineData(ClipboardPasteGesture.VK_LCONTROL)]
    [InlineData(ClipboardPasteGesture.VK_RCONTROL)]
    [InlineData(ClipboardPasteGesture.VK_MENU)]
    [InlineData(ClipboardPasteGesture.VK_LMENU)]
    [InlineData(ClipboardPasteGesture.VK_RMENU)]
    [InlineData(ClipboardPasteGesture.VK_LWIN)]
    [InlineData(ClipboardPasteGesture.VK_RWIN)]
    public void IsPassthroughModifierVirtualKey_IncludesLeftAndRightModifiers(int virtualKey)
    {
        Assert.True(ClipboardPasteGesture.IsPassthroughModifierVirtualKey(virtualKey));
    }

    [Theory]
    [InlineData(0x41)]
    [InlineData(0x0D)]
    [InlineData(0x20)]
    public void IsPassthroughModifierVirtualKey_ExcludesNonModifiers(int virtualKey)
    {
        Assert.False(ClipboardPasteGesture.IsPassthroughModifierVirtualKey(virtualKey));
    }
}
