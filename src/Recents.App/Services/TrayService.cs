using System.Windows.Forms;
using Application = System.Windows.Application;

namespace Recents.App.Services;

// PRD §6.2 托盘常驻
public class TrayService : IDisposable
{
    private NotifyIcon _notifyIcon = null!;
    private MainWindow? _mainWindow;
    private readonly Func<Task> _rescanAsync;

    public TrayService(Func<Task> rescanAsync)
    {
        _rescanAsync = rescanAsync;

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "Recents",
            Visible = true,
            ContextMenuStrip = CreateMenu()
        };

        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _mainWindow?.ShowAndFocus();
        };
    }

    public void SetMainWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
    }

    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (s, e) => _mainWindow?.ShowAndFocus());
        menu.Items.Add("Settings", null, (s, e) =>
        {
            _mainWindow?.ShowAndFocus();
            _mainWindow?.OpenSettings();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Rescan", null, (s, e) => _ = _rescanAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) =>
        {
            _notifyIcon.Visible = false;
            Application.Current.Shutdown();
        });
        return menu;
    }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, title, text, icon);
    }

    public void Dispose()
    {
        _notifyIcon.Dispose();
    }
}
