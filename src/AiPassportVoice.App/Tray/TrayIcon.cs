using System.Drawing;
using System.Windows.Forms;

namespace AiPassportVoice.App.Tray;

/// <summary>系统托盘图标与菜单（WinForms NotifyIcon，框架内置，无 NuGet）。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _appIcon;

    public TrayIcon(
        Action show,
        Action exit,
        Func<bool> isRunning,
        Action toggleForwarding,
        Func<bool> isAutoStart,
        Action<bool> setAutoStart)
    {
        _appIcon = LoadIcon();

        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("显示主界面");
        showItem.Font = new Font(showItem.Font, FontStyle.Bold);
        showItem.Click += (_, _) => show();

        var toggleItem = new ToolStripMenuItem("停止转发");
        toggleItem.Click += (_, _) => toggleForwarding();

        var autoItem = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true };
        autoItem.Click += (_, _) => setAutoStart(autoItem.Checked);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => exit();

        menu.Items.Add(showItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        // 每次展开菜单时按当前真实状态刷新
        menu.Opening += (_, _) =>
        {
            toggleItem.Text = isRunning() ? "停止转发" : "开始转发";
            autoItem.Checked = isAutoStart();
        };

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "AI Passport 语音",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => show();
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(4000);
    }

    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
                return new Icon(stream, new Size(32, 32));
        }
        catch { /* 回退到系统图标 */ }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
