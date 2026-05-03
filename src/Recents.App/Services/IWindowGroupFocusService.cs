using System.Windows;

namespace Recents.App.Services;

public interface IWindowGroupFocusService
{
    bool IsInteractingWithRecentDockWindowGroup { get; set; }

    bool IsRecentDockWindow(Window window);

    bool IsAnyRecentDockWindowActive();

    bool IsMouseOverAnyRecentDockWindow();

    void RegisterWindow(Window window);

    void UnregisterWindow(Window window);

    void CancelPendingHide();

    Task<bool> ShouldHideAfterDeactivatedAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default);
}
