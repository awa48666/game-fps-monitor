using System;
using System.IO;

namespace GameFPS.Backend;

/// <summary>
/// 应用数据目录解析。
///
/// 关键点：设置和帧率记录**不能**放在程序目录（AppContext.BaseDirectory）。
/// 用户更新版本时通常是整包替换程序目录，放在里面的数据会被一起删掉。
/// 统一存到 %LOCALAPPDATA%\GameFPSMonitor 后，覆盖更新不会影响历史记录。
/// </summary>
public static class AppPaths
{
    /// <summary>数据根目录：%LOCALAPPDATA%\GameFPSMonitor</summary>
    public static string DataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameFPSMonitor");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string RecordsDir => Path.Combine(DataDir, "records");

    /// <summary>
    /// 把旧版本留在程序目录里的 settings.json 和 records\ 搬到新位置。
    /// 只在新位置还没有对应内容时才搬，避免覆盖用户已有数据。
    /// 搬家失败不影响启动（顶多是数据留在老地方）。
    /// </summary>
    public static void MigrateFromLegacy(string? legacyRoot = null)
    {
        legacyRoot ??= AppContext.BaseDirectory;

        try
        {
            Directory.CreateDirectory(DataDir);

            // settings.json
            string oldSettings = Path.Combine(legacyRoot, "settings.json");
            if (File.Exists(oldSettings) && !File.Exists(SettingsFile))
                File.Copy(oldSettings, SettingsFile, false);

            // records 目录
            string oldRecords = Path.Combine(legacyRoot, "records");
            if (Directory.Exists(oldRecords) && !Directory.Exists(RecordsDir))
                CopyDirectory(oldRecords, RecordsDir);
        }
        catch
        {
            // 数据目录不可写之类的情况，静默跳过，程序照常以默认配置运行
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);

        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
