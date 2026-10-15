namespace WindowsAICompanion.Desktop;

internal sealed class TrayIconService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _contextMenu;

    public TrayIconService(
        Action openAssistant,
        Action toggleAssistant,
        Action showSettings,
        Action exitApplication)
    {
        _contextMenu = new System.Windows.Forms.ContextMenuStrip();
        _contextMenu.Items.Add("Open Assistant", null, (_, _) => openAssistant());
        _contextMenu.Items.Add("Show / Hide Assistant", null, (_, _) => toggleAssistant());
        _contextMenu.Items.Add("Settings", null, (_, _) => showSettings());
        _contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _contextMenu.Items.Add("Exit", null, (_, _) => exitApplication());

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Windows AI Companion",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => openAssistant();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}