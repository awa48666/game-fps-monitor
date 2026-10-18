using System;
using System.Collections.Generic;
using System.Linq;

namespace GameFPS.Core;

/// <summary>
/// 单个进程的帧率统计：实时 FPS、平均帧时间、1% Low、0.1% Low。
/// 实时 FPS 用最近 1 秒窗口内的帧数计算（与游戏加加/RTSS 口径一致）。
/// </summary>
public sealed class FrameStats
{
    private const int FrameTimeCapacity = 2048;

    private readonly Queue<double> _timestamps = new();          // 最近 1s 的帧时间戳
    private readonly double[] _frameTimes = new double[FrameTimeCapacity];
    private readonly object _lock = new();
    private int _ftHead;
    private int _ftCount;
    private long _totalFrames;
    private double _lastTimestamp;

    public long TotalFrames { get { lock (_lock) return _totalFrames; } }

    /// <summary>记录一帧。timestampMs 为单调递增的毫秒时间戳。</summary>
    public void AddFrame(double timestampMs)
    {
        lock (_lock)
        {
            // 帧时间：与上一帧的间隔；过滤超过 2s 的间隔（切场景/暂停）
            if (_lastTimestamp > 0)
            {
                double dt = timestampMs - _lastTimestamp;
                if (dt > 0 && dt < 2000)
                {
                    _frameTimes[_ftHead] = dt;
                    _ftHead = (_ftHead + 1) % FrameTimeCapacity;
                    if (_ftCount < FrameTimeCapacity) _ftCount++;
                }
            }
            _lastTimestamp = timestampMs;
            _totalFrames++;

            _timestamps.Enqueue(timestampMs);
            while (_timestamps.Count > 0 && timestampMs - _timestamps.Peek() > 1000)
                _timestamps.Dequeue();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _timestamps.Clear();
            _ftHead = 0;
            _ftCount = 0;
            _totalFrames = 0;
            _lastTimestamp = 0;
        }
    }

    public FrameSnapshot Snapshot()
    {
        lock (_lock)
        {
            var snap = new FrameSnapshot
            {
                TimestampMs = _lastTimestamp,
                Fps = _timestamps.Count,          // 窗口是 1 秒，所以帧数即 FPS
                TotalFrames = _totalFrames,
            };

            if (_ftCount > 0)
            {
                var arr = new double[_ftCount];
                for (int i = 0; i < _ftCount; i++)
                {
                    int idx = (_ftHead - _ftCount + i + FrameTimeCapacity * 2) % FrameTimeCapacity;
                    arr[i] = _frameTimes[idx];
                }

                snap.AvgFrameTimeMs = arr.Average();
                snap.MinFrameTimeMs = arr.Min();
                snap.MaxFrameTimeMs = arr.Max();
                snap.Low1PercentFps = SlowestPercentFps(arr, 0.01);
                snap.Low01PercentFps = SlowestPercentFps(arr, 0.001);
            }

            return snap;
        }
    }

    /// <summary>最近 n 帧的帧时间(ms)，按时间先后顺序，供 UI 画曲线。</summary>
    public double[] RecentFrameTimes(int n)
    {
        lock (_lock)
        {
            if (n > _ftCount) n = _ftCount;
            var res = new double[n];
            for (int i = 0; i < n; i++)
            {
                int idx = (_ftHead - n + i + FrameTimeCapacity * 2) % FrameTimeCapacity;
                res[i] = _frameTimes[idx];
            }
            return res;
        }
    }

    /// <summary>最慢 p 比例帧的平均帧时间 → FPS。这就是 1% Low / 0.1% Low 的标准算法。</summary>
    private static double SlowestPercentFps(double[] frameTimes, double p)
    {
        var sorted = (double[])frameTimes.Clone();
        Array.Sort(sorted);                                   // 升序：最快的在前，最慢的在末尾
        int n = Math.Max(1, (int)Math.Ceiling(sorted.Length * p));
        double sum = 0;
        for (int i = sorted.Length - n; i < sorted.Length; i++) sum += sorted[i];
        double avg = sum / n;
        return avg > 0 ? 1000.0 / avg : 0;
    }
}

public struct FrameSnapshot
{
    public double TimestampMs;
    public double Fps;
    public double AvgFrameTimeMs;
    public double MinFrameTimeMs;
    public double MaxFrameTimeMs;
    public double Low1PercentFps;
    public double Low01PercentFps;
    public long TotalFrames;
}
