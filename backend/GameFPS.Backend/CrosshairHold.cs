using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameFPS.Backend;

/// <summary>
/// 准星「按住才显示」模式：按住绑定的键（键盘键或鼠标键）时准星出现，
/// 松开立即隐藏；可配置按下后延迟多少毫秒才出现。
///
/// 实现选型：轮询 GetAsyncKeyState 而不是 WH_KEYBOARD_LL / WH_MOUSE_LL 低层钩子。
/// 低层钩子在每个输入事件上都要回调本进程一次，会给游戏里的每一次按键/点击
/// 增加一次同步等待，也容易撞上反作弊对 SetWindowsHookEx 的关注；GetAsyncKeyState
/// 只读全局按键状态位，不插入输入链，代价是最多 16 ms 的采样延迟 —— 对"按住显示"
/// 这种场景完全无感。
/// </summary>
public sealed class CrosshairHold : IDisposable
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int PollMs = 16;

    private readonly CrosshairForm? _form;
    private readonly System.Windows.Forms.Timer? _timer;

    private bool _enabled;
    private int _vk = -1;
    private string _key = "";
    private int _delayMs;
    private bool _down;
    private long _downAt;
    private bool _shown;

    public CrosshairHold(CrosshairForm? form)
    {
        _form = form;
        if (_form == null) return;

        // Timer 必须在有消息循环的线程上创建（AppHost 建在 WinForms 主线程）
        _timer = new System.Windows.Forms.Timer { Interval = PollMs };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>同步参数。参数没变时没有任何副作用，可以每次 ApplySettings 都调。</summary>
    public void Apply(CrosshairSettings s)
    {
        string newKey = s.HoldKey ?? "";
        string oldKey = _key;

        _enabled = s.HoldEnabled;
        _delayMs = Math.Clamp(s.HoldDelayMs, 0, 10_000);

        bool keyChanged = !string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase);
        if (keyChanged)
        {
            _key = newKey;
            _vk = ParseKey(newKey);
        }

        // 关掉按住模式、或换了键：把残留的"显示中"收回来，避免准星永远挂在那里
        if (!_enabled || keyChanged)
        {
            _down = false;
            if (_shown) SetShown(false);
        }
    }

    private void Poll()
    {
        if (!_enabled || _vk < 0) return;

        bool down;
        try
        {
            down = (GetAsyncKeyState(_vk) & 0x8000) != 0;
        }
        catch
        {
            return;
        }

        if (down && !_down)
        {
            _down = true;
            _downAt = Environment.TickCount64;
            if (_delayMs == 0) SetShown(true);
        }
        else if (down && !_shown)
        {
            if (Environment.TickCount64 - _downAt >= _delayMs) SetShown(true);
        }
        else if (!down && _down)
        {
            _down = false;
            if (_shown) SetShown(false);
        }
    }

    private void SetShown(bool show)
    {
        _shown = show;
        _form?.SetHoldActive(show);
    }

    /// <summary>
    /// 绑定串 → 虚拟键码。支持 Mouse1..Mouse5（左/右/中/侧键）与键盘键
    /// （单字符 A-Z / 0-9、F1-F24，其余交给 Keys 枚举解析）。解析不出返回 -1。
    /// </summary>
    private static int ParseKey(string key)
    {
        string t = key.Trim();
        if (t.Length == 0) return -1;

        switch (t.ToLowerInvariant())
        {
            case "mouse1": return 0x01;   // VK_LBUTTON
            case "mouse2": return 0x02;   // VK_RBUTTON
            case "mouse3": return 0x04;   // VK_MBUTTON
            case "mouse4": return 0x05;   // VK_XBUTTON1
            case "mouse5": return 0x06;   // VK_XBUTTON2
        }

        if (t.Length == 1)
        {
            char c = char.ToUpperInvariant(t[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
        }

        if ((t[0] == 'F' || t[0] == 'f') && int.TryParse(t[1..], out int fn) && fn >= 1 && fn <= 24)
            return 0x70 + (fn - 1);       // VK_F1 = 0x70

        if (Enum.TryParse<Keys>(t, true, out var k) && (int)k > 0) return (int)k;

        return -1;
    }

    public void Dispose()
    {
        try
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
            }
            if (_shown) SetShown(false);
        }
        catch { }
    }
}
