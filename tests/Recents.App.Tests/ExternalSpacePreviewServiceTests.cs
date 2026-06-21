using Recents.App.Models;
using Recents.App.Services;
using Recents.App.Services.Preview;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

    [Fact]
    public async Task SpacePreviewSelectionRunsAsynchronously()
    {
        var settings = CreateSettings(previewEnabled: true, externalEnabled: true);
        var hook = new FakeExternalPreviewKeyboardHook
        {
            ForegroundWindow = new IntPtr(1234),
            ClassName = "CabinetWClass"
        };
        var selection = new BlockingExternalPreviewSelectionService("C:\\demo.txt");
        using var service = new ExternalSpacePreviewService(
            settings,
            _ => { },
            hook,
            selection,
            TimeSpan.FromSeconds(5));
        service.Refresh();

        var elapsed = Stopwatch.StartNew();
        hook.PressSpace();
        elapsed.Stop();

        Assert.True(await selection.Started.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(250));
        Assert.Equal(1, selection.LookupCalls);

        selection.Release();
    }

    [Fact]
    public async Task SpacePreviewIgnoresSecondLookupWhileFirstLookupIsRunning()
    {
        var settings = CreateSettings(previewEnabled: true, externalEnabled: true);
        var hook = new FakeExternalPreviewKeyboardHook
        {
            ForegroundWindow = new IntPtr(1234),
            ClassName = "CabinetWClass"
        };
        var selection = new BlockingExternalPreviewSelectionService("C:\\demo.txt");
        using var service = new ExternalSpacePreviewService(
            settings,
            _ => { },
            hook,
            selection,
            TimeSpan.FromSeconds(5));
        service.Refresh();

        hook.PressSpace();
        Assert.True(await selection.Started.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        hook.PressSpace();

        Assert.Equal(1, selection.LookupCalls);

        selection.Release();
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
        private ExternalSpacePreviewKeyboardProc? _proc;

        public bool FailNextInstall { get; set; }
        public int InstallCalls { get; private set; }
        public IntPtr ForegroundWindow { get; set; }
        public string ClassName { get; set; } = string.Empty;

        public IntPtr Install(ExternalSpacePreviewKeyboardProc proc)
        {
            InstallCalls++;
            if (FailNextInstall)
                return IntPtr.Zero;

            _proc = proc;
            return new IntPtr(42);
        }

        public void Uninstall(IntPtr hook)
        {
        }

        public IntPtr CallNext(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam) => IntPtr.Zero;

        public bool IsKeyDown(int virtualKey) => false;

        public IntPtr GetForegroundWindow() => ForegroundWindow;

        public string GetClassName(IntPtr hwnd) => ClassName;

        public void PressSpace()
        {
            if (_proc is null)
                throw new InvalidOperationException("Hook has not been installed.");

            var info = Marshal.AllocHGlobal(40);
            try
            {
                Marshal.WriteInt32(info, 0x20);
                _proc(0, new IntPtr(0x0100), info);
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }
    }

    private sealed class BlockingExternalPreviewSelectionService : IExternalPreviewSelectionService
    {
        private readonly string? _path;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingExternalPreviewSelectionService(string? path)
        {
            _path = path;
        }

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LookupCalls { get; private set; }

        public ExternalPreviewFocusedControlKind GetFocusedControlKind() => ExternalPreviewFocusedControlKind.Other;

        public string? TryGetSelectedPath(IntPtr hwnd, ExternalPreviewTargetKind targetKind)
        {
            LookupCalls++;
            Started.TrySetResult(true);
            _release.Task.GetAwaiter().GetResult();
            return _path;
        }

        public void Release() => _release.TrySetResult();
    }
}
