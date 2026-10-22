using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace GameFPS.Backend;

/// <summary>
/// 屏幕准星：一块只含准星像素的分层窗口，恒定居中于目标显示器（可加偏移）。
///
/// 与 OverlayForm 用的是同一套 UpdateLayeredWindow + 预乘 Alpha 推送方式，
/// 区别有两点：
///   1. 窗口尺寸只等于准星自身的包围盒，不是整块悬浮面板；
///   2. WS_EX_TRANSPARENT 常开 —— 准星永远不吃鼠标事件，游戏里的点击照常穿透。
/// 因此它不会引入输入延迟，也不需要在游戏内切换焦点。
/// </summary>
public sealed class CrosshairForm : Form
{
    // ---- Win32 样式 ----
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;

    // ---- UpdateLayeredWindow 相关常量 ----
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
        public SIZE(int w, int h) { cx = w; cy = h; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
        int usage, out IntPtr ppvBits, IntPtr hSection, int offset);

    private CrosshairSettings _settings;

    /// <summary>0 = 无待处理重绘，1 = 已排队一次。用于合并高频 Apply（拖滑块）时的调用。</summary>
    private int _queued;

    /// <summary>图片准星的解码缓存（避免每帧从磁盘重读）。</summary>
    private Image? _image;
    private string? _imagePath;
    private DateTime _imageStamp;

    /// <summary>按住模式下主键当前是否按住（由 CrosshairHold 驱动）。</summary>
    private volatile bool _holdActive;

    public CrosshairForm(CrosshairSettings settings)
    {
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque, true);

        // 建句柄后才能推分层位图
        _ = Handle;

