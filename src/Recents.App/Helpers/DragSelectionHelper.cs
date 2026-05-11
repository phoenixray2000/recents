namespace Recents.App.Helpers;

internal static class DragSelectionHelper
{
    public static IReadOnlyList<T>? CaptureSnapshot<T>(IEnumerable<T> selectedItems, T? dragSource)
        where T : class
    {
        if (dragSource is null)
            return null;

        var snapshot = selectedItems.ToList();
        return snapshot.Count > 1 && snapshot.Contains(dragSource)
            ? snapshot
            : null;
    }

    public static IReadOnlyList<T> ResolveDragItems<T>(
        IEnumerable<T> currentSelectedItems,
        IReadOnlyList<T>? selectionSnapshot)
        where T : class
    {
        var current = currentSelectedItems.ToList();
        if (current.Count != 1 || selectionSnapshot is null || selectionSnapshot.Count <= current.Count)
            return current;

        return selectionSnapshot.Contains(current[0])
            ? selectionSnapshot
            : current;
    }
}
