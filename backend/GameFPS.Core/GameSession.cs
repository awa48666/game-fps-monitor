using System;
using System.Collections.Generic;
using System.Linq;

namespace GameFPS.Core;

/// <summary>
/// 一场游戏会话的累计统计。与 <see cref="FrameStats"/> 的滑动窗口不同，
/// 这里累积整场的逐帧帧时间，因此给出的是真正意义上的「全场 1% Low」，
/// 而不是最近几十秒窗口内的值。
/// </summary>
public sealed class GameSession
{
    /// <summary>逐帧样本上限，防止游戏挂后台忘了关时内存无限增长（float 约 8MB）。</summary>
    private const int MaxFramesKept = 2_000_000;

    /// <summary>
    /// 可信的最小帧时间。ETW 偶尔会对同一帧重复上报，产生 0.1ms 级别的伪间隔
    /// （换算成 7000+ FPS），这类值会把"最高 FPS"污染得毫无意义，直接丢弃。
    /// 0.2ms 相当于 5000 FPS，现实中不存在。
    /// </summary>
    private const double MinPlausibleFrameMs = 0.2;

    /// <summary>超过这个间隔视为切场景/暂停，不计入帧时间分布。</summary>
    private const double MaxPlausibleFrameMs = 2000;

    private readonly List<float> _frameTimes = new();
    private readonly object _lock = new();

    private long _frameCount;
    private double _sumMs;
    private float _minMs = float.MaxValue;
    private float _maxMs;

    public GameSession(int pid, double firstTsMs)
    {
        Pid = pid;
        StartedAt = DateTime.Now;
        LastFrameTsMs = firstTsMs;
    }

    public int Pid { get; }
    public DateTime StartedAt { get; }
    public double LastFrameTsMs { get; private set; }
    public string GameName { get; private set; } = "";
    public string GamePath { get; private set; } = "";

    /// <summary>样本达到上限后被截断，此时汇总只覆盖前 MaxFramesKept 帧。</summary>
    public bool Truncated { get; private set; }

    public long FrameCount { get { lock (_lock) return _frameCount; } }

    public void UpdateGameInfo(string name, string path)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(name)) GameName = name;
            if (!string.IsNullOrEmpty(path)) GamePath = path;
        }
    }

    /// <summary>
    /// 记录一帧。tsMs 是 ETW 单调时间轴上的时间戳；dtMs 是它与上一帧的间隔，
    /// &lt;= 0 表示首帧或无法确定间隔（不计入帧时间分布）。
    /// </summary>
    public void AddFrame(double tsMs, double dtMs)
    {
        lock (_lock)
        {
            LastFrameTsMs = tsMs;
            _frameCount++;

            if (dtMs < MinPlausibleFrameMs || dtMs >= MaxPlausibleFrameMs) return;

            if (_frameTimes.Count < MaxFramesKept) _frameTimes.Add((float)dtMs);
            else Truncated = true;

            _sumMs += dtMs;
            if (dtMs < _minMs) _minMs = (float)dtMs;
            if (dtMs > _maxMs) _maxMs = (float)dtMs;
        }
    }

    public double[] FrameTimesSnapshot()
    {
        lock (_lock) return _frameTimes.Select(x => (double)x).ToArray();
    }

    public SessionRecord BuildRecord(DateTime endedAt)
    {
        double[] arr;
        lock (_lock) arr = _frameTimes.Select(x => (double)x).ToArray();

        var rec = new SessionRecord
        {
            Pid = Pid,
            GameName = GameName,
            GamePath = GamePath,
            StartedAt = StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            EndedAt = endedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            DurationSeconds = Math.Round((endedAt - StartedAt).TotalSeconds, 1),
            FrameCount = FrameCount,
            Truncated = Truncated,
            SampleCount = arr.Length,
        };

        if (arr.Length == 0) return rec;

        var sorted = (double[])arr.Clone();
        Array.Sort(sorted);                          // 升序：最快在前，最慢在后

        double sum = 0;
        foreach (var v in arr) sum += v;
        double avg = sum / arr.Length;

        double fastest = sorted[0];
        double slowest = sorted[^1];

        rec.AvgFrameTimeMs = Math.Round(avg, 3);
        rec.MinFrameTimeMs = Math.Round(fastest, 3);
        rec.MaxFrameTimeMs = Math.Round(slowest, 3);
        rec.AverageFps = Math.Round(avg > 0 ? 1000.0 / avg : 0, 1);
        rec.MedianFps = Math.Round(sorted[sorted.Length / 2] > 0 ? 1000.0 / sorted[sorted.Length / 2] : 0, 1);
        rec.MaxFps = Math.Round(fastest > 0 ? 1000.0 / fastest : 0, 1);
        rec.MinFps = Math.Round(slowest > 0 ? 1000.0 / slowest : 0, 1);
        rec.Low1PercentFps = Math.Round(SlowestPercentFps(sorted, 0.01), 1);
        rec.Low01PercentFps = Math.Round(SlowestPercentFps(sorted, 0.001), 1);
        rec.StutterCount = arr.Count(v => v > avg * 2);   // 卡顿：帧时间超过平均两倍

        return rec;
    }

    /// <summary>最慢 p 比例帧的平均帧时间 → FPS（1% Low / 0.1% Low 的标准算法）。</summary>
    private static double SlowestPercentFps(double[] sortedAsc, double p)
    {
        int n = Math.Max(1, (int)Math.Ceiling(sortedAsc.Length * p));
        double sum = 0;
        for (int i = sortedAsc.Length - n; i < sortedAsc.Length; i++) sum += sortedAsc[i];
        double avg = sum / n;
        return avg > 0 ? 1000.0 / avg : 0;
    }
}

/// <summary>一场游戏结束时落盘的一条汇总记录。</summary>
public sealed class SessionRecord
{
    public int Pid;
    public string GameName = "";
    public string GamePath = "";
    public string StartedAt = "";
    public string EndedAt = "";
    public double DurationSeconds;
    public long FrameCount;

    /// <summary>参与统计的帧数（首帧与超长间隔不计入）。</summary>
    public long SampleCount;

    public double AverageFps;
    public double MedianFps;
    public double MinFps;
    public double MaxFps;
    public double AvgFrameTimeMs;
    public double MinFrameTimeMs;
    public double MaxFrameTimeMs;
    public double Low1PercentFps;
    public double Low01PercentFps;

    /// <summary>卡顿帧数（帧时间 &gt; 平均的两倍）。</summary>
    public int StutterCount;

    public bool Truncated;

    /// <summary>逐帧 CSV 的文件名（相对于 records\csv\），未写则为 null。</summary>
    public string? CsvFile;
}
