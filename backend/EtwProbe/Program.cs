using System;
using System.Text;
using System.Threading;
using GameFPS.Core;

/// <summary>
/// 控制台验证程序：直接驱动真实采集引擎，实时打印 FPS / 帧时间 / 硬件占用。
/// 用法: EtwProbe.exe [运行秒数]
/// </summary>
internal static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        int secs = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 20;

        Console.WriteLine("=== GameFPS 采集验证 ===");
        Console.WriteLine($"管理员权限 : {EtwFrameCapture.IsElevated}");
        Console.WriteLine();

        if (!EtwFrameCapture.IsElevated)
        {
            Console.WriteLine("!! 未提权，ETW 会话必然失败。请以管理员身份运行。");
        }

        using var engine = new MonitorEngine();
        MonitorSnapshot? last = null;
        engine.Updated += snap => last = snap;

        if (!engine.Start())
        {
            Console.WriteLine("启动失败: " + engine.StartError);
            return 2;
        }

        for (int t = 0; t < secs; t++)
        {
            Thread.Sleep(1000);
            var v = last;
            if (v == null)
            {
                Console.WriteLine($"[{t + 1,3}s] 等待数据 ...");
                continue;
            }

            Console.WriteLine(
                $"[{t + 1,3}s] {(string.IsNullOrEmpty(v.GameName) ? "(无窗口进程)" : v.GameName)}(pid={v.GamePid}) " +
                $"FPS={v.Fps,6:F1}  1%Low={v.Low1PercentFps,6:F1}  0.1%Low={v.Low01PercentFps,6:F1}  " +
                $"帧时间={v.AvgFrameTimeMs,5:F2}ms  总帧={v.TotalFrames,7}  " +
                $"CPU={v.CpuUsagePercent,5:F1}%  GPU={v.GpuUsagePercent,5:F1}%  " +
                $"显存={v.GpuMemoryMB,6:F0}MB  内存={v.MemoryUsagePercent,5:F1}%");

            if (t == secs - 1 && v.RecentFrameTimes.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"最近 {v.RecentFrameTimes.Length} 帧帧时间样本: " +
                    string.Join(", ", Array.ConvertAll(v.RecentFrameTimes[^Math.Min(12, v.RecentFrameTimes.Length)..],
                        x => x.ToString("F1"))));
            }
        }

        return 0;
    }
}
