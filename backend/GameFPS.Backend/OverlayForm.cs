using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GameFPS.Core;

namespace GameFPS.Backend;

/// <summary>
/// 游戏内悬浮窗：无边框 + 置顶 + 点击穿透的分层窗口。
/// 通过 UpdateLayeredWindow 推送【预乘 Alpha】位图，因此能同时得到
/// 半透明圆角面板与抗锯齿文字（普通 TransparencyKey 方案做不到半透明）。
///
/// 注意：非注入式悬浮层只能盖在“窗口化 / 无边框全屏”的游戏画面上。
/// 真正独占全屏(exclusive fullscreen)的游戏独占显示输出，任何不注入的
/// 桌面窗口都无法覆盖——这是该路线的固有边界（RTSS/游戏加加靠注入解决）。
/// </summary>
public sealed class OverlayForm : Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int GWL_EXSTYLE = -20;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

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
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
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

    private readonly object _gate = new();
    private OverlaySettings _settings;
    private MonitorSnapshot? _pending;
    private bool _renderQueued;

    // ---- 拖动定位 ----
    private bool _dragMode;
    private bool _dragging;
    private Point _dragStartScreen;
    private int _dragStartX, _dragStartY;

    /// <summary>拖动结束时触发，参数为新的屏幕坐标。</summary>
    public event Action<int, int>? DragCompleted;

    public bool OverlayVisible { get; private set; }

    public OverlayForm(OverlaySettings settings)
    {
        _settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            // 拖动定位模式下不能穿透，否则收不到鼠标消息
            if (!_dragMode) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    /// <summary>
    /// 进入/退出"拖动定位"模式。
    /// 平时悬浮窗带 WS_EX_TRANSPARENT（点击穿透），游戏里不会挡住操作；
    /// 调整位置时需要收鼠标事件，所以临时去掉这个样式。
    /// </summary>
    public void SetDragMode(bool on)
    {
        if (_dragMode == on) return;
        _dragMode = on;

        if (IsHandleCreated)
        {
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            ex = on ? (ex & ~WS_EX_TRANSPARENT) : (ex | WS_EX_TRANSPARENT);
            SetWindowLong(Handle, GWL_EXSTYLE, ex);

            // 拖动时给个视觉反馈：光标变成十字
            Cursor = on ? Cursors.SizeAll : Cursors.Default;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_dragMode && e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragStartScreen = Cursor.Position;
            _dragStartX = _settings.X;
            _dragStartY = _settings.Y;
            Capture = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            var now = Cursor.Position;
            _settings.X = _dragStartX + (now.X - _dragStartScreen.X);
            _settings.Y = _dragStartY + (now.Y - _dragStartScreen.Y);
            RequestRender();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            DragCompleted?.Invoke(_settings.X, _settings.Y);
        }
        base.OnMouseUp(e);
    }

    /// <summary>不抢焦点。</summary>
    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        OverlayVisible = true;
    }

    public void UpdateSettings(OverlaySettings settings)
    {
        _settings = settings;
        RequestRender();
    }

    /// <summary>由采集线程调用；内部切回 UI 线程渲染，并做积压合并。</summary>
    public void ApplySnapshot(MonitorSnapshot snap)
    {
        lock (_gate) _pending = snap;
        RequestRender();
    }

    private void RequestRender()
    {
        if (IsDisposed || !IsHandleCreated) return;
        lock (_gate)
        {
            if (_renderQueued) return;         // 合并高频调用，避免消息队列积压
            _renderQueued = true;
        }
        try
        {
            BeginInvoke(new Action(() =>
            {
                lock (_gate) _renderQueued = false;
                RenderFromPending();
            }));
        }
        catch (InvalidOperationException)
        {
            lock (_gate) _renderQueued = false;
        }
    }

    private void RenderFromPending()
    {
        MonitorSnapshot? snap;
        lock (_gate) snap = _pending;
        if (snap == null || IsDisposed) return;
        Render(snap);
    }

    public void HideOverlay()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { Hide(); OverlayVisible = false; })); }
        catch (InvalidOperationException) { }
    }

    /// <summary>拖动定位模式下即使没有游戏也要把悬浮窗显示出来，否则没东西可拖。</summary>
    public void ShowForDrag()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (!Visible) { Show(); OverlayVisible = true; }
                // 没有实时数据时先用最后一帧快照渲染一次，保证有东西可拖
                if (_pending != null) Render(_pending);
                BringToFront();
            }));
        }
        catch (InvalidOperationException) { }
    }

    private readonly record struct Row(string Label, string Value, bool IsTitle, string Key);

    private List<Row> BuildRows(MonitorSnapshot s)
    {
        // 样式2 = 横排单行；样式1 = 竖排单列；样式3 = 竖排双列（每行两项）
        return _settings.OverlayStyle == 2
            ? BuildRowsHorizontal(s)
            : BuildRowsVertical(s);
    }

    /// <summary>是否双列布局（样式3）。</summary>
    private bool IsTwoColumn => _settings.OverlayStyle == 3;

    /// <summary>
    /// 「使用预设样式」开关的实际取值。
    /// 设置里的字段是可空的：老配置（v1.0.3 起网格就是"有块就用"）里没有这一项，
    /// 反序列化后是 null，表示用户没表过态 —— 这时按老行为推导，
    /// 有网格块就走网格，没网格块就走预设样式，升级后版式不会突然变样。
    /// </summary>
    private bool UsePresetStyleResolved =>
        _settings.UsePresetStyle
        ?? !(_settings.OverlayBlocks != null && _settings.OverlayBlocks.Count > 0);

    /// <summary>
    /// 是否使用用户自定义的自由网格布局。
    /// 由设置页的「使用预设样式」开关决定：开关打开走预设样式（样式1/2/3），
    /// 关闭才走网格。网格为空时无论开关如何都退回预设样式，
    /// 否则关掉开关会让悬浮窗变空、看起来像"消失"。
    /// </summary>
    private bool UseGridLayout =>
        !UsePresetStyleResolved
        && _settings.OverlayBlocks != null
        && _settings.OverlayBlocks.Count > 0;

    /// <summary>
    /// 按块类型取显示内容。返回 (label, value)；
    /// value 为 null 表示该块当前不可显示（例如非异构 CPU 上的大小核）。
    /// 曲线块返回特殊标记，由渲染层单独处理。
    /// </summary>
    private (string Label, string Value) BlockContent(string type, MonitorSnapshot s)
    {
        switch (type)
        {
            case "gamename":
                return string.IsNullOrEmpty(s.GameName) ? ("", MatchNoValue) : ("", s.GameName);
            case "fps":
                return ("FPS", Fps(s.Fps));
            case "frametime":
                return ("帧时间", s.AvgFrameTimeMs.ToString("0.00") + " ms");
            case "low1":
                return ("1% Low", Fps(s.Low1PercentFps));
            case "low01":
                return ("0.1% Low", Fps(s.Low01PercentFps));
            case "cpu":
                return ("CPU", BuildCpuText(s));
            case "gpu":
                return ("GPU", BuildGpuText(s));
            case "pcore":
                // 以前这里要求频率必须 >0 才显示整块，频率读不到时连占用率一起消失。
                // 改成占用率为主体、频率有则追加，与竖排 / 横排两种样式保持一致。
                if (!s.HasHeterogeneousCores) return ("", MatchNoValue);
                {
                    string p = $"{s.PCoreUsagePercent:0.0} %";
                    if (s.PCoreFrequencyMHz > 0) p += $"  {s.PCoreFrequencyMHz / 1000.0:0.00} GHz";
                    return ("大核", p);
                }
            case "ecore":
                if (!s.HasHeterogeneousCores) return ("", MatchNoValue);
                {
                    string e = $"{s.ECoreUsagePercent:0.0} %";
                    if (s.ECoreFrequencyMHz > 0) e += $"  {s.ECoreFrequencyMHz / 1000.0:0.00} GHz";
                    return ("小核", e);
                }
            case "vram":
                return ("显存", s.GpuMemoryMB.ToString("0") + " MB");
            case "memory":
                return ("内存", s.MemoryUsagePercent.ToString("0.0") + " %");
            case "curve":
            case "ftcurve":
                // 曲线块没有文本内容，由渲染层按 Type 单独绘制；
                // 这里必须返回非 MatchNoValue，否则会被当成"不可显示"跳过
                return ("", "");
            default:
                return ("", MatchNoValue);
        }
    }

    /// <summary>不可显示的块的占位标记。</summary>
    private const string MatchNoValue = "\u0001NONE";

    /// <summary>样式2：所有数值压成一行横排，最省屏幕空间。</summary>
    private List<Row> BuildRowsHorizontal(MonitorSnapshot s)
    {
        var parts = new List<string>();

        if (_settings.ShowGameName && !string.IsNullOrEmpty(s.GameName))
            parts.Add(s.GameName);

        if (_settings.ShowFps) parts.Add("FPS " + Fps(s.Fps));
        if (_settings.ShowFrameTime) parts.Add("帧时间 " + s.AvgFrameTimeMs.ToString("0.00") + "ms");
        if (_settings.ShowLow1) parts.Add("1% Low " + Fps(s.Low1PercentFps));
        if (_settings.ShowLow01) parts.Add("0.1% Low " + Fps(s.Low01PercentFps));
        if (_settings.ShowCpu) parts.Add("CPU " + BuildCpuText(s));
        // 大小核：占用率必给，频率有就带上。这里以前只拼占用率，
        // 勾选「大核/小核」后横排样式永远看不到频率，和竖排样式不一致。
        if (_settings.ShowPCore && s.HasHeterogeneousCores
            && (s.PCoreUsagePercent > 0 || s.PCoreFrequencyMHz > 0))
        {
            string p = "大核 " + s.PCoreUsagePercent.ToString("0") + "%";
            if (s.PCoreFrequencyMHz > 0) p += $" {s.PCoreFrequencyMHz / 1000.0:0.00}G";
            parts.Add(p);
        }
        if (_settings.ShowECore && s.HasHeterogeneousCores
            && (s.ECoreUsagePercent > 0 || s.ECoreFrequencyMHz > 0))
        {
            string e = "小核 " + s.ECoreUsagePercent.ToString("0") + "%";
            if (s.ECoreFrequencyMHz > 0) e += $" {s.ECoreFrequencyMHz / 1000.0:0.00}G";
            parts.Add(e);
        }
        if (_settings.ShowGpu) parts.Add("GPU " + BuildGpuText(s));
        if (_settings.ShowGpuMemory && _settings.ShowVramTemperature && s.HasGpuMemoryTemperature)
            parts.Add("显存 " + s.GpuMemoryTemperatureC.ToString("0") + "°C");
        if (_settings.ShowMemory) parts.Add("内存 " + s.MemoryUsagePercent.ToString("0") + "%");

        var rows = new List<Row>();
        if (parts.Count > 0) rows.Add(new Row("", string.Join("   ", parts), false, "hline"));
        return rows;
    }

    /// <summary>样式1：每项一行的竖排列表。</summary>
    private List<Row> BuildRowsVertical(MonitorSnapshot s)
    {
        var rows = new List<Row>();

        // 游戏名是标题，永远置顶，不参与排序
        if (_settings.ShowGameName && !string.IsNullOrEmpty(s.GameName))
            rows.Add(new Row("", s.GameName, true, "gamename"));

        if (_settings.ShowFps) rows.Add(new Row("FPS", Fps(s.Fps), false, "fps"));
        if (_settings.ShowFrameTime) rows.Add(new Row("帧时间", s.AvgFrameTimeMs.ToString("0.00") + " ms", false, "frametime"));
        if (_settings.ShowLow1) rows.Add(new Row("1% Low", Fps(s.Low1PercentFps), false, "low1"));
        if (_settings.ShowLow01) rows.Add(new Row("0.1% Low", Fps(s.Low01PercentFps), false, "low01"));

        if (_settings.ShowCpu) rows.Add(new Row("CPU", BuildCpuText(s), false, "cpu"));

        // 大小核：只在异构 CPU 上显示，且各自独立开关
        if (s.HasHeterogeneousCores)
        {
            if (_settings.ShowPCore && (s.PCoreUsagePercent > 0 || s.PCoreFrequencyMHz > 0))
            {
                string p = s.PCoreUsagePercent.ToString("0.0") + " %";
                if (s.PCoreFrequencyMHz > 0) p += $"  {s.PCoreFrequencyMHz / 1000.0:0.00} GHz";
                rows.Add(new Row("大核", p, false, "pcore"));
            }
            if (_settings.ShowECore && (s.ECoreUsagePercent > 0 || s.ECoreFrequencyMHz > 0))
            {
                string e = s.ECoreUsagePercent.ToString("0.0") + " %";
                if (s.ECoreFrequencyMHz > 0) e += $"  {s.ECoreFrequencyMHz / 1000.0:0.00} GHz";
                rows.Add(new Row("小核", e, false, "ecore"));
            }
        }

        if (_settings.ShowGpu) rows.Add(new Row("GPU", BuildGpuText(s), false, "gpu"));

        if (_settings.ShowGpuMemory)
        {
            string vram = s.GpuMemoryMB.ToString("0") + " MB";
            if (_settings.ShowVramTemperature && s.HasGpuMemoryTemperature && s.GpuMemoryTemperatureC > 0)
                vram += $"  {s.GpuMemoryTemperatureC:0}°C";
            rows.Add(new Row("显存", vram, false, "vram"));
        }

        if (_settings.ShowMemory) rows.Add(new Row("内存", s.MemoryUsagePercent.ToString("0.0") + " %", false, "memory"));

        return ApplyCustomOrder(rows);
    }

    /// <summary>
    /// 按用户自定义的顺序重排。标题行保持最前；
    /// 没出现在自定义顺序里的项（比如新版本新增的）排在末尾并保持原有相对次序。
    /// </summary>
    private List<Row> ApplyCustomOrder(List<Row> rows)
    {
        var order = _settings.OverlayOrder;
        if (order == null || order.Count == 0) return rows;

        return rows
            .Select((r, i) => new { Row = r, Index = i, Pos = r.IsTitle ? -1 : order.IndexOf(r.Key) })
            .OrderBy(x => x.Pos < 0 ? (x.Row.IsTitle ? -1 : int.MaxValue) : x.Pos)
            .ThenBy(x => x.Index)
            .Select(x => x.Row)
            .ToList();
    }

    /// <summary>CPU 行的数值部分：占用率 + 可选的频率/功耗/温度。</summary>
    private string BuildCpuText(MonitorSnapshot s)
    {
        string cpu = s.CpuUsagePercent.ToString("0.0") + " %";
        if (_settings.ShowCpuFrequency && s.CpuFrequencyMHz > 0)
            cpu += $"  {s.CpuFrequencyMHz / 1000.0:0.00} GHz";
        if (_settings.ShowCpuPower && s.HasCpuPower && s.CpuPowerW > 0)
            cpu += $"  {s.CpuPowerW:0.0} W";
        if (_settings.ShowCpuTemperature && s.HasCpuTemperature && s.CpuTemperatureC > 0)
            cpu += $"  {s.CpuTemperatureC:0}°C";
        return cpu;
    }

    /// <summary>GPU 行的数值部分：占用率 + 可选的频率/功耗/温度。</summary>
    private string BuildGpuText(MonitorSnapshot s)
    {
        string gpu = s.GpuUsagePercent.ToString("0.0") + " %";
        if (_settings.ShowGpuFrequency && s.GpuFrequencyMHz > 0)
            gpu += $"  {s.GpuFrequencyMHz:0} MHz";
        if (_settings.ShowGpuPower && s.GpuPowerW > 0)
            gpu += $"  {s.GpuPowerW:0.0} W";
        if (_settings.ShowGpuTemperature && s.GpuTemperatureC > 0)
            gpu += $"  {s.GpuTemperatureC:0}°C";
        return gpu;
    }

    /// <summary>
    /// 双列布局下实际会占多少行：标题行独占一行，其余每两项并成一行。
    /// </summary>
    private static int CountVisualLines(List<Row> rows)
    {
        int lines = 0;
        int i = 0;
        while (i < rows.Count)
        {
            if (rows[i].IsTitle) { lines++; i++; continue; }
            // 看下一项能不能并到同一行
            if (i + 1 < rows.Count && !rows[i + 1].IsTitle) { lines++; i += 2; }
            else { lines++; i++; }
        }
        return lines;
    }

    private static string Fps(double v) => v <= 0.05 ? "--" : v.ToString("0");

    /// <summary>
    /// 自由网格布局渲染：每个已放置的块占一个格子，
    /// 列宽取该列最宽的块，行高取该行最高的块，曲线块也一样。
    /// </summary>
    private void RenderGrid(MonitorSnapshot s)
    {
        float scale = (float)Math.Clamp(_settings.Scale, 0.5, 4.0);
        using var font = new Font(_settings.FontFamily, 10.5f * scale, FontStyle.Bold, GraphicsUnit.Point);

        float pad = 10f * scale;
        float gap = 14f * scale;
        float cellPadX = 12f * scale;      // 格内左右留白
        float cellPadY = 6f * scale;       // 格内上下留白
        float rowGap = Math.Max(0f, _settings.RowGap) * scale;
        float lineH = font.GetHeight() + rowGap;

        float chartH = Math.Max(16f, _settings.OverlayChartHeight * scale);
        float ftChartH = Math.Max(16f, _settings.OverlayChartHeight * scale * 0.75f);
        float chartMinW = 150f * scale;

        // 收集可见块
        var cells = new Dictionary<(int, int), (string Label, string Value, string Type)>();
        int maxRow = 0, maxCol = 0;
        foreach (var b in _settings.OverlayBlocks)
        {
            var (label, value) = BlockContent(b.Type, s);
            if (value == MatchNoValue) continue;
            cells[(b.Row, b.Col)] = (label, value, b.Type);
            if (b.Row > maxRow) maxRow = b.Row;
            if (b.Col > maxCol) maxCol = b.Col;
        }

        if (cells.Count == 0)
        {
            if (Visible) { Hide(); OverlayVisible = false; }
            return;
        }

        int rows = Math.Max(Math.Max(1, _settings.GridRows), maxRow + 1);
        int cols = Math.Max(Math.Max(1, _settings.GridCols), maxCol + 1);

        float[] colW = new float[cols];
        float[] rowH = new float[rows];

        using (var mb = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(mb))
        {
            foreach (var kv in cells)
            {
                var (label, value, type) = kv.Value;
                float w, h;

                if (type == "curve") { w = chartMinW; h = chartH; }
                else if (type == "ftcurve") { w = chartMinW; h = ftChartH; }
                else
                {
                    float lw = label.Length > 0 ? mg.MeasureString(label, font).Width : 0f;
                    float vw = mg.MeasureString(value, font).Width;
                    w = lw + (lw > 0 ? gap : 0f) + vw;
                    h = lineH;
                }

                int c = kv.Key.Item2, r = kv.Key.Item1;
                if (w > colW[c]) colW[c] = w;
                if (h > rowH[r]) rowH[r] = h;
            }
        }

        // 空的列/行也要留出可拖入的面积
        for (int c = 0; c < cols; c++)
            if (colW[c] <= 0) colW[c] = chartMinW;
        for (int r = 0; r < rows; r++)
            if (rowH[r] <= 0) rowH[r] = lineH;

        float totalW = pad * 2;
        for (int c = 0; c < cols; c++) totalW += colW[c] + (c > 0 ? cellPadX : 0f);

        float totalH = pad * 2;
        for (int r = 0; r < rows; r++) totalH += rowH[r] + (r > 0 ? cellPadY : 0f);

        int width = Math.Max(80, (int)Math.Ceiling(totalW));
        int height = Math.Max(24, (int)Math.Ceiling(totalH));

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            float radius = Math.Max(0f, _settings.CornerRadius * scale);
            using (var path = RoundedRect(new RectangleF(0, 0, width, height), radius))
            using (var back = new SolidBrush(Color.FromArgb(
                Math.Clamp(_settings.BackOpacity, 0, 255), ParseColor(_settings.BackColor, Color.Black))))
            {
                g.FillPath(back, path);
            }

            using var labelBrush = new SolidBrush(ParseColor(_settings.LabelColor, Color.White));
            using var valueBrush = new SolidBrush(ParseColor(_settings.TextColor, Color.Lime));
            using var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0));

            // 逐格绘制
            for (int r = 0; r < rows; r++)
            {
                float y = pad;
                for (int i = 0; i < r; i++) y += rowH[i] + cellPadY;

                float x = pad;
                for (int c = 0; c < cols; c++)
                {
                    if (cells.TryGetValue((r, c), out var cell))
                    {
                        if (cell.Type == "curve")
                        {
                            DrawMiniChart(g, s.RecentFrameTimes,
                                s.Low1PercentHistory, s.Low01PercentHistory,
                                _settings.ShowOverlayLow1, _settings.ShowOverlayLow01,
                                x, y, colW[c], rowH[r], valueBrushColor);
                        }
                        else if (cell.Type == "ftcurve")
                        {
                            DrawFrameTimeMiniChart(g, s.RecentFrameTimes,
                                x, y, colW[c], rowH[r], Color.FromArgb(100, 210, 255));
                        }
                        else
                        {
                            if (cell.Label.Length > 0)
                            {
                                g.DrawString(cell.Label, font, shadow, x + 1f, y + 1f);
                                g.DrawString(cell.Label, font, labelBrush, x, y);
                                float lw = g.MeasureString(cell.Label, font).Width;
                                g.DrawString(cell.Value, font, shadow, x + lw + gap + 1f, y + 1f);
                                g.DrawString(cell.Value, font, valueBrush, x + lw + gap, y);
                            }
                            else
                            {
                                g.DrawString(cell.Value, font, shadow, x + 1f, y + 1f);
                                g.DrawString(cell.Value, font, valueBrush, x, y);
                            }
                        }
                    }
                    x += colW[c] + cellPadX;
                }
            }
        }

        PushToScreen(bmp, _settings.X, _settings.Y);

        if (!Visible)
        {
            Show();
            OverlayVisible = true;
        }
    }

    /// <summary>曲线用的颜色（取自数值颜色）。</summary>
    private Color valueBrushColor => ParseColor(_settings.TextColor, Color.Lime);

    private void Render(MonitorSnapshot s)
    {
        if (UseGridLayout) { RenderGrid(s); return; }

        var rows = BuildRows(s);
        if (rows.Count == 0)
        {
            if (Visible) { Hide(); OverlayVisible = false; }
            return;
        }

        float scale = (float)Math.Clamp(_settings.Scale, 0.5, 4.0);
        using var normalFont = new Font(_settings.FontFamily, 10.5f * scale, FontStyle.Bold, GraphicsUnit.Point);

        float pad = 10f * scale;
        float gap = 14f * scale;
        float rowGap = Math.Max(0f, _settings.RowGap) * scale;
        float lineH = normalFont.GetHeight() + rowGap;

        float labelW = 0f, valueW = 0f;
        using (var mb = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(mb))
        {
            foreach (var r in rows)
            {
                if (r.Label.Length > 0)
                    labelW = Math.Max(labelW, mg.MeasureString(r.Label, normalFont).Width);
                valueW = Math.Max(valueW, mg.MeasureString(r.Value, normalFont).Width);
            }
        }

        float chartH = _settings.ShowOverlayChart ? Math.Max(16f, _settings.OverlayChartHeight * scale) : 0f;
        float ftChartH = _settings.ShowOverlayFrameTime ? Math.Max(16f, _settings.OverlayChartHeight * scale * 0.75f) : 0f;
        float chartGap = (chartH > 0 || ftChartH > 0) ? 6f * scale : 0f;

        float chartsTotal = 0f;
        if (chartH > 0) chartsTotal += chartGap + chartH;
        if (ftChartH > 0) chartsTotal += chartGap + ftChartH;

        // 双列：每列宽度相同，列之间留一点间距；标题行仍独占整行
        bool twoCol = IsTwoColumn;
        float colGap = 18f * scale;
        float cellW = labelW + gap + valueW;
        int colsUsed = twoCol ? 2 : 1;

        float contentW = cellW * colsUsed + (colsUsed - 1) * colGap;
        int visualLines = twoCol ? CountVisualLines(rows) : rows.Count;

        int width = (int)Math.Ceiling(pad * 2 + contentW);
        int height = (int)Math.Ceiling(pad * 2 + lineH * visualLines + chartsTotal);

        // 画了曲线就给足宽度，否则曲线会被挤成一条竖线
        if (chartsTotal > 0) width = Math.Max(width, (int)Math.Ceiling(pad * 2 + 130 * scale));

        width = Math.Max(width, 80);
        height = Math.Max(height, 24);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            float radius = Math.Max(0f, _settings.CornerRadius * scale);
            using (var path = RoundedRect(new RectangleF(0, 0, width, height), radius))
            using (var back = new SolidBrush(Color.FromArgb(
                Math.Clamp(_settings.BackOpacity, 0, 255), ParseColor(_settings.BackColor, Color.Black))))
            {
                g.FillPath(back, path);
            }

            using var labelBrush = new SolidBrush(ParseColor(_settings.LabelColor, Color.White));
            using var valueBrush = new SolidBrush(ParseColor(_settings.TextColor, Color.Lime));
            using var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0));

            float y = pad;

            // 每格画一组 label + value
            void DrawCell(Row r, float x, float top)
            {
                if (r.Label.Length > 0)
                {
                    g.DrawString(r.Label, normalFont, shadow, x + 1f, top + 1f);   // 描影，保证亮背景上可读
                    g.DrawString(r.Label, normalFont, labelBrush, x, top);
                }
                float vx = x + labelW + gap;
                g.DrawString(r.Value, normalFont, shadow, vx + 1f, top + 1f);
                g.DrawString(r.Value, normalFont, valueBrush, vx, top);
            }

            int i = 0;
            while (i < rows.Count)
            {
                var r = rows[i];

                // 标题（游戏名）永远独占一整行
                if (r.IsTitle)
                {
                    DrawCell(r, pad, y);
                    y += lineH;
                    i++;
                    continue;
                }

                DrawCell(r, pad, y);

                // 双列且下一个不是标题时，同一行右侧再放一项
                if (twoCol && i + 1 < rows.Count && !rows[i + 1].IsTitle)
                {
                    DrawCell(rows[i + 1], pad + cellW + colGap, y);
                    i += 2;
                }
                else
                {
                    i++;
                }
                y += lineH;
            }

            float chartY = pad + lineH * visualLines + chartGap;

            if (chartH > 0)
            {
                DrawMiniChart(g, s.RecentFrameTimes,
                    s.Low1PercentHistory, s.Low01PercentHistory,
                    _settings.ShowOverlayLow1, _settings.ShowOverlayLow01,
                    pad, chartY,
                    width - pad * 2, chartH,
                    ParseColor(_settings.TextColor, Color.Lime));

                chartY += chartH + chartGap;
            }

            if (ftChartH > 0)
            {
                DrawFrameTimeMiniChart(g, s.RecentFrameTimes,
                    pad, chartY, width - pad * 2, ftChartH, Color.FromArgb(100, 210, 255));
            }
        }

        PushToScreen(bmp, _settings.X, _settings.Y);

        if (!Visible)
        {
            Show();
            OverlayVisible = true;
        }
    }

    /// <summary>
    /// 悬浮窗底部的迷你帧率曲线，可叠加 1% / 0.1% Low 两条线（共用 FPS 纵轴）。
    /// 纵轴用 P99 定位而不是最大值，否则偶发的异常尖峰会把正常数据压成贴底的一条线。
    /// </summary>
    private static void DrawMiniChart(Graphics g, double[] frameTimes,
        double[] low1, double[] low01, bool drawLow1, bool drawLow01,
        float x, float y, float w, float h, Color color)
    {
        if (w <= 4f || h <= 4f) return;

        using (var bg = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillRectangle(bg, x, y, w, h);

        if (frameTimes == null || frameTimes.Length < 2) return;

        int n = frameTimes.Length;
        var fps = new double[n];
        for (int i = 0; i < n; i++)
            fps[i] = frameTimes[i] > 0 ? 1000.0 / frameTimes[i] : 0;

        // 纵轴要能容纳所有要画的线
        double peak = Percentile(fps, 0.99);
        if (drawLow1 && low1 != null && low1.Length > 0) peak = Math.Max(peak, Percentile(low1, 0.99));
        if (drawLow01 && low01 != null && low01.Length > 0) peak = Math.Max(peak, Percentile(low01, 0.99));

        double yMax = Math.Max(60.0, peak);

        var pts = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            float px = x + w * i / (n - 1);
            double v = Math.Clamp(fps[i], 0, yMax);
            float py = y + h - (float)(h * v / yMax);
            pts[i] = new PointF(px, py);
        }

        using var pen = new Pen(color, Math.Max(1f, h / 36f));
        g.DrawLines(pen, pts);

        // 低帧率曲线：每秒一个采样点，铺满整条宽度
        if (drawLow1) DrawSeries(g, low1, x, y, w, h, yMax, Color.FromArgb(255, 180, 84), h / 34f);
        if (drawLow01) DrawSeries(g, low01, x, y, w, h, yMax, Color.FromArgb(180, 140, 255), h / 36f);
    }

    /// <summary>单独的帧时间迷你曲线（ms 轴，越低越好）。</summary>
    private static void DrawFrameTimeMiniChart(Graphics g, double[] frameTimes,
        float x, float y, float w, float h, Color color)
    {
        if (w <= 4f || h <= 4f) return;

        using (var bg = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillRectangle(bg, x, y, w, h);

        if (frameTimes == null || frameTimes.Length < 2) return;

        int n = frameTimes.Length;
        var ft = new double[n];
        for (int i = 0; i < n; i++)
            ft[i] = Math.Clamp(frameTimes[i], 0, 2000);

        double yMax = Math.Max(8.0, Percentile(ft, 0.99));

        var pts = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            float px = x + w * i / (n - 1);
            float py = y + h - (float)(h * Math.Clamp(ft[i], 0, yMax) / yMax);
            pts[i] = new PointF(px, py);
        }

        using var pen = new Pen(color, Math.Max(1f, h / 36f));
        g.DrawLines(pen, pts);
    }

    /// <summary>把一组每秒采样值画成铺满宽度的折线。</summary>
    private static void DrawSeries(Graphics g, double[]? series,
        float x, float y, float w, float h, double yMax, Color color, float thickness)
    {
        if (series == null || series.Length < 2 || yMax <= 0) return;

        var pts = new PointF[series.Length];
        for (int i = 0; i < series.Length; i++)
        {
            float px = x + w * i / (series.Length - 1);
            float py = y + h - (float)(h * Math.Clamp(series[i], 0, yMax) / yMax);
            pts[i] = new PointF(px, py);
        }

        using var pen = new Pen(color, Math.Max(1f, thickness));
        g.DrawLines(pen, pts);
    }

    private static double Percentile(double[] values, double p)
    {
        if (values == null || values.Length == 0) return 0;

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int idx = Math.Clamp((int)(sorted.Length * p), 0, sorted.Length - 1);
        return sorted[idx];
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0.5f) { p.AddRectangle(r); return p; }
        float d = radius * 2f;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch { return fallback; }
    }

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

            // UpdateLayeredWindow 要求预乘 Alpha：把 GDI+ 的非预乘像素手工乘上去
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
