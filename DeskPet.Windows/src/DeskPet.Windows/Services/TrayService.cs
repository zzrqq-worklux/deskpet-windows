using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DeskPet.Windows.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _icon;
    private readonly bool _ownsIcon;
    private readonly Func<bool> _isPetVisible;
    private readonly Action _showPet;
    private readonly Action _hidePet;

    public TrayService(Func<bool> isPetVisible, Action showPet, Action hidePet, Action showSettings, Action exit)
    {
        _isPetVisible = isPetVisible;
        _showPet = showPet;
        _hidePet = hidePet;

        (_icon, _ownsIcon) = LoadIcon();

        _notifyIcon = new NotifyIcon
        {
            Text = "DeskPet",
            Icon = _icon,
            Visible = true
        };

        _menu = new ContextMenuStrip();
        _menu.Items.Add("显示宠物", null, (_, _) => _showPet());
        _menu.Items.Add("隐藏宠物", null, (_, _) => _hidePet());
        _menu.Items.Add("设置", null, (_, _) => showSettings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => exit());
        _notifyIcon.ContextMenuStrip = _menu;

        _notifyIcon.DoubleClick += OnTrayDoubleClick;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip = null;
        _notifyIcon.DoubleClick -= OnTrayDoubleClick;
        _notifyIcon.Dispose();
        _menu.Dispose();
        if (_ownsIcon)
        {
            _icon.Dispose();
        }
    }

    private void OnTrayDoubleClick(object? sender, EventArgs e)
    {
        if (_isPetVisible())
        {
            _hidePet();
        }
        else
        {
            _showPet();
        }
    }

    private static (Icon Icon, bool OwnsIcon) LoadIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "BoringPet", "home", "1.png");
        if (File.Exists(iconPath))
        {
            try
            {
                using var source = new Bitmap(iconPath);
                using var scaled = new Bitmap(source, new Size(32, 32));
                using var handleIcon = Icon.FromHandle(scaled.GetHicon());
                return ((Icon)handleIcon.Clone(), true);
            }
            catch
            {
                // fall through to the system icon
            }
        }

        return (SystemIcons.Application, false);
    }
}
