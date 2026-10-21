using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GameFPS.Core;

/// <summary>
/// 把每场游戏的帧率记录落盘：
///   records\sessions.json —— 每场一条汇总（最新在前），供界面读历史
///   records\csv\*.csv     —— 逐帧帧时间，供事后用 Excel 等做同口径分析
/// </summary>
public sealed class SessionRecorder
{
    private const string HistoryFileName = "sessions.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,          // SessionRecord 用的是公共字段
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();

    public SessionRecorder(string rootDir)
    {
        RootDir = rootDir;
        CsvDir = Path.Combine(rootDir, "csv");
        try
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(CsvDir);
        }
        catch { /* 目录建不出来时 Save 会各自静默失败 */ }
    }

    public string RootDir { get; }
    public string CsvDir { get; }
    public string HistoryFile => Path.Combine(RootDir, HistoryFileName);

    /// <summary>落盘一场会话，返回汇总记录。</summary>
    public SessionRecord Save(GameSession session, bool writeCsv, int maxHistory)
    {
        var record = session.BuildRecord(DateTime.Now);

        if (writeCsv && record.SampleCount > 1)
        {
            try { record.CsvFile = WriteCsv(session, record); }
            catch { /* CSV 写失败不影响汇总落盘 */ }
        }

        try { AppendHistory(record, maxHistory); }
        catch { }

        return record;
    }

    private string WriteCsv(GameSession session, SessionRecord rec)
    {
        Directory.CreateDirectory(CsvDir);      // 目录可能被外部清理掉，写前再确保一次

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string file = Path.Combine(CsvDir, $"{stamp}_{Sanitize(rec.GameName)}_pid{rec.Pid}.csv");

        var frames = session.FrameTimesSnapshot();
        var sb = new StringBuilder(frames.Length * 24 + 512);

        // 以 # 开头的是注释行，便于人工查看，也方便解析器跳过
        sb.Append("# GameFPS 逐帧记录\n");
        sb.Append("# game,").Append(rec.GameName).Append('\n');
        sb.Append("# path,").Append(rec.GamePath).Append('\n');
        sb.Append("# started,").Append(rec.StartedAt).Append('\n');
        sb.Append("# ended,").Append(rec.EndedAt).Append('\n');
        sb.Append("# duration_sec,").Append(rec.DurationSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("# average_fps,").Append(rec.AverageFps.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("# low1_percent_fps,").Append(rec.Low1PercentFps.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("# low01_percent_fps,").Append(rec.Low01PercentFps.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("frame_index,frame_time_ms,instant_fps\n");

        for (int i = 0; i < frames.Length; i++)
        {
            double ms = frames[i];
            sb.Append(i + 1).Append(',')
              .Append(ms.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
              .Append((ms > 0 ? 1000.0 / ms : 0).ToString("F2", CultureInfo.InvariantCulture))
              .Append('\n');
        }

        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        return Path.GetFileName(file);
    }

    private void AppendHistory(SessionRecord record, int maxHistory)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(RootDir);   // 同上：目录可能已被外部删除

            var list = LoadHistoryUnlocked();
            list.Insert(0, record);                 // 最新的排在最前
            if (maxHistory > 0 && list.Count > maxHistory)
                list.RemoveRange(maxHistory, list.Count - maxHistory);

            File.WriteAllText(HistoryFile,
                JsonSerializer.Serialize(list, JsonOpts), new UTF8Encoding(false));
        }
    }

    /// <summary>读取历史汇总（最新在前）。</summary>
    public List<SessionRecord> LoadHistory(int max = 0)
    {
        lock (_lock)
        {
            var list = LoadHistoryUnlocked();
            return max > 0 && list.Count > max ? list.GetRange(0, max) : list;
        }
    }

    /// <summary>
    /// 删除一条历史，连同它对应的逐帧 CSV。
    /// SessionRecord 没有唯一 id，所以用「开始时间 + 结束时间 + 游戏名 + CSV 文件名」
    /// 组合定位；这四项足以区分同一款游戏的不同场次。
    /// </summary>
    public bool DeleteSession(SessionRecord target)
    {
        if (target == null) return false;

        lock (_lock)
        {
            var list = LoadHistoryUnlocked();
            int idx = list.FindIndex(r =>
                r.StartedAt == target.StartedAt &&
                r.EndedAt == target.EndedAt &&
                r.GameName == target.GameName &&
                r.CsvFile == target.CsvFile);

            if (idx < 0) return false;

            var removed = list[idx];
            list.RemoveAt(idx);

            try
            {
                Directory.CreateDirectory(RootDir);
                File.WriteAllText(HistoryFile,
                    JsonSerializer.Serialize(list, JsonOpts), new UTF8Encoding(false));
            }
            catch
            {
                return false;
            }

            DeleteCsv(removed.CsvFile);
            return true;
        }
    }

    /// <summary>清空全部历史与逐帧 CSV，返回删掉的记录条数。</summary>
    public int ClearHistory()
    {
        lock (_lock)
        {
            int count = LoadHistoryUnlocked().Count;

            try
            {
                // 直接写空列表覆盖，比先删再建更稳（避免删失败后留下旧数据）
                Directory.CreateDirectory(RootDir);
                File.WriteAllText(HistoryFile,
                    JsonSerializer.Serialize(new List<SessionRecord>(), JsonOpts),
                    new UTF8Encoding(false));
            }
            catch { }

            try
            {
                if (Directory.Exists(CsvDir))
                {
                    foreach (var f in Directory.GetFiles(CsvDir, "*.csv"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }

            return count;
        }
    }

    private void DeleteCsv(string? csvFile)
    {
        if (string.IsNullOrWhiteSpace(csvFile)) return;
        try
        {
            // 只取文件名，避免路径穿越
            var path = Path.Combine(CsvDir, Path.GetFileName(csvFile));
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private List<SessionRecord> LoadHistoryUnlocked()
    {
        try
        {
            if (!File.Exists(HistoryFile)) return new List<SessionRecord>();
            var json = File.ReadAllText(HistoryFile, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<SessionRecord>>(json, JsonOpts)
                   ?? new List<SessionRecord>();
        }
        catch
        {
            return new List<SessionRecord>();      // 文件损坏时当作空历史，不阻断运行
        }
    }

    /// <summary>
    /// 读取某一场的逐帧帧时间(ms)，供界面绘制该场的帧率曲线。
    /// 点数过多时等距降采样，避免几十万帧的传输与绘制压力。
    /// </summary>
    public double[] LoadFrameTimes(string csvFileName, int maxPoints = 4000)
    {
        if (string.IsNullOrWhiteSpace(csvFileName)) return Array.Empty<double>();

        // 只取文件名，避免路径穿越
        string path = Path.Combine(CsvDir, Path.GetFileName(csvFileName));
        if (!File.Exists(path)) return Array.Empty<double>();

        var list = new List<double>();
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("frame_index", StringComparison.Ordinal)) continue;

                var parts = line.Split(',');
                if (parts.Length < 2) continue;

                if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) && ms > 0)
                    list.Add(ms);
            }
        }
        catch
        {
            return Array.Empty<double>();
        }

        if (list.Count <= maxPoints) return FilterOutliers(list).ToArray();

        var cleaned = FilterOutliers(list);
        if (cleaned.Count <= maxPoints) return cleaned.ToArray();

        var sampled = new double[maxPoints];
        double step = (double)(cleaned.Count - 1) / (maxPoints - 1);
        for (int i = 0; i < maxPoints; i++)
            sampled[i] = cleaned[(int)Math.Round(i * step)];

        return sampled;
    }

    /// <summary>
    /// 过滤 ETW 重复上报产生的伪帧时间。
    /// 这类异常值只有正常帧的几十分之一（如 0.2ms ≈ 4673 FPS），
    /// 数量不多，但只要存在，画曲线时就会把纵轴整个撑爆，
    /// 导致真实数据被压成一条贴着底部的线。
    /// 用中位数做锚点比固定阈值稳妥：不同游戏/刷新率下的正常帧时间差别很大。
    /// </summary>
    private static List<double> FilterOutliers(List<double> values)
    {
        if (values.Count < 20) return values;

        var sorted = values.OrderBy(x => x).ToList();
        double median = sorted[sorted.Count / 2];
        if (median <= 0) return values;

        // 小于中位数 1/4 视为伪帧；同时给一个绝对下限 0.5ms(2000 FPS)，
        // 免得低帧率场景把中位数算小后误伤正常帧。
        double lowerBound = Math.Max(median * 0.25, 0.5);

        var result = values.Where(x => x >= lowerBound).ToList();
        return result.Count > 0 ? result : values;
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
