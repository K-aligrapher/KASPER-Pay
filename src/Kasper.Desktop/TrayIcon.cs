using System.IO;
using Forms = System.Windows.Forms;

namespace Kasper.Desktop;

/// <summary>The bunny in the notification area: open, notifications and quit.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private string? _lastRoute;

    public TrayIcon(Action<string?> open, Action quit)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "kasper.ico");
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open KASPER", null, (_, _) => open(null));
        menu.Items.Add("Approvals", null, (_, _) => open("approvals"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());

        _icon = new Forms.NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath, 32, 32) : System.Drawing.SystemIcons.Application,
            Text = "KASPER",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(null); };
        _icon.BalloonTipClicked += (_, _) => open(_lastRoute);
    }

    /// <param name="route">View to open when the notification is clicked.</param>
    public void Notify(string title, string message, string? route = null)
    {
        _lastRoute = route;
        _icon.ShowBalloonTip(5000, title, message.Length > 200 ? message[..200] + "…" : message, Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
