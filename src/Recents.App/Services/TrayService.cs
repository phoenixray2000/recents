using System.Windows.Forms;
using Application = System.Windows.Application;

namespace Recents.App.Services;

// PRD §6.2 托盘常驻
public class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly MainWindow _mainWindow;
    private readonly RecentIndexService _indexService;

    public TrayService(MainWindow mainWindow, RecentIndexService indexService)
    {
        _mainWindow = mainWindow;
        _indexService = indexService;

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application, // 默认占位图标
            Text = "Recents",
            Visible = true,
            ContextMenuStrip = CreateMenu()
        };

        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _mainWindow.ShowAndFocus();
        };
    }

    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (s, e) => _mainWindow.ShowAndFocus());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Rescan", null, (s, e) => _ = _indexService.RebuildAsync());
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
