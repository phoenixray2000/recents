using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Services.Preview;
using Xunit;

namespace Recents.App.Tests;

public sealed class ExternalSpacePreviewServiceTests
{
    [Fact]
    public void RefreshInstallsHookWhenBothPreviewSettingsAreEnabled()
    {
        var settings = CreateSettings(previewEnabled: true, externalEnabled: true);
        var hook = new FakeExternalPreviewKeyboardHook();
        using var service = new ExternalSpacePreviewService(settings, _ => { }, hook);

        service.Refresh();

        Assert.Equal(1, hook.InstallCalls);
        Assert.True(service.IsHookInstalled);
    }

    [Fact]
    public void RefreshDoesNotInstallHookWhenExternalPreviewIsDisabled()
    {
        var settings = CreateSettings(previewEnabled: true, externalEnabled: false);
        var hook = new FakeExternalPreviewKeyboardHook();
        using var service = new ExternalSpacePreviewService(settings, _ => { }, hook);

        service.Refresh();

        Assert.Equal(0, hook.InstallCalls);
        Assert.False(service.IsHookInstalled);
    }

    [Fact]
    public void RefreshCanInstallAfterAnEarlierFailedAttempt()
    {
        var settings = CreateSettings(previewEnabled: true, externalEnabled: true);
        var hook = new FakeExternalPreviewKeyboardHook { FailNextInstall = true };
        using var service = new ExternalSpacePreviewService(settings, _ => { }, hook);

        service.Refresh();
        hook.FailNextInstall = false;
        service.Refresh();

        Assert.Equal(2, hook.InstallCalls);
        Assert.True(service.IsHookInstalled);
    }

    private static SettingsService CreateSettings(bool previewEnabled, bool externalEnabled)
    {
        var settings = new SettingsService();
        settings.Current.PreviewEnabled = previewEnabled;
        settings.Current.ExternalSpacePreviewEnabled = externalEnabled;
        return settings;
    }

    private sealed class FakeExternalPreviewKeyboardHook : IExternalPreviewKeyboardHook
    {
        public bool FailNextInstall { get; set; }
        public int InstallCalls { get; private set; }

        public IntPtr Install(ExternalSpacePreviewKeyboardProc proc)
        {
            InstallCalls++;
            if (FailNextInstall)
                return IntPtr.Zero;

            return new IntPtr(42);
        }

        public void Uninstall(IntPtr hook)
        {
        }

        public IntPtr CallNext(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam) => IntPtr.Zero;

        public bool IsKeyDown(int virtualKey) => false;

        public IntPtr GetForegroundWindow() => IntPtr.Zero;

        public string GetClassName(IntPtr hwnd) => string.Empty;
    }
}
