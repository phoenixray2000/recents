using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace Recents.App.Services;

// PRD §5.9 动态状态栏提示
public partial class StatusHintService : ObservableObject
{
    public enum AppStatus
    {
        Ready,
        Indexing,
        Watching,
        Partial,
        Error
    }

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private SolidColorBrush _statusColor = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0xC4, 0xFF)); // AccentBlue

    [ObservableProperty]
    private string _itemCount = "0 items";

    [ObservableProperty]
    private string _keyboardHint = "↑↓ Navigate";

    public void SetStatus(AppStatus status)
    {
        switch (status)
        {
            case AppStatus.Ready:
                StatusText = "Ready";
                StatusColor = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0xC4, 0xFF));
                break;
            case AppStatus.Indexing:
                StatusText = "Indexing...";
                StatusColor = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD6, 0x00)); // Yellow
                break;
            case AppStatus.Watching:
                StatusText = "Watching";
                StatusColor = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xE6, 0x76)); // Green
                break;
            case AppStatus.Error:
                StatusText = "Error";
                StatusColor = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x52, 0x52)); // Red
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
            KeyboardHint = "↑↓ Navigate";
        else if (canDrag)
            KeyboardHint = "Enter Open · Drag to share";
        else
            KeyboardHint = "Enter Open";
    }
}
