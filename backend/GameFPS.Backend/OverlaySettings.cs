using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameFPS.Backend;

/// <summary>悬浮窗网格里的一个数据块。</summary>
public sealed class OverlayBlock
{
    /// <summary>块类型，如 fps / frametime / cpu / curve / ftcurve。</summary>
    public string Type { get; set; } = "";

    /// <summary>所在行（0 起）。</summary>
    public int Row { get; set; }

    /// <summary>所在列（0 起）。</summary>
    public int Col { get; set; }
}

/// <summary>悬浮窗 / 界面设置，持久化为 JSON。</summary>
public sealed class OverlaySettings
{
    // ---- 悬浮窗外观 ----
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>
    /// 悬浮窗样式：1 = 竖排列表（每项一行），2 = 横排单行。
    /// </summary>
    public int OverlayStyle { get; set; } = 1;

    /// <summary>检测到游戏时自动显示悬浮窗。</summary>
    public bool AutoShowOnGame { get; set; } = true;
    public int X { get; set; } = 24;
    public int Y { get; set; } = 24;
    public double Scale { get; set; } = 1.0;
    public string FontFamily { get; set; } = "微软雅黑";
    public string TextColor { get; set; } = "#6EF3C0";
    public string LabelColor { get; set; } = "#D8E6F2";
    public string BackColor { get; set; } = "#0B0F14";
    /// <summary>背景不透明度 0-255。</summary>
    public int BackOpacity { get; set; } = 150;
    public int CornerRadius { get; set; } = 10;
    public int RowGap { get; set; } = 4;

    // ---- 悬浮窗显示项 ----
    public bool ShowGameName { get; set; } = true;
    public bool ShowFps { get; set; } = true;
    public bool ShowFrameTime { get; set; } = true;
    public bool ShowLow1 { get; set; } = true;
    public bool ShowLow01 { get; set; } = false;
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowMemory { get; set; } = false;
    public bool ShowGpuMemory { get; set; } = false;

    // ---- 频率 / 功耗 / 大小核 ----
    public bool ShowCpuFrequency { get; set; } = false;
    public bool ShowGpuFrequency { get; set; } = false;
    public bool ShowCpuPower { get; set; } = false;
    public bool ShowGpuPower { get; set; } = false;

    /// <summary>显示大核(P-core)的占用率与频率。</summary>
    public bool ShowPCore { get; set; } = false;

    /// <summary>显示小核(E-core)的占用率与频率。</summary>
    public bool ShowECore { get; set; } = false;

    /// <summary>在 CPU 行末显示温度（ACPI 热区，非核心温度）。</summary>
    public bool ShowCpuTemperature { get; set; } = false;

    /// <summary>在 GPU 行末显示核心温度。</summary>
    public bool ShowGpuTemperature { get; set; } = false;

    /// <summary>在显存行末显示显存温度（仅部分显卡支持）。</summary>
    public bool ShowVramTemperature { get; set; } = false;

    /// <summary>在悬浮窗底部画一条迷你帧率曲线。</summary>
    public bool ShowOverlayChart { get; set; } = true;

    /// <summary>在迷你曲线里叠加 1% Low 线（与帧率共用 FPS 纵轴）。</summary>
    public bool ShowOverlayLow1 { get; set; } = false;

    /// <summary>在迷你曲线里叠加 0.1% Low 线。</summary>
    public bool ShowOverlayLow01 { get; set; } = false;

    /// <summary>额外画一条帧时间曲线（ms 轴，单独一块）。</summary>
    public bool ShowOverlayFrameTime { get; set; } = false;

    /// <summary>迷你曲线高度（像素，会随缩放一起放大）。</summary>
    public int OverlayChartHeight { get; set; } = 40;

    /// <summary>
    /// 竖排样式下各显示项的顺序，存的是键名（如 "fps"、"cpu"、"frametime"）。
    /// 空列表 = 用内置默认顺序。用户手动调整顺序后写入这里。
    /// </summary>
    public List<string> OverlayOrder { get; set; } = new();

    // ---- 自由网格布局（在设置页拖拽编排）----
    /// <summary>
    /// 网格里已放置的数据块。列表为空时退回 OverlayStyle 的固定布局，
    /// 保证老配置仍然可用。
    /// </summary>
    public List<OverlayBlock> OverlayBlocks { get; set; } = new();

    /// <summary>网格行数（决定有多少个虚线空槽可拖入）。</summary>
    public int GridRows { get; set; } = 3;

    /// <summary>网格列数。</summary>
    public int GridCols { get; set; } = 1;

    // ---- 热键 ----
    /// <summary>显示/隐藏悬浮窗的热键，如 F9 / Ctrl+Alt+F。</summary>
    public string HotkeyToggleOverlay { get; set; } = "F9";
    /// <summary>打开主界面的热键。</summary>
    public string HotkeyOpenMain { get; set; } = "Shift+F9";

    // ---- 监测目标 ----
    /// <summary>手动指定要监测的进程，关闭"自动检测前台渲染进程"。</summary>
    public bool ManualTargetEnabled { get; set; } = false;

    /// <summary>手动指定的进程名（可带或省略 .exe）。</summary>
    public string ManualTargetName { get; set; } = "";

    // ---- 游戏记录 ----
    /// <summary>游戏退出时自动保存并记录本场帧率数据。</summary>
    public bool RecordSessions { get; set; } = true;
    /// <summary>
    /// 兜底：连续多久没有新帧就把本场结算（秒）。
    /// 0 = 关闭，只认"进程彻底退出"。默认关闭，保证一整局记录完整。
    /// </summary>
    public int SessionIdleSeconds { get; set; } = 0;
    /// <summary>除了汇总，再写一份逐帧 CSV 到 records\csv\。</summary>
    public bool WriteFrameCsv { get; set; } = true;
    /// <summary>sessions.json 里最多保留多少条历史。</summary>
    public int MaxSessionsInHistory { get; set; } = 300;

    // ---- 其它 ----
    public bool StartMinimizedToTray { get; set; } = true;
    public bool LaunchElectronUi { get; set; } = true;

    [JsonIgnore]
    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static OverlaySettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(path));
                if (s != null) return s;
            }
        }
        catch { /* 配置损坏时回退默认值 */ }
        return new OverlaySettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch { /* 只读目录下静默失败 */ }
    }
}
