using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace Recents.App.Services;

// Dynamic status bar text, color, and keyboard hints.
public partial class StatusHintService : ObservableObject
{
    public enum AppStatus
    {
        Ready,
        Initializing,
        Indexing,
        Watching,
        Partial,
        Error
    }

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private SolidColorBrush _statusColor = Brush(0x63, 0xC5, 0x54);

    [ObservableProperty]
    private string _itemCount = "0 items";

    [ObservableProperty]
    private string _keyboardHint = "Up/Down Navigate";

    public void SetStatus(AppStatus status)
    {
        switch (status)
        {
            case AppStatus.Ready:
                StatusText = "Ready";
                StatusColor = Brush(0x63, 0xC5, 0x54); // Success Green
                break;
            case AppStatus.Initializing:
                StatusText = "Initializing...";
                StatusColor = Brush(0x60, 0xCD, 0xFF); // Info Blue
                break;
            case AppStatus.Indexing:
                StatusText = "Indexing...";
                StatusColor = Brush(0xF5, 0xB6, 0x42); // Warning Orange
                break;
            case AppStatus.Watching:
                StatusText = "Watching sources";
                StatusColor = Brush(0x63, 0xC5, 0x54); // Success Green
                break;
            case AppStatus.Partial:
                StatusText = "Some sources unavailable";
                StatusColor = Brush(0xF5, 0xB6, 0x42); // Warning Orange
                break;
            case AppStatus.Error:
                StatusText = "Error";
                StatusColor = Brush(0xE1, 0x5B, 0x64); // Danger Red
                break;
        }
    }

    public void UpdateCount(int count)
    {
        ItemCount = $"{count} items";
    }

    public void UpdateHint(bool hasSelection, bool canDrag)
    {
        if (!hasSelection)
            KeyboardHint = "Up/Down Navigate";
        else if (canDrag)
            KeyboardHint = "Enter Open | Drag to share";
        else
            KeyboardHint = "Enter Open";
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
