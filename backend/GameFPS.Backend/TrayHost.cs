using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GameFPS.Backend;

/// <summary>系统托盘图标与右键菜单。</summary>
public sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _overlayItem;
    private readonly ToolStripMenuItem _autoStartItem;

    public event Action? OpenMainRequested;
    public event Action? ToggleOverlayRequested;
    /// <summary>用户勾选/取消托盘菜单里的「开机自启」。参数是勾选后的目标状态。</summary>
    public event Action<bool>? AutoStartToggleRequested;
    public event Action? ExitRequested;

    public TrayHost(bool overlayChecked, bool autoStartChecked)
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("打开主界面");
        openItem.Click += (_, _) => OpenMainRequested?.Invoke();

        _overlayItem = new ToolStripMenuItem("显示游戏内悬浮窗")
        {
            Checked = overlayChecked,
            CheckOnClick = true,
        };
        _overlayItem.Click += (_, _) => ToggleOverlayRequested?.Invoke();

        // 开机自启：CheckOnClick 让它自己翻状态，但我们不直接信任它 ——
        // 写注册表可能失败，最后以 SetAutoStartChecked 回推的值为准。
        _autoStartItem = new ToolStripMenuItem("开机自启")
        {
            Checked = autoStartChecked,
            CheckOnClick = true,
        };
        _autoStartItem.Click += (_, _) => AutoStartToggleRequested?.Invoke(_autoStartItem.Checked);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(openItem);
        menu.Items.Add(_overlayItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "游戏帧率实时监测",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // 左键单击直接打开主界面（右键仍然弹菜单）
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenMainRequested?.Invoke();
        };
        _icon.DoubleClick += (_, _) => OpenMainRequested?.Invoke();
    }

    public void SetOverlayChecked(bool value)
    {
        if (_overlayItem.Checked != value) _overlayItem.Checked = value;
    }

    /// <summary>
    /// 回推开机自启的真实状态。写注册表失败时用它把菜单勾选弹回来，
    /// 免得界面显示已开启、实际没写进去。
    /// </summary>
    public void SetAutoStartChecked(bool value)
    {
        if (_autoStartItem.Checked != value) _autoStartItem.Checked = value;
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(3000);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var back = new SolidBrush(Color.FromArgb(255, 11, 15, 20));
            g.FillEllipse(back, 0, 0, 31, 31);

            using var ring = new Pen(Color.FromArgb(255, 110, 243, 192), 3f);
            g.DrawArc(ring, 3f, 3f, 25f, 25f, -80f, 285f);

            using var font = new Font("Consolas", 14, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.FromArgb(255, 110, 243, 192));
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("F", font, fg, new RectangleF(0, 1, 32, 32), sf);
        }

        // GetHicon 返回的句柄必须自己销毁，否则每次启动都泄漏一个 HICON。
        // 先 Clone 出一份由 Icon 自己托管的副本，再销毁原句柄。
        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { }
    }
}
