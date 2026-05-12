using System.Windows;
using Recents.App;
using Recents.App.Services.Preview;
using Xunit;

namespace Recents.App.Tests;

public sealed class PreviewWindowTests
{
    [Theory]
    [InlineData(false, false, WindowState.Maximized, true)]
    [InlineData(true, false, WindowState.Maximized, false)]
    [InlineData(false, true, WindowState.Maximized, false)]
    [InlineData(false, false, WindowState.Normal, false)]
    public void NeedsNormalStateBeforeInactiveShowOnlyForHiddenMaximizedInactiveWindows(
        bool isVisible,
        bool showActivated,
        WindowState windowState,
        bool expected)
    {
        Assert.Equal(expected, PreviewWindow.NeedsNormalStateBeforeInactiveShow(
            isVisible,
            showActivated,
            windowState));
    }

    [Fact]
    public void PreviewWindowUsesWindowChromeToOwnTheNonClientFrame()
    {
        var xaml = File.ReadAllText(FindRepoFile("src", "Recents.App", "PreviewWindow.xaml"));

        Assert.Contains("xmlns:shell=\"clr-namespace:System.Windows.Shell;assembly=PresentationFramework\"", xaml);
        Assert.Contains("<shell:WindowChrome.WindowChrome>", xaml);
        Assert.Contains("GlassFrameThickness=\"0\"", xaml);
        Assert.Contains("CaptionHeight=\"0\"", xaml);
    }

    [Fact]
    public void AppStartupRefreshesExternalPreviewHookSynchronously()
    {
        var source = File.ReadAllText(FindRepoFile("src", "Recents.App", "App.xaml.cs"));

        Assert.Contains("mainWindow.RefreshExternalSpacePreview();", source);
    }

    [Fact]
    public void AppStartupDoesNotSchedulePreviewPrewarmBeforeMainWindowIsVisible()
    {
        var source = File.ReadAllText(FindRepoFile("src", "Recents.App", "App.xaml.cs"));

        Assert.DoesNotContain("mainWindow.PrewarmPreview()", source);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void PreviewPrewarmIsScheduledOnlyOnceAfterVisibleWindow(
        bool previewEnabled,
        bool prewarmStarted,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldSchedulePreviewPrewarmAfterWindowShown(
            previewEnabled,
            prewarmStarted));
    }

    [Theory]
    [InlineData(PreviewKind.ShellHandler, false)]
    [InlineData(PreviewKind.Audio, false)]
    [InlineData(PreviewKind.Video, false)]
    [InlineData(PreviewKind.Text, true)]
    [InlineData(PreviewKind.Markdown, true)]
    [InlineData(PreviewKind.Pdf, true)]
    [InlineData(PreviewKind.Image, true)]
    public void NativePreviewKindsDoNotWaitForWebView2BeforeRendering(
        PreviewKind kind,
        bool expected)
    {
        Assert.Equal(expected, PreviewWindow.RequiresWebView2BeforeRendering(kind));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, false)]
    public void PreviewOwnerIsAssignedOnlyBeforeShowingFromVisibleMainWindow(
        bool mainVisible,
        bool previewVisible,
        bool alreadyOwnedByMain,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldAssignPreviewOwner(
            mainVisible,
            previewVisible,
            alreadyOwnedByMain));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void InactiveNonTopmostPreviewUsesTopmostPulseToRiseAboveOwner(
        bool showActivated,
        bool topmost,
        bool expected)
    {
        Assert.Equal(expected, PreviewWindow.ShouldPulseTopmostAfterInactiveShow(
            showActivated,
            topmost));
    }

    [Theory]
    [InlineData(true, null, null, true)]
    [InlineData(true, @"C:\Temp\a.txt", null, false)]
    [InlineData(true, null, @"C:\Temp\a.txt", false)]
    [InlineData(false, null, null, false)]
    public void CanHideHiddenPrewarmOnlyWhenNoPreviewRequestIsPending(
        bool startedHidden,
        string? currentPath,
        string? pendingPath,
        bool expected)
    {
        Assert.Equal(expected, PreviewWindow.CanHideHiddenPrewarm(
            startedHidden,
            currentPath,
            pendingPath,
            hasCurrentClipboardItem: false,
            hasPendingClipboardItem: false));
    }

    private static string FindRepoFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate repo file.", Path.Combine(relativeParts));
    }
}
