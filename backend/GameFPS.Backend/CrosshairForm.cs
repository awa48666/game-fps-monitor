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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

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

    /// <summary>
    /// 看门狗：每秒重新断言一次置顶并补推位图。
    /// 准星平时不重绘，一旦被别的置顶窗口（游戏自带 overlay、播放器全屏条）
    /// 压到下面、或 DWM 在显示模式切换后丢掉分层窗口位图，就会"显示一段时间
    /// 之后再也不出现"，而没有任何事件能把它唤醒 —— 只能主动轮询纠正。
    /// </summary>
    private System.Windows.Forms.Timer? _watchdog;

    /// <summary>连续渲染失败次数；用于把日志压成每条故障只记一次。</summary>
    private int _failStreak;

    /// <summary>上次成功推图的时间戳（Environment.TickCount64），供看门狗判断是否需要补推。</summary>
    private long _lastPushAt;

    /// <summary>看门狗把窗口从"不可见"救回来的累计次数，只用于日志节流。</summary>
    private int _recoverCount;

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

        // 看门狗必须建在有消息循环的线程上，调用方（AppHost）就在 WinForms 主线程
        _watchdog = new System.Windows.Forms.Timer { Interval = 1000 };
        _watchdog.Tick += (_, _) => WatchdogTick();
        _watchdog.Start();

        if (settings.Enabled) Apply(settings);
    }

    /// <summary>
    /// 每秒一次的保活：可见时重申 TOPMOST 并重推一次位图。
    /// 重推而不是只 SetWindowPos，是为了覆盖 DWM 丢位图的情况
    /// （显卡驱动更新、分辨率/刷新率切换、独占全屏进出都会发生）。
    /// </summary>
    private void WatchdogTick()
    {
        if (IsDisposed || !IsHandleCreated) return;

        bool shouldShow = _settings.Enabled && (!_settings.HoldEnabled || _holdActive);
        if (!shouldShow) return;

        try
        {
            // 窗口被压到别的 topmost 之下时，重新插到 topmost 链顶端。
            // 这里必须记日志：这是"准星显示一段时间后自己不见了"的唯一可观测证据。
            if (!Visible)
            {
                Show();
                _recoverCount++;
                if (_recoverCount <= 5)
                    Log($"窗口已不可见，看门狗重新显示（累计 {_recoverCount} 次）");
            }

            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

            // 每 5 秒补推一次位图，防止 DWM 侧内容丢失
            if (Environment.TickCount64 - _lastPushAt >= 5000) RenderAndShow();
        }
        catch
        {
            // 看门狗自身绝不能让进程崩掉
        }
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
            // using 而不是手工 Dispose：PushToScreen 在半途抛异常时，
            // 下面那行 bmp.Dispose() 根本执行不到，看门狗开始补推后
            // 就成了每 5 秒漏一张位图的稳定泄漏。
            using var bmp = RenderBitmap(_settings, out _);
            var screen = TargetScreen(_settings);

            int left = screen.Bounds.X + (screen.Bounds.Width - bmp.Width) / 2 + _settings.OffsetX;
            int top = screen.Bounds.Y + (screen.Bounds.Height - bmp.Height) / 2 + _settings.OffsetY;

            PushToScreen(bmp, left, top);

            _lastPushAt = Environment.TickCount64;

            if (!Visible) Show();

            if (_failStreak > 0)
            {
                Log($"恢复渲染（此前连续失败 {_failStreak} 次）");
                _failStreak = 0;
            }
        }
        catch (Exception ex)
        {
            // 以前这里是全静默的 catch，结果"准星显示一段时间后消失"完全无从查起。
            // 现在只记前 3 次，避免显示器热插拔时把日志刷爆。
            _failStreak++;
            if (_failStreak <= 3)
                Log($"渲染失败 #{_failStreak}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Log(string msg)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crosshair.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
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
    ///
    /// 细线图（donk / s1mple / TShape 这类 220x220 但非透明像素不足 120 个）
    /// 直接缩到 64 px 时，线条覆盖率被插值摊薄成 alpha 极低的雾，预乘 Alpha 后
    /// 屏幕上几乎看不见。这里按 alpha 总能量做补偿：缩放丢掉的覆盖率按比例
    /// 补回去，线条亮度恢复而不会变粗。
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

        // PrepareSource 可能原样返回缓存中的 _image（图比目标尺寸小时），
        // 所以这里只释放它真正新建的那份，绝不 Dispose 缓存的源图。
        var source = PrepareSource(img, draw);
        try
        {
            g.DrawImage(source, new Rectangle(x, y, draw, draw),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
        }
        finally
        {
            if (!ReferenceEquals(source, img)) source.Dispose();
        }

        return bmp;
    }

    /// <summary>
    /// 缩放并补偿 alpha 能量。细线条直接一次缩到 1/3 以下会被插值摊平成
    /// alpha 极低的雾：总覆盖率没丢，只是被摊到了更大面积上，单像素亮度掉光。
    /// 按"源图 alpha 总和 / 缩放后 alpha 总和"补一个增益，线条就能恢复可见而不变粗。
    /// 增益上限 16：内置 220px 预设缩到 64px 时实测需要 9.6~11.9，原上限 8
    /// 会把这些细线图截断在"仍然偏淡"；空白图由 srcEnergy<=0 分支挡住，
    /// 上限只为防极端噪声。
    /// </summary>
    private static Bitmap PrepareSource(Image img, int target)
    {
        int w = img.Width, h = img.Height;
        if (target >= w || w <= 0 || h <= 0) return ToBitmap(img);

        var scaled = new Bitmap(target, target, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(img, new Rectangle(0, 0, target, target), 0, 0, w, h, GraphicsUnit.Pixel);
        }

        // 统一成 32bppArgb 后再逐像素补 alpha（源图可能是 24bpp 或带调色板）。
        // 注意：ToBitmap 在源图本来就是 32bppArgb 时直接返回原对象（零拷贝），
        // 那可能是 LoadImage 缓存着的 _image。用 using 会把调用方的缓存图释放掉，
        // 下次渲染拿到的就是已释放对象 —— GDI+ 报 "ArgumentException: Parameter is
        // not valid."，表现为准星闪一下之后再也不出现。所以只释放自己拷贝的那份。
        var src = ToBitmap(img);
        try
        {
            BoostAlpha(scaled, src);
        }
        catch
        {
            // 补偿失败就用未补偿的缩放结果，至少还能画出东西
        }
        finally
        {
            if (!ReferenceEquals(src, img)) src.Dispose();
        }

        return scaled;
    }

    /// <summary>
    /// 把 scaled 的 alpha 按 src 的 alpha 总量做比例补偿。
    /// 两个位图尺寸不同，这里只比较总和，不需要逐像素对应。
    /// </summary>
    private static unsafe void BoostAlpha(Bitmap scaled, Bitmap src)
    {
        long srcEnergy = 0;
        var rect = new Rectangle(0, 0, src.Width, src.Height);
        var sd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* p = (byte*)sd.Scan0;
            for (int y = 0; y < src.Height; y++)
            {
                byte* row = p + y * sd.Stride;
                for (int x = 0; x < src.Width; x++)
                    srcEnergy += row[x * 4 + 3];
            }
        }
        finally { src.UnlockBits(sd); }

        if (srcEnergy <= 0) return;

        long dstEnergy = 0;
        var rect2 = new Rectangle(0, 0, scaled.Width, scaled.Height);
        var dd = scaled.LockBits(rect2, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte* p = (byte*)dd.Scan0;
            for (int y = 0; y < scaled.Height; y++)
            {
                byte* row = p + y * dd.Stride;
                for (int x = 0; x < scaled.Width; x++)
                    dstEnergy += row[x * 4 + 3];
            }

            if (dstEnergy <= 0) return;

            double gain = Math.Min(16.0, (double)srcEnergy / dstEnergy);
            if (gain <= 1.05) return;   // 本来就没损失，不折腾

            for (int y = 0; y < scaled.Height; y++)
            {
                byte* row = p + y * dd.Stride;
                for (int x = 0; x < scaled.Width; x++)
                {
                    int a = row[x * 4 + 3];
                    if (a == 0) continue;             // 全透明像素保持透明
                    int na = (int)Math.Round(a * gain);
                    if (na > 255) na = 255;
                    row[x * 4 + 3] = (byte)na;
                }
            }
        }
        finally { scaled.UnlockBits(dd); }
    }

    private static Bitmap ToBitmap(Image img)
    {
        if (img is Bitmap bmp && bmp.PixelFormat == PixelFormat.Format32bppArgb) return bmp;
        var copy = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(copy);
        g.DrawImage(img, 0, 0, img.Width, img.Height);
        return copy;
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
            try
            {
                if (_watchdog != null)
                {
                    _watchdog.Stop();
                    _watchdog.Dispose();
                    _watchdog = null;
                }
            }
            catch { }

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
