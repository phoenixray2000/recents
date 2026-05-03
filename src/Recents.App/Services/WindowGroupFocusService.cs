using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Recents.App.Views;
using WpfApplication = System.Windows.Application;
using WpfPoint = System.Windows.Point;

namespace Recents.App.Services;

public sealed class WindowGroupFocusService : IWindowGroupFocusService
{
    private readonly HashSet<Window> _registeredWindows = new();
    private readonly object _gate = new();

    public bool IsInteractingWithRecentDockWindowGroup { get; set; }

    static WindowGroupFocusService()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnContextMenuOpened));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler(OnContextMenuClosed));
    }

    private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (WpfApplication.Current is App app && App.WindowGroupFocusService is WindowGroupFocusService service)
        {
            service.IsInteractingWithRecentDockWindowGroup = true;
        }
    }

    private static async void OnContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (WpfApplication.Current is App app && App.WindowGroupFocusService is WindowGroupFocusService service)
        {
            await Task.Delay(150);
            service.IsInteractingWithRecentDockWindowGroup = false;
        }
    }

    public bool IsRecentDockWindow(Window window)
    {
        return window is IRecentDockWindow;
    }

    public void RegisterWindow(Window window)
    {
        if (!IsRecentDockWindow(window))
            return;

        lock (_gate)
        {
            _registeredWindows.Add(window);
        }

        window.Closed += (_, _) => UnregisterWindow(window);
    }

    public void UnregisterWindow(Window window)
    {
        lock (_gate)
        {
            _registeredWindows.Remove(window);
        }
    }

    public void CancelPendingHide()
    {
    }

    public bool IsAnyRecentDockWindowActive()
    {
        return WpfApplication.Current.Windows
            .OfType<Window>()
            .Any(w => IsRecentDockWindow(w) && w.IsVisible && w.IsActive);
    }

    public bool IsMouseOverAnyRecentDockWindow()
    {
        foreach (Window window in WpfApplication.Current.Windows)
        {
            if (!IsRecentDockWindow(window) || !window.IsVisible)
                continue;

            if (IsMouseInsideWindow(window))
                return true;
        }

        return false;
    }

    public async Task<bool> ShouldHideAfterDeactivatedAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (IsInteractingWithRecentDockWindowGroup)
            return false;

        if (IsAnyRecentDockWindowActive())
            return false;

        if (IsMouseOverAnyRecentDockWindow())
            return false;

        return true;
    }

    private static bool IsMouseInsideWindow(Window window)
    {
        if (!window.IsVisible)
            return false;

        try
        {
            WpfPoint position = Mouse.GetPosition(window);
            return position.X >= 0
                && position.Y >= 0
                && position.X <= window.ActualWidth
                && position.Y <= window.ActualHeight;
        }
        catch
        {
            return false;
        }
    }
}
