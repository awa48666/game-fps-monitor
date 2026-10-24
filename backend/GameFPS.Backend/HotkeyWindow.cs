using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameFPS.Backend;

/// <summary>不可见的消息宿主窗口，只用来接收全局热键 WM_HOTKEY。</summary>
public sealed class HotkeyWindow : Form
{
    private const int WM_HOTKEY = 0x0312;
    private const int IdToggleOverlay = 0x5101;
    private const int IdOpenMain = 0x5102;
    private const int IdToggleCrosshair = 0x5103;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action? ToggleOverlay;
    public event Action? OpenMain;
    public event Action? ToggleCrosshair;

    /// <summary>热键注册失败时触发：(热键文本, 原因)。</summary>
    public event Action<string, string>? HotkeyFailed;

    private bool _toggleRegistered;
    private bool _openRegistered;
    private bool _crosshairRegistered;

    public HotkeyWindow(OverlaySettings settings)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        _ = Handle;              // 立刻建句柄，之后才能收 WM_HOTKEY
        Apply(settings);
    }

    /// <summary>该宿主窗口永不真正显示。</summary>
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    public void Apply(OverlaySettings settings)
    {
        // RegisterHotKey / UnregisterHotKey 必须在本窗口所属的 UI 线程上调用。
        // IPC 回调跑在线程池线程上，直接调用会静默失败（返回 false，且旧的注册也解不掉），
        // 表现就是"改了热键没反应"。
        if (IsHandleCreated && InvokeRequired)
        {
            try { BeginInvoke(new Action(() => ApplyCore(settings))); }
            catch (InvalidOperationException) { }
            return;
        }
        ApplyCore(settings);
    }

    private void ApplyCore(OverlaySettings settings)
    {
        Unregister();

        var (mods, vk) = Parse(settings.HotkeyToggleOverlay);
        if (vk != 0) _toggleRegistered = RegisterHotKey(Handle, IdToggleOverlay, mods | MOD_NOREPEAT, vk);
        // 错误码必须紧跟着 RegisterHotKey 取，之后任何 API 调用都会把它覆盖掉
        int toggleErr = _toggleRegistered ? 0 : Marshal.GetLastWin32Error();
        Log($"toggle '{settings.HotkeyToggleOverlay}' -> mods=0x{mods:X} vk=0x{vk:X} ok={_toggleRegistered} err={toggleErr}");
        if (vk != 0 && !_toggleRegistered) Fail(settings.HotkeyToggleOverlay, toggleErr);

        (mods, vk) = Parse(settings.HotkeyOpenMain);
        if (vk != 0) _openRegistered = RegisterHotKey(Handle, IdOpenMain, mods | MOD_NOREPEAT, vk);
        int openErr = _openRegistered ? 0 : Marshal.GetLastWin32Error();
        Log($"openmain '{settings.HotkeyOpenMain}' -> mods=0x{mods:X} vk=0x{vk:X} ok={_openRegistered} err={openErr}");
        if (vk != 0 && !_openRegistered) Fail(settings.HotkeyOpenMain, openErr);

        // 准星热键。老配置文件里可能没有 crosshair 子树，这里按空串处理（不注册）。
        string crosshair = settings.Crosshair?.HotkeyToggle ?? "";
        (mods, vk) = Parse(crosshair);
        if (vk != 0) _crosshairRegistered = RegisterHotKey(Handle, IdToggleCrosshair, mods | MOD_NOREPEAT, vk);
        int crosshairErr = _crosshairRegistered ? 0 : Marshal.GetLastWin32Error();
        Log($"crosshair '{crosshair}' -> mods=0x{mods:X} vk=0x{vk:X} ok={_crosshairRegistered} err={crosshairErr}");
        if (vk != 0 && !_crosshairRegistered) Fail(crosshair, crosshairErr);
    }

    /// <summary>注册失败（多半是被别的程序占了）时通知上层，界面好给提示。</summary>
    private void Fail(string hotkey, int errorCode)
    {
        string reason = errorCode == 1409 ? "已被其他程序占用" : $"注册失败（错误码 {errorCode}）";
        HotkeyFailed?.Invoke(hotkey, reason);
    }

    private static void Log(string msg)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "hotkey.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private void Unregister()
    {
        if (_toggleRegistered)
        {
            try { UnregisterHotKey(Handle, IdToggleOverlay); } catch { }
            _toggleRegistered = false;
        }
        if (_openRegistered)
        {
            try { UnregisterHotKey(Handle, IdOpenMain); } catch { }
            _openRegistered = false;
        }
        if (_crosshairRegistered)
        {
            try { UnregisterHotKey(Handle, IdToggleCrosshair); } catch { }
            _crosshairRegistered = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case IdToggleOverlay: ToggleOverlay?.Invoke(); break;
                case IdOpenMain: OpenMain?.Invoke(); break;
                case IdToggleCrosshair: ToggleCrosshair?.Invoke(); break;
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>解析 "F9" / "Shift+F9" / "Ctrl+Alt+F" / "Ctrl+1" / "Alt+Space" 这类热键描述。</summary>
    private static (uint modifiers, uint vk) Parse(string text)
    {
        uint mods = 0, vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return (0, 0);

        // 特殊键名 -> 虚拟键码
        var named = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPACE"] = 0x20, ["TAB"] = 0x09, ["ENTER"] = 0x0D, ["BACKSPACE"] = 0x08,
            ["INSERT"] = 0x2D, ["DELETE"] = 0x2E, ["HOME"] = 0x24, ["END"] = 0x23,
            ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22,
            ["UP"] = 0x26, ["DOWN"] = 0x28, ["LEFT"] = 0x25, ["RIGHT"] = 0x27,
            ["MINUS"] = 0xBD, ["EQUAL"] = 0xBB, ["COMMA"] = 0xBC, ["PERIOD"] = 0xBE,
            ["SLASH"] = 0xBF, ["SEMICOLON"] = 0xBA, ["QUOTE"] = 0xDE,
            ["LBRACKET"] = 0xDB, ["RBRACKET"] = 0xDD, ["BACKSLASH"] = 0xDC, ["BACKQUOTE"] = 0xC0,
        };

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL":
                case "CONTROL": mods |= MOD_CONTROL; continue;
                case "ALT": mods |= MOD_ALT; continue;
                case "SHIFT": mods |= MOD_SHIFT; continue;
                case "WIN": mods |= MOD_WIN; continue;
            }

            if (part.Length == 1 && part[0] >= 'A' && part[0] <= 'Z')
            {
                vk = part[0];
            }
            else if (part.Length == 1 && part[0] >= '0' && part[0] <= '9')
            {
                vk = (uint)part[0];                       // '0'..'9' 的 VK 就是 ASCII 码
            }
            else if (part.Length == 4 && part.StartsWith("NUM", StringComparison.Ordinal)
                     && part[3] >= '0' && part[3] <= '9')
            {
                vk = 0x60 + (uint)(part[3] - '0');        // 小键盘 0-9
            }
            else if (part.Length >= 2 && part[0] == 'F'
                     && int.TryParse(part[1..], out int n) && n is >= 1 and <= 24)
            {
                vk = (uint)(0x70 + n - 1);
            }
            else if (named.TryGetValue(part, out uint special))
            {
                vk = special;
            }
        }

        return (mods, vk);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Unregister();
        base.OnHandleDestroyed(e);
    }
}
