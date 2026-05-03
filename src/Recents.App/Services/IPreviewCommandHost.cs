namespace Recents.App.Services;

public interface IPreviewCommandHost
{
    void ClosePreview();

    void SelectNextAndRefreshPreview();

    void SelectPreviousAndRefreshPreview();

    void OpenSelectedItem();

    void CopySelectedItemPath();
}