        if (settings.Enabled) Apply(settings);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // TRANSPARENT 常开：准星只负责显示，不接收任何鼠标消息
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                          | WS_EX_TOPMOST | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    /// <summary>绝不抢游戏焦点。</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// 应用新参数。可从任意线程调用（IPC 回调跑在线程池上），内部切回 UI 线程。
    /// </summary>
    public void Apply(CrosshairSettings settings)
    {
        _settings = settings;

        if (IsDisposed) return;

        if (InvokeRequired)
        {
            // 拖滑块时 IPC 会连续送达，合并成一次重绘即可
            if (Interlocked.Exchange(ref _queued, 1) == 1) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    Interlocked.Exchange(ref _queued, 0);
                    ApplySelf();
                }));
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref _queued, 0); }
            return;
        }

        ApplySelf();
    }

    /// <summary>必须在 UI 线程上执行。</summary>
    private void ApplySelf()
    {
        if (IsDisposed || !IsHandleCreated) return;

        // 按住模式开启时，只有主键按住期间才显示；Enabled 仍是总开关
        bool visible = _settings.Enabled && (!_settings.HoldEnabled || _holdActive);
        if (!visible)
        {
            Hide();
            return;
        }

        RenderAndShow();
    }

    /// <summary>
    /// 按住模式：由 CrosshairHold 在按下/松开时调用，只改变可见性，不动 Enabled。
    /// </summary>
    public void SetHoldActive(bool active)
    {
        if (_holdActive == active) return;
        _holdActive = active;
        Apply(_settings);
    }

    private void RenderAndShow()
    {
        if (IsDisposed || !IsHandleCreated) return;

        try
        {
            var bmp = RenderBitmap(_settings, out _);
            var screen = TargetScreen(_settings);

            int left = screen.Bounds.X + (screen.Bounds.Width - bmp.Width) / 2 + _settings.OffsetX;
            int top = screen.Bounds.Y + (screen.Bounds.Height - bmp.Height) / 2 + _settings.OffsetY;

            PushToScreen(bmp, left, top);
            bmp.Dispose();

            if (!Visible) Show();
        }
        catch
        {
            // 显示器热插拔瞬间可能取不到屏幕，静默跳过这一次重绘
        }
    }

    /// <summary>取准星要落在哪块屏幕上；序号越界时退回主屏。</summary>
    private static Screen TargetScreen(CrosshairSettings s)
    {
        var screens = Screen.AllScreens;
        if (s.MonitorIndex >= 0 && s.MonitorIndex < screens.Length) return screens[s.MonitorIndex];
        return Screen.PrimaryScreen ?? screens[0];
    }

    // ---------- 绘制 ----------

    /// <summary>
    /// 按 SVG 版的同一套几何画准星：所有尺寸都以准星中心为原点，
    /// extent 为半边长，窗口即 extent*2 见方。
    /// </summary>
    private Bitmap RenderBitmap(CrosshairSettings s, out int extent)
    {
        // 图片准星优先；图丢了就直接按程序绘制画，别留一个空白窗口
        if (s.UseCustomImage)
        {
            var img = LoadImage(s.CustomImagePath);
            if (img != null) return RenderImageBitmap(s, img, out extent);
        }

        int thickness = Math.Max(1, s.Thickness);
        int outline = s.OutlineEnabled && s.OutlineThickness > 0 ? s.OutlineThickness : 0;
        float halfOutline = thickness / 2f + outline;

        float crossExtent = s.Gap + s.Size + halfOutline;
        float circleExtent = s.Size + halfOutline;
        float dotExtent = s.DotSize / 2f + outline;

        float span = Math.Max(Math.Max(crossExtent, circleExtent), dotExtent);
        if (span < 2) span = 2;                       // 极端小参数下也别退化成 0 尺寸
        extent = (int)Math.Ceiling(span) + 2;          // +2 给抗锯齿边缘留余量

        int side = extent * 2;
        var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var main = WithAlpha(ParseColor(s.Color, Color.Red), s.Opacity);
        var line = WithAlpha(ParseColor(s.OutlineColor, Color.Black), s.Opacity);

        using var penMain = new Pen(main, thickness)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Miter,
        };
        using var penOutline = new Pen(line, thickness + outline * 2)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Miter,
        };
        using var brushMain = new SolidBrush(main);
        using var brushOutline = new SolidBrush(line);

        float cx = extent, cy = extent;
        int size = Math.Max(1, s.Size);
        int gap = Math.Max(0, s.Gap);
        int dot = Math.Max(0, s.DotSize);

        void Arms(Pen pen)
        {
            g.DrawLine(pen, cx, cy - gap - size, cx, cy - gap);
            g.DrawLine(pen, cx, cy + gap, cx, cy + gap + size);
            g.DrawLine(pen, cx - gap - size, cy, cx - gap, cy);
            g.DrawLine(pen, cx + gap, cy, cx + gap + size, cy);
        }

        void Ring(Pen pen, float extra)
        {
            float r = size + extra;
            g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        }

        void Box(Pen pen, float extra)
        {
            float r = size + extra;
            g.DrawRectangle(pen, cx - r, cy - r, r * 2, r * 2);
        }

        void Dot(Brush brush, int d)
        {
            if (d <= 0) return;
            g.FillEllipse(brush, cx - d / 2f, cy - d / 2f, d, d);
        }

        switch (s.Style)
        {
            case "Dot":
                if (outline > 0) Dot(brushOutline, dot + outline * 2);
                Dot(brushMain, dot);
                break;

            case "Circle":
                if (outline > 0) Ring(penOutline, outline);
                Ring(penMain, 0);
                break;

            case "CrossDot":
                if (outline > 0) { Arms(penOutline); Dot(brushOutline, dot + outline * 2); }
                Arms(penMain);
                Dot(brushMain, dot);
                break;

            case "CircleCross":
                if (outline > 0) { Ring(penOutline, outline); Arms(penOutline); }
                Ring(penMain, 0);
                Arms(penMain);
                break;

            case "DotBox":
                if (outline > 0) { Box(penOutline, outline); Dot(brushOutline, dot + outline * 2); }
                Box(penMain, 0);
                Dot(brushMain, dot);
                break;

            default:   // "Cross" 与未知值
                if (outline > 0) Arms(penOutline);
                Arms(penMain);
                break;
        }

        return bmp;
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch { return fallback; }
    }

    private static Color WithAlpha(Color c, int opacity)
        => Color.FromArgb(Math.Clamp(opacity, 0, 255), c.R, c.G, c.B);

    // ---------- 图片准星 ----------

    /// <summary>
    /// 把图片拉成 ImageSize 见方的方块（与原版一致：不保持宽高比，方形窗口居中）。
    /// 透明度通过 ColorMatrix 的 alpha 通道一次乘进去，避免逐像素再处理一遍。
    /// </summary>
    private static Bitmap RenderImageBitmap(CrosshairSettings s, Image img, out int extent)
    {
        int draw = Math.Max(1, s.ImageSize > 0 ? s.ImageSize : Math.Max(1, s.Size));
        int side = Math.Max(draw + 16, 64);
        extent = side / 2;

        var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        int x = (side - draw) / 2;
        int y = (side - draw) / 2;

        using var attrs = new ImageAttributes();
        int opacity = Math.Clamp(s.Opacity, 0, 255);
        if (opacity < 255)
        {
            var cm = new ColorMatrix { Matrix33 = opacity / 255f };
            attrs.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        }

        g.DrawImage(img, new Rectangle(x, y, draw, draw),
            0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);

        return bmp;
    }

    /// <summary>
    /// 读取图片并缓存。先读进内存再解码：Image.FromFile 会长期锁住文件，
    /// 用户换图/删图时那种锁会让操作静默失败。
    /// </summary>
    private Image? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); }
        catch { return null; }

        if (_image != null &&
            string.Equals(_imagePath, path, StringComparison.OrdinalIgnoreCase) &&
            _imageStamp == stamp)
            return _image;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            using var decoded = Image.FromStream(ms);
            var copy = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(copy))
                g.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height);

            _image?.Dispose();
            _image = copy;
            _imagePath = path;
            _imageStamp = stamp;
            return _image;
        }
        catch
        {
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _image?.Dispose();
            _image = null;
        }
        base.Dispose(disposing);
    }

    // ---------- 推送到屏幕 ----------

    private unsafe void PushToScreen(Bitmap bmp, int x, int y)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hDib = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;

        try
        {
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = bmp.Width,
                    biHeight = -bmp.Height,     // 负高度 = 自上而下的 DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,          // BI_RGB
                }
            };

            hDib = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (hDib == IntPtr.Zero || bits == IntPtr.Zero) return;
            oldObj = SelectObject(memDc, hDib);

            // UpdateLayeredWindow 要预乘 Alpha：GDI+ 给的是非预乘像素，这里手工乘一遍
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int w = bmp.Width, h = bmp.Height;
                byte* src = (byte*)data.Scan0;
                byte* dst = (byte*)bits;
                for (int row = 0; row < h; row++)
                {
                    byte* s = src + row * data.Stride;
                    byte* d = dst + row * w * 4;
                    for (int col = 0; col < w; col++, s += 4, d += 4)
                    {
                        byte a = s[3];
                        d[0] = (byte)(s[0] * a / 255);
                        d[1] = (byte)(s[1] * a / 255);
                        d[2] = (byte)(s[2] * a / 255);
                        d[3] = a;
                    }
                }
            }
            finally { bmp.UnlockBits(data); }

            var size = new SIZE(bmp.Width, bmp.Height);
            var srcLoc = new POINT(0, 0);
            var dstLoc = new POINT(x, y);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            UpdateLayeredWindow(Handle, screenDc, ref dstLoc, ref size, memDc, ref srcLoc, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (hDib != IntPtr.Zero)
            {
                if (oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
                DeleteObject(hDib);
            }
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
