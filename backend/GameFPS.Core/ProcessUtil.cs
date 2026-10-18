using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameFPS.Core;

/// <summary>前台窗口与进程信息查询。</summary>
public static class ProcessUtil
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>当前前台窗口所属进程 PID；无前台窗口返回 0。</summary>
    public static int GetForegroundProcessId()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hWnd, out uint pid);
        return (int)pid;
    }

    public static string GetProcessName(int pid)
    {
        if (pid <= 0) return "";
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return ""; }
    }

    public static string GetProcessPath(int pid)
    {
        if (pid <= 0) return "";
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        catch { return ""; }
        finally { CloseHandle(h); }
    }

    /// <summary>该进程是否有可见的主窗口（用来把后台进程排除在“游戏”之外）。</summary>
    public static bool HasMainWindow(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainWindowHandle != IntPtr.Zero;
        }
        catch { return false; }
    }

    /// <summary>该 PID 是否仍然存在（用来判断游戏是不是已经退出了）。</summary>
    public static bool ProcessExists(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    /// <summary>判断是否是系统/自身进程，避免把桌面合成器当成游戏。</summary>
    public static bool IsSystemProcess(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        return name.Equals("dwm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("csrss", StringComparison.OrdinalIgnoreCase)
            || name.Equals("winlogon", StringComparison.OrdinalIgnoreCase)
            || name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Idle", StringComparison.OrdinalIgnoreCase)
            || name.Equals("GameFPS.Backend", StringComparison.OrdinalIgnoreCase);
    }
}
