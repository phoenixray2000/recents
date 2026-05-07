using Recents.App.Models;

namespace Recents.App.Services.Clipboard;

public sealed class ClipboardDragDropService
{
    private readonly ClipboardActionService _actions;

    public ClipboardDragDropService(ClipboardActionService actions)
    {
        _actions = actions;
    }

    public System.Windows.DataObject CreateDataObject(ClipboardItem item, bool preferBlobFileDrop)
    {
        if (preferBlobFileDrop)
            return _actions.CreateBlobFileDropDataObject(item) ?? _actions.CreateDataObject(item);

        return _actions.CreateDataObject(item);
    }
}
