using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace GameFPS.Core;

/// <summary>
/// 基于 ETW 的帧率采集器（PresentMon 同路线）。
///
/// 原理：Microsoft-Windows-DxgKrnl 是内核态图形 provider，任何渲染 API
/// (D3D9 / D3D11 / D3D12 / Vulkan) 的 Present 最终都会经过 dxgkrnl.sys，
/// 在那里产生 event id = 184 (task=Present, keyword=0x08000000) 的事件，
/// 每帧一次。事件头里带有发起进程的 PID 与高精度时间戳，因此我们可以在
/// 完全不对游戏进程做任何注入的情况下，得到逐帧时间线。
///
/// 必须管理员权限：创建 ETW 会话需要 SeSystemProfilePrivilege。
/// </summary>
public sealed class EtwFrameCapture : IDisposable
{
    /// <summary>DxgKrnl 的 Present 事件 ID（每帧一次，已实测验证）。</summary>
    public const int PresentEventId = 184;

    /// <summary>Present 关键字掩码，只放行 Present 相关事件，避免每秒 6 万条噪声。</summary>
    private const ulong PresentKeyword = 0x08000000UL;

    /// <summary>
    /// 固定会话名（不掺 PID）。必须固定，才能在下次启动时认出并回收上一次的残留会话。
    /// </summary>
    private const string SessionName = "GameFPSMonitor";

    private static readonly Guid DxgKrnlProvider = new("802ec45a-1e99-4b83-9920-87c98277ba9d");

    private TraceEventSession? _session;
    private Thread? _pump;
    private volatile bool _running;

    /// <summary>每检测到一帧触发一次：(进程Id, 事件时间戳ms)。</summary>
    public event Action<int, double>? FramePresented;

    public bool IsRunning => _running;
    public static bool IsElevated => TraceEventSession.IsElevated() ?? false;
    public string? LastError { get; private set; }

    /// <summary>收到的总事件数（用于诊断 keyword 过滤是否生效）。</summary>
    public long RawEventCount;

    /// <summary>最近一帧的事件时间戳(ms)，作为整个引擎的单调时间轴。</summary>
    public double LastTimestampMs;

    public bool Start()
    {
        if (_running) return true;
        LastError = null;

        try
        {
            // 关键：先回收上一次异常退出（被强杀）留下的残留会话。
            // ETW 的 realtime 会话在宿主进程被强制终止时不会自动回收，残留会持续占用
            // 系统会话资源，累积到一定程度后 StartTrace 就会返回
            // ERROR_NO_SYSTEM_RESOURCES (0x800705AA)，导致再也开不了采集。
            CleanupStaleSessions();

            _session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true,
            };
            _session.EnableProvider(DxgKrnlProvider, TraceEventLevel.Verbose, PresentKeyword);
            _session.Source.AllEvents += OnEvent;

            _pump = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "GameFPS.EtwPump",
            };
            _running = true;
            _pump.Start();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex is UnauthorizedAccessException
                ? "创建 ETW 会话被拒绝，请以管理员身份运行。"
                : $"{ex.GetType().Name}: {ex.Message}";
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// 停止所有名字以 "GameFPSMonitor" 开头的残留 ETW 会话。
    /// 借助系统自带 logman，避免自己维护 EVENT_TRACE_PROPERTIES 的裸内存布局。
    /// </summary>
    private static void CleanupStaleSessions()
    {
        try
        {
            var query = new ProcessStartInfo("logman.exe", "query -ets")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var queryProc = Process.Start(query);
            if (queryProc == null) return;

            string output = queryProc.StandardOutput.ReadToEnd();
            queryProc.WaitForExit(3000);

            foreach (var line in output.Split('\n'))
            {
                int at = line.IndexOf("GameFPSMonitor", StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;

                string name = line[at..].Trim().Split(' ', '\t', '\r')[0].Trim();
                if (name.Length == 0) continue;

                var stop = new ProcessStartInfo("logman.exe", $"stop \"{name}\" -ets")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var stopProc = Process.Start(stop);
                stopProc?.WaitForExit(3000);
            }
        }
        catch
        {
            // 清理失败不阻断启动：仍按正常流程去建会话
        }
    }

    private void PumpLoop()
    {
        try
        {
            _session?.Source.Process();
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _running = false;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        // keyword 已过滤掉大部分噪声，这里再用事件 ID 精确锁定 Present
        if ((int)e.ID != PresentEventId) return;
        Interlocked.Increment(ref RawEventCount);
        LastTimestampMs = e.TimeStampRelativeMSec;
        FramePresented?.Invoke(e.ProcessID, e.TimeStampRelativeMSec);
    }

    public void Stop()
    {
        if (!_running && _session == null) return;
        try { _session?.Stop(); } catch { /* 关停竞态可忽略 */ }
        _pump?.Join(2000);
        Cleanup();
    }

    private void Cleanup()
    {
        _running = false;
        try { _session?.Dispose(); } catch { }
        _session = null;
        _pump = null;
    }

    public void Dispose() => Stop();
}
