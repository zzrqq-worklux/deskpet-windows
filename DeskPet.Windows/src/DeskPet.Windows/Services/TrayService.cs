using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DeskPet.Windows.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _homeLeaveItem;
    private readonly Icon _icon;
    private readonly bool _ownsIcon;
    private readonly Action _showPet;
    private readonly Action _hidePet;
    private readonly Action _goHome;
    private readonly Action _leaveHome;
    private bool _isAtHome;

    public TrayService(Action showPet, Action hidePet, Action goHome, Action leaveHome, Action showSettings, Action exit)
    {
        _showPet = showPet;
        _hidePet = hidePet;
        _goHome = goHome;
        _leaveHome = leaveHome;

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
        _homeLeaveItem = new ToolStripMenuItem("回窝");
        _homeLeaveItem.Click += (_, _) => ToggleHomeLeave();
        _menu.Items.Add(_homeLeaveItem);
        _menu.Items.Add("设置", null, (_, _) => showSettings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => exit());
        _notifyIcon.ContextMenuStrip = _menu;

        _notifyIcon.DoubleClick += OnTrayDoubleClick;
    }

    /// <summary>随 LifeState 切换菜单项文字（AtHome → 出窝；Roaming → 回窝）。App 在 manager.LifeStateChanged 里调用。</summary>
    public void SetAtHome(bool atHome)
    {
        _isAtHome = atHome;
        _homeLeaveItem.Text = atHome ? "出窝" : "回窝";
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

    /// <summary>托盘双击（Windows 上左键单击默认不触发 NotifyIcon 事件，双击为实际交互）：
    /// AtHome → 出窝（掉落出场）；Roaming → 进窝（走回顶部淡出）。</summary>
    private void ToggleHomeLeave()
    {
        if (_isAtHome)
        {
            _leaveHome();
        }
        else
        {
            _goHome();
        }
    }

    private void OnTrayDoubleClick(object? sender, EventArgs e) => ToggleHomeLeave();

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
