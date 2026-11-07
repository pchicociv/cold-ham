using ColdHam.Core;
using Forms = System.Windows.Forms;

namespace ColdHam.App;

internal sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _state;
    private bool _disposed;

    public TrayIconService(MainWindow window)
    {
        _window = window;
        _state = new Forms.ToolStripMenuItem { Enabled = false };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(LocalizedText.Get("OpenColdHam"), null, (_, _) => _window.ShowFromTray());
        menu.Items.Add(_state);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(LocalizedText.Get("EndSession"), null, async (_, _) => await _window.EndSessionFromTrayAsync());
        menu.Items.Add(LocalizedText.Get("StopAll"), null, async (_, _) => await _window.StopAllFromTrayAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(LocalizedText.Get("Exit"), null, async (_, _) => await _window.RequestExitFromTrayAsync());

        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Cold Ham",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) _window.ShowFromTray();
        };
        _window.BlockStateChanged += Update;
        _window.NotificationRaised += ShowNotification;
        Update(this, EventArgs.Empty);
    }

    private void ShowNotification(object? sender, TrayNotificationEventArgs notification)
    {
        if (!_disposed) _icon.ShowBalloonTip(4000, notification.Title, notification.Message, Forms.ToolTipIcon.Info);
    }

    private void Update(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var status = _window.TrayStatus.Replace(Environment.NewLine, " ", StringComparison.Ordinal);
        _state.Text = status;
        _icon.Text = status.Length > 63 ? status[..63] : status;
        var items = _icon.ContextMenuStrip!.Items;
        items[0].Text = LocalizedText.Get("OpenColdHam");
        items[3].Text = LocalizedText.Get("EndSession");
        items[4].Text = LocalizedText.Get("StopAll");
        items[6].Text = LocalizedText.Get("Exit");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.BlockStateChanged -= Update;
        _window.NotificationRaised -= ShowNotification;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
