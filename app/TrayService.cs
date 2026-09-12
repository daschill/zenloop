using System.Drawing;
using System.Windows.Forms;

namespace ZenLoop.App;

sealed class TrayService : IDisposable
{
    readonly NotifyIcon _icon;
    readonly MainWindow _window;

    public TrayService(MainWindow window)
    {
        _window = window;
        _icon = new NotifyIcon
        {
            Text = "ZenLoop",
            Icon = SystemIcons.Application,
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Apply saved profiles", null, (_, _) => _window.ApplySavedProfilesFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _window.ForceClose());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowWindow();
    }

    public void ShowWindow()
    {
        _window.Show();
        _window.WindowState = System.Windows.WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Non-modal balloon for silent startup update notices.</summary>
    public void ShowBalloon(string title, string text, int timeoutMs = 6000)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text.Length > 240 ? text[..240] : text;
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(timeoutMs);
        }
        catch
        {
            /* tray balloons are best-effort */
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
