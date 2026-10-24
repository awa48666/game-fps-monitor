using System;

namespace GameFPS.Backend;

/// <summary>
/// 屏幕准星的参数。字段含义与绘制逻辑一一对应（见 CrosshairForm.RenderBitmap）：
/// 尺寸类参数单位都是物理像素，缩放由用户直接调 Size/Thickness 控制。
/// </summary>
public sealed class CrosshairSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 样式：Cross / Dot / Circle / CrossDot / CircleCross / DotBox。
    /// 未知值按 Cross 处理。
    /// </summary>
    public string Style { get; set; } = "Cross";

    /// <summary>十字的臂长 / 圆环半径 / 方框半边长。</summary>
    public int Size { get; set; } = 20;

    /// <summary>线条粗细。</summary>
    public int Thickness { get; set; } = 2;

    /// <summary>主色，形如 #FF0000。</summary>
    public string Color { get; set; } = "#FF0000";

    /// <summary>中心留空半径，0 = 四臂在中心相接。</summary>
    public int Gap { get; set; } = 0;

    /// <summary>中心点的直径（Dot / CrossDot / DotBox 使用）。</summary>
    public int DotSize { get; set; } = 2;

    /// <summary>整体不透明度 0-255。</summary>
    public int Opacity { get; set; } = 255;

    /// <summary>相对屏幕中心的水平偏移，正数向右。</summary>
    public int OffsetX { get; set; } = 0;

    /// <summary>相对屏幕中心的垂直偏移，正数向下。</summary>
    public int OffsetY { get; set; } = 0;

    /// <summary>显示器序号；-1 = 主显示器。</summary>
    public int MonitorIndex { get; set; } = -1;

    /// <summary>
    /// true = 用图片做准星，false = 按 Style 程序绘制。
    /// 图片缺失或解码失败时自动退回程序绘制，不会留下一个看不见的准星。
    /// </summary>
    public bool UseCustomImage { get; set; } = false;

    /// <summary>图片准星的本地绝对路径（PNG / JPG / BMP / GIF）。</summary>
    public string? CustomImagePath { get; set; }

    /// <summary>图片宽度作为准星边长；非正方形图片会被拉伸填满这个正方形。</summary>
    public int ImageSize { get; set; } = 64;

    /// <summary>
    /// 按住模式：开启后准星只在按住 HoldKey 时出现（松手即隐藏），
    /// 适合只在开镜/架枪时看准星的用法。
    /// </summary>
    public bool HoldEnabled { get; set; } = false;

    /// <summary>按住模式的按键，支持键盘键（F8 / K）与鼠标键（Mouse1..Mouse5）。</summary>
    public string HoldKey { get; set; } = "Mouse2";

    /// <summary>按下后延迟多少毫秒才显示，0 = 立即显示（上限 10000）。</summary>
    public int HoldDelayMs { get; set; } = 0;

    /// <summary>描边（放在准星本体下面，让准星在浅色场景里也能看清）。</summary>
    public bool OutlineEnabled { get; set; } = false;

    public string OutlineColor { get; set; } = "#000000";

    /// <summary>描边比本体每侧多出的宽度。</summary>
    public int OutlineThickness { get; set; } = 1;

    /// <summary>
    /// 显示/隐藏准星的全局热键。默认 F8 —— F9 与 Shift+F9 已被悬浮窗和主界面占用。
    /// </summary>
    public string HotkeyToggle { get; set; } = "F8";
}
