namespace Recents.App.Services.Preview;

public enum ExternalPreviewTargetKind
{
    None,
    Explorer,
    Desktop,
    FileDialog,
}

public enum ExternalPreviewFocusedControlKind
{
    Other,
    TextInput,
}

public static class ExternalPreviewWindowClassifier
{
    public static ExternalPreviewTargetKind Classify(string? windowClassName) =>
        windowClassName switch
        {
            "CabinetWClass" or "ExploreWClass" => ExternalPreviewTargetKind.Explorer,
            "Progman" or "WorkerW" => ExternalPreviewTargetKind.Desktop,
            "#32770" => ExternalPreviewTargetKind.FileDialog,
            _ => ExternalPreviewTargetKind.None,
        };

    public static bool ShouldHandleSpace(
        bool previewEnabled,
        bool externalSpacePreviewEnabled,
        ExternalPreviewTargetKind targetKind,
        ExternalPreviewFocusedControlKind focusedControlKind)
    {
        return previewEnabled &&
               externalSpacePreviewEnabled &&
               targetKind != ExternalPreviewTargetKind.None &&
               focusedControlKind != ExternalPreviewFocusedControlKind.TextInput;
    }
}
