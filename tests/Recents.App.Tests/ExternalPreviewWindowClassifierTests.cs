using Recents.App.Services.Preview;
using Xunit;

namespace Recents.App.Tests;

public sealed class ExternalPreviewWindowClassifierTests
{
    [Theory]
    [InlineData("CabinetWClass", ExternalPreviewTargetKind.Explorer)]
    [InlineData("ExploreWClass", ExternalPreviewTargetKind.Explorer)]
    [InlineData("Progman", ExternalPreviewTargetKind.Desktop)]
    [InlineData("WorkerW", ExternalPreviewTargetKind.Desktop)]
    [InlineData("#32770", ExternalPreviewTargetKind.FileDialog)]
    public void ClassifyRecognizesSupportedShellWindows(string className, ExternalPreviewTargetKind expected)
    {
        Assert.Equal(expected, ExternalPreviewWindowClassifier.Classify(className));
    }

    [Fact]
    public void ShouldHandleSpaceRequiresBothSettingsEnabled()
    {
        Assert.False(ExternalPreviewWindowClassifier.ShouldHandleSpace(
            previewEnabled: true,
            externalSpacePreviewEnabled: false,
            targetKind: ExternalPreviewTargetKind.Explorer,
            focusedControlKind: ExternalPreviewFocusedControlKind.Other));

        Assert.False(ExternalPreviewWindowClassifier.ShouldHandleSpace(
            previewEnabled: false,
            externalSpacePreviewEnabled: true,
            targetKind: ExternalPreviewTargetKind.Explorer,
            focusedControlKind: ExternalPreviewFocusedControlKind.Other));
    }

    [Fact]
    public void ShouldHandleSpaceRejectsTextInputFocus()
    {
        Assert.False(ExternalPreviewWindowClassifier.ShouldHandleSpace(
            previewEnabled: true,
            externalSpacePreviewEnabled: true,
            targetKind: ExternalPreviewTargetKind.FileDialog,
            focusedControlKind: ExternalPreviewFocusedControlKind.TextInput));
    }

    [Fact]
    public void ShouldHandleSpaceAcceptsDocumentFocus()
    {
        Assert.True(ExternalPreviewWindowClassifier.ShouldHandleSpace(
            previewEnabled: true,
            externalSpacePreviewEnabled: true,
            targetKind: ExternalPreviewTargetKind.FileDialog,
            focusedControlKind: ExternalPreviewFocusedControlKind.Other));
    }

    [Fact]
    public void ShouldHandleSpaceAcceptsSupportedNonTextFocus()
    {
        Assert.True(ExternalPreviewWindowClassifier.ShouldHandleSpace(
            previewEnabled: true,
            externalSpacePreviewEnabled: true,
            targetKind: ExternalPreviewTargetKind.Explorer,
            focusedControlKind: ExternalPreviewFocusedControlKind.Other));
    }
}
