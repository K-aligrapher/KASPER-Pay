using System.IO;
using Forms = System.Windows.Forms;

namespace Kasper.Desktop;

/// <summary>The bunny in the notification area: open, notifications and quit.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action open, Action quit)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "kasper.ico");
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open KASPER", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());

        _icon = new Forms.NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath, 32, 32) : System.Drawing.SystemIcons.Application,
            Text = "KASPER",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => open();
    }

    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
