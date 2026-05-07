using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using Recents.App.Localization;

namespace Recents.App.Services;

// Dynamic status bar text, color, and keyboard hints.
public partial class StatusHintService : ObservableObject
{
    public enum HintMode
    {
        None,
        File,
        Clipboard
    }

    public enum AppStatus
    {
        Ready,
        Initializing,
        Indexing,
        Watching,
        ClipboardCapturing,
        PopPasteActive,
        Partial,
        Error
    }

    private AppStatus _currentStatus = AppStatus.Ready;
    private int _currentCount = 0;
    private HintMode _hintMode = HintMode.None;
    private bool _hintCanDrag = false;

    [ObservableProperty]
    private string _statusText = Loc.T("Status_Ready");

    [ObservableProperty]
    private SolidColorBrush _statusColor = Brush(0x63, 0xC5, 0x54);

    [ObservableProperty]
    private string _itemCount = Loc.T("Status_ItemCount", 0);

    [ObservableProperty]
    private string _keyboardHint = Loc.T("Status_Hint_Navigate");

    public StatusHintService()
    {
        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            // Re-render localized strings on language switch.
            SetStatus(_currentStatus);
            UpdateCount(_currentCount);
            UpdateHint(_hintMode, _hintCanDrag);
        };
    }

    public void SetStatus(AppStatus status)
    {
        _currentStatus = status;
        switch (status)
        {
            case AppStatus.Ready:
                StatusText = Loc.T("Status_Ready");
                StatusColor = Brush(0x63, 0xC5, 0x54);
                break;
            case AppStatus.Initializing:
                StatusText = Loc.T("Status_Initializing");
                StatusColor = Brush(0x60, 0xCD, 0xFF);
                break;
            case AppStatus.Indexing:
                StatusText = Loc.T("Status_Indexing");
                StatusColor = Brush(0xF5, 0xB6, 0x42);
                break;
            case AppStatus.Watching:
                StatusText = Loc.T("Status_Watching");
                StatusColor = Brush(0x63, 0xC5, 0x54);
                break;
            case AppStatus.ClipboardCapturing:
                StatusText = Loc.T("Status_ClipboardCapturing");
                StatusColor = Brush(0x60, 0xCD, 0xFF);
                break;
            case AppStatus.PopPasteActive:
                StatusText = Loc.T("Status_PopPasteActive");
                StatusColor = Brush(0x60, 0xCD, 0xFF);
                break;
            case AppStatus.Partial:
                StatusText = Loc.T("Status_Partial");
                StatusColor = Brush(0xF5, 0xB6, 0x42);
                break;
            case AppStatus.Error:
                StatusText = Loc.T("Status_Error");
                StatusColor = Brush(0xE1, 0x5B, 0x64);
                break;
        }
    }

    public void UpdateCount(int count)
    {
        _currentCount = count;
        ItemCount = Loc.T("Status_ItemCount", count);
    }

    public void UpdateHint(HintMode mode, bool canDrag)
    {
        _hintMode = mode;
        _hintCanDrag = canDrag;
        KeyboardHint = mode switch
        {
            HintMode.File => canDrag ? Loc.T("Status_Hint_FileDrag") : Loc.T("Status_Hint_File"),
            HintMode.Clipboard => canDrag ? Loc.T("Status_Hint_ClipboardDrag") : Loc.T("Status_Hint_Clipboard"),
            _ => Loc.T("Status_Hint_Navigate")
        };
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
