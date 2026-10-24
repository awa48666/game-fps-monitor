using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GameFPS.Backend;

/// <summary>
/// 开机自启：写当前用户的注册表 Run 项。
///
/// 用 HKCU 而不是 HKLM 或计划任务，理由有三：
///   · HKCU 不需要管理员权限，避免为一个开关再弹一次 UAC；
///   · 计划任务要 CreateTask 权限，且卸载时容易留垃圾；
///   · 本项目本身要求以管理员运行，写入 Run 项后 Windows 会在登录时
///     直接按清单里的 requireAdministrator 提权启动它。
///
/// 值名固定为 GameFPS，值内容是带引号的 exe 完整路径 —— 路径里有空格时
/// 不加引号会被拆成两个参数，这是 Run 项最常见的坑。
/// </summary>
internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GameFPS";

    /// <summary>当前是否已登记开机自启（以注册表实际状态为准，不看设置文件）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return false;

            string? value = key.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value)) return false;

            // 值为空或指向别的路径都算没开：可能用户挪过程序目录，
            // 这时旧路径已经失效，应该让开关显示成关闭状态
            string expected = BuildCommand();
            return string.Equals(value.Trim('"'), expected.Trim('"'), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 打开/关闭开机自启。返回实际写入后的状态 —— 写失败时返回 false，
    /// 调用方据此把界面开关回弹，而不是假装成功。
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (enabled)
                key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, false);

            return IsEnabled() == enabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 启动命令。发布版是 GameFPS.exe 自身；开发期拿 dotnet 跑时
    /// （进程名是 dotnet.exe）就退回 exe 路径，至少不会写一条指向
    /// dotnet.exe 却不带参数的错误命令。
    /// </summary>
    private static string BuildCommand()
    {
        string exe = Application.ExecutablePath;
        return "\"" + exe + "\"";
    }
}
