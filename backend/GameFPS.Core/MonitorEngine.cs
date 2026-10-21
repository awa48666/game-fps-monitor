using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace GameFPS.Core;

/// <summary>一次监测快照，供 UI / IPC 消费。</summary>
public sealed class MonitorSnapshot
{
    public long Seq;
    public bool Elevated;
    public string? Error;

    public int GamePid;
    public string GameName = "";
    public string GamePath = "";
    public bool GameActive;

    public double Fps;
    public double AvgFrameTimeMs;
    public double MinFrameTimeMs;
    public double MaxFrameTimeMs;
    public double Low1PercentFps;
    public double Low01PercentFps;
    public long TotalFrames;

    public double CpuUsagePercent;
    public double GpuUsagePercent;
    public double GpuMemoryMB;
    public double GpuMemoryTotalMB;
    public double MemoryUsedMB;
    public double MemoryTotalMB;
    public double MemoryUsagePercent;

    // ---- 频率 / 功耗 / 大小核 ----
    public double CpuFrequencyMHz;
    public double CpuPowerW;
    public bool HasCpuPower;

    public double PCoreUsagePercent;
    public double PCoreFrequencyMHz;
    public double ECoreUsagePercent;
    public double ECoreFrequencyMHz;
    public bool HasHeterogeneousCores;

    public double GpuFrequencyMHz;
    public double GpuPowerW;
    public bool HasGpuSensors;

    /// <summary>GPU 核心温度（NVML，准确）。</summary>
    public double GpuTemperatureC;

    /// <summary>显存温度（多数消费级卡不支持）。</summary>
    public double GpuMemoryTemperatureC;
    public bool HasGpuMemoryTemperature;

    /// <summary>CPU 温度（ACPI 热区，非核心温度）。</summary>
    public double CpuTemperatureC;
    public bool HasCpuTemperature;

    /// <summary>1% Low 的历史序列（每秒一个采样点），供前端画低帧率曲线。</summary>
    public double[] Low1PercentHistory = Array.Empty<double>();

    /// <summary>0.1% Low 的历史序列（每秒一个采样点）。</summary>
    public double[] Low01PercentHistory = Array.Empty<double>();

    /// <summary>最近若干帧的帧时间(ms)，供前端画曲线。</summary>
    public double[] RecentFrameTimes = Array.Empty<double>();

    /// <summary>当前这场游戏会话已经累积的帧数（0 表示没有进行中的会话）。</summary>
    public long SessionFrames;
    public string SessionStartedAt = "";

    /// <summary>是否处于"手动指定监测目标"模式。</summary>
    public bool ManualTarget;
    public string ManualTargetName = "";
}

/// <summary>一个可供手动选择的渲染进程。</summary>
public sealed class ProcessCandidate
{
    public int Pid;
    public string Name = "";
    public string Path = "";
    public double Fps;
    public bool IsForeground;
}

/// <summary>
/// 监测引擎：把 ETW 帧采集、硬件监控、游戏识别组合成固定频率的快照流，
/// 并在游戏退出时把整场的帧率记录落盘。
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    private const int TickIntervalMs = 200;
    private const int FrameHistoryForUi = 240;

    /// <summary>少于这个帧数的一瞬而过进程不值得写进历史。</summary>
    private const int MinFramesToRecord = 30;

    private readonly EtwFrameCapture _capture = new();
    private readonly HardwareMonitor _hw = new();
    private readonly ConcurrentDictionary<int, FrameStats> _stats = new();
    private readonly ConcurrentDictionary<int, double> _lastFrameTs = new();

    // ---- 会话记录 ----
    private readonly ConcurrentDictionary<int, GameSession> _sessions = new();
    private readonly ConcurrentDictionary<int, double> _lastFrameForSession = new();
    private int _currentSessionPid;

    /// <summary>
    /// 粘性监测目标：一旦认定某个游戏，只要它还在渲染就继续跟它，
    /// 不会因为 alt-tab 到浏览器、动态壁纸刷新而被抢走。
    /// 游戏退出或停止渲染后自动清空。
    /// </summary>
    private int _stickyGamePid;

    private Timer? _timer;
    private long _seq;

    // ---- 1% / 0.1% Low 曲线历史（每秒一个点）----
    private readonly List<double> _low1History = new();
    private readonly List<double> _low01History = new();
    private const int LowHistoryMax = 180;
    private double _lastLowSampleMs;

    public event Action<MonitorSnapshot>? Updated;

    /// <summary>一场游戏结束且记录已落盘时触发。</summary>
    public event Action<SessionRecord>? SessionCompleted;

    /// <summary>
    /// 要排除的进程可执行文件路径（自身 Electron 前端）。否则切到本程序界面时，
    /// “前台窗口优先”策略会把自己的 UI 误判成正在玩的游戏。
    /// </summary>
    public string? ExcludedExecutablePath { get; set; }

    // ---- 会话记录设置（由 AppHost 从 OverlaySettings 同步过来）----
    public bool RecordSessions { get; set; } = true;
    /// <summary>兜底：连续多久无新帧就结算本场（秒）。0 = 关闭，只认进程退出。</summary>
    public int SessionIdleSeconds { get; set; }
    public bool WriteFrameCsv { get; set; } = true;
    public int MaxSessionsInHistory { get; set; } = 300;
    public SessionRecorder? Recorder { get; set; }

    // ---- 手动指定监测目标 ----
    private bool _manualTargetEnabled;
    private string _manualTargetName = "";
    private int _manualPid;

    /// <summary>开启后不再自动检测，只监测 <see cref="ManualTargetName"/> 指定的进程。</summary>
    public bool ManualTargetEnabled
    {
        get => _manualTargetEnabled;
        set
        {
            if (_manualTargetEnabled == value) return;
            _manualTargetEnabled = value;
            _manualPid = 0;
        }
    }

    public string ManualTargetName
    {
        get => _manualTargetName;
        set
        {
            if (_manualTargetName == value) return;
            _manualTargetName = value ?? "";
            _manualPid = 0;
        }
    }

    public bool IsElevated => EtwFrameCapture.IsElevated;
    public string? StartError { get; private set; }

    public bool Start()
    {
        _capture.FramePresented += OnFrame;
        bool ok = _capture.Start();
        StartError = _capture.LastError;
        _timer = new Timer(_ => Tick(), null, TickIntervalMs, TickIntervalMs);
        return ok;
    }

    private void OnFrame(int pid, double tsMs)
    {
        var st = _stats.GetOrAdd(pid, _ => new FrameStats());
        st.AddFrame(tsMs);
        _lastFrameTs[pid] = tsMs;

        // 只有当前这场会话所绑定的游戏，其帧才计入整场统计
        if (_sessions.TryGetValue(pid, out var session))
        {
            double prev = _lastFrameForSession.TryGetValue(pid, out var p) ? p : 0;
            session.AddFrame(tsMs, prev > 0 ? tsMs - prev : -1);
            _lastFrameForSession[pid] = tsMs;
        }
    }

    private void Tick()
    {
        try { BuildSnapshot(); }
        catch { /* 单次采样异常不应终止监测 */ }
    }

    private void BuildSnapshot()
    {
        _hw.Sample();

        double now = _capture.LastTimestampMs;
        PruneStale(now);

        int pid = PickGamePid(now);
        UpdateSessions(pid, now);

        var snap = new MonitorSnapshot
        {
            Seq = Interlocked.Increment(ref _seq),
            Elevated = IsElevated,
            Error = _capture.LastError ?? StartError,
            GamePid = pid,
            CpuUsagePercent = _hw.CpuUsagePercent,
            MemoryUsedMB = _hw.MemoryUsedMB,
            MemoryTotalMB = _hw.MemoryTotalMB,
            MemoryUsagePercent = _hw.MemoryUsagePercent,
            ManualTarget = ManualTargetEnabled,
            ManualTargetName = _manualTargetName,

            CpuFrequencyMHz = _hw.CpuFrequencyMHz,
            CpuPowerW = _hw.CpuPowerW,
            HasCpuPower = _hw.HasCpuPower,
            PCoreUsagePercent = _hw.PCoreUsagePercent,
            PCoreFrequencyMHz = _hw.PCoreFrequencyMHz,
            ECoreUsagePercent = _hw.ECoreUsagePercent,
            ECoreFrequencyMHz = _hw.ECoreFrequencyMHz,
            HasHeterogeneousCores = _hw.HasHeterogeneousCores,
            GpuFrequencyMHz = _hw.GpuFrequencyMHz,
            GpuPowerW = _hw.GpuPowerW,
            HasGpuSensors = _hw.HasGpuSensors,
            GpuTemperatureC = _hw.GpuTemperatureC,
            GpuMemoryTemperatureC = _hw.GpuMemoryTemperatureC,
            HasGpuMemoryTemperature = _hw.HasGpuMemoryTemperature,
            CpuTemperatureC = _hw.CpuTemperatureC,
            HasCpuTemperature = _hw.HasCpuTemperature,
        };

        if (pid > 0)
        {
            snap.GameName = ProcessUtil.GetProcessName(pid);
            snap.GamePath = ProcessUtil.GetProcessPath(pid);
            snap.GameActive = true;

            if (_stats.TryGetValue(pid, out var st))
            {
                var f = st.Snapshot();
                snap.Fps = Math.Round(f.Fps, 1);
                snap.AvgFrameTimeMs = Math.Round(f.AvgFrameTimeMs, 2);
                snap.MinFrameTimeMs = Math.Round(f.MinFrameTimeMs, 2);
                snap.MaxFrameTimeMs = Math.Round(f.MaxFrameTimeMs, 2);
                snap.Low1PercentFps = Math.Round(f.Low1PercentFps, 1);
                snap.Low01PercentFps = Math.Round(f.Low01PercentFps, 1);
                snap.TotalFrames = f.TotalFrames;
                snap.RecentFrameTimes = st.RecentFrameTimes(FrameHistoryForUi);

                RecordLowHistory(now, f.Low1PercentFps, f.Low01PercentFps);
            }

            snap.Low1PercentHistory = _low1History.ToArray();
            snap.Low01PercentHistory = _low01History.ToArray();

            snap.GpuUsagePercent = _hw.GetGpuUsage(pid);
            snap.GpuMemoryMB = _hw.GetGpuMemoryMB(pid);
            snap.GpuMemoryTotalMB = _hw.GetGpuMemoryTotalMB();
        }

        if (_currentSessionPid != 0 && _sessions.TryGetValue(_currentSessionPid, out var cur))
        {
            snap.SessionFrames = cur.FrameCount;
            snap.SessionStartedAt = cur.StartedAt.ToString("HH:mm:ss");
        }

        Updated?.Invoke(snap);
    }

    // ---------------- 会话生命周期 ----------------

    /// <summary>
    /// 由当前识别出的游戏驱动会话：
    ///   开了新游戏 → 选新游戏开场（旧场先结算）
    ///   进程已退出 / 长时间无 Present → 结算落盘
    /// </summary>
    private void UpdateSessions(int gamePid, double now)
    {
        if (!RecordSessions) return;

        bool hasCurrent = _currentSessionPid != 0;

        if (!hasCurrent && gamePid > 0)
        {
            StartSession(gamePid, now);
        }
        else if (hasCurrent && gamePid > 0 && gamePid != _currentSessionPid && ShouldSwitch(now, gamePid))
        {
            Log($"游戏切换 {_currentSessionPid} -> {gamePid}");
            CloseSession(_currentSessionPid);       // 游戏切换，先结算上一场
            StartSession(gamePid, now);
        }

        if (_currentSessionPid != 0 && _sessions.TryGetValue(_currentSessionPid, out var session))
        {
            // 只认"进程彻底退出"：切到桌面、加载场景、暂停、最小化都不算结束，
            // 这样一整局游戏才会记成一条完整记录，而不是被切成好几段。
            if (!ProcessUtil.ProcessExists(_currentSessionPid))
            {
                Log($"会话结束 pid={_currentSessionPid} frames={session.FrameCount} 原因=进程已退出");
                CloseSession(_currentSessionPid);
            }
            else if (SessionIdleSeconds > 0)
            {
                // 可选兜底（默认关闭）：只有当用户在设置里显式填了秒数才启用，
                // 用于应对"进程没退但再也不渲染"的异常情况。
                double idleMs = SessionIdleSeconds * 1000.0;
                double last = session.LastFrameTsMs;
                if (last > 0 && now > 0 && (now - last) > idleMs)
                {
                    Log($"会话结束 pid={_currentSessionPid} frames={session.FrameCount} 原因=空闲超时({SessionIdleSeconds}s)");
                    CloseSession(_currentSessionPid);
                }
            }
        }
    }

    /// <summary>
    /// 每秒往 1% / 0.1% Low 的曲线历史里推一个点。
    /// 这两个值本身是基于滑动窗口算的，如果每个 tick(200ms) 都记，
    /// 曲线会变成一串几乎相同的台阶，所以按 1 秒采样。
    /// </summary>
    private void RecordLowHistory(double nowMs, double low1, double low01)
    {
        if (low1 <= 0 && low01 <= 0) return;
        if (_lastLowSampleMs > 0 && nowMs - _lastLowSampleMs < 1000) return;

        _lastLowSampleMs = nowMs;
        _low1History.Add(Math.Round(low1, 1));
        _low01History.Add(Math.Round(low01, 1));

        if (_low1History.Count > LowHistoryMax)
        {
            _low1History.RemoveRange(0, _low1History.Count - LowHistoryMax);
            _low01History.RemoveRange(0, _low01History.Count - LowHistoryMax);
        }
    }

    /// <summary>
    /// 是否该换场。
    /// 现在只认"旧会话的进程已经不存在"这一种情况：
    /// 只要那个进程还活着，就继续记在同一场里，alt-tab 切到浏览器、游戏最小化都不会打断记录。
    /// </summary>
    private bool ShouldSwitch(double now, int newPid)
    {
        return !ProcessUtil.ProcessExists(_currentSessionPid);
    }

    /// <summary>诊断日志：会话生命周期只在出现问题时才需要看，写到 exe 旁边。</summary>
    private static void Log(string msg)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "session-debug.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 262144) File.WriteAllText(path, string.Empty);   // 防止无限增长

            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private void StartSession(int pid, double now)
    {
        var session = new GameSession(pid, now);
        session.UpdateGameInfo(ProcessUtil.GetProcessName(pid), ProcessUtil.GetProcessPath(pid));
        _sessions[pid] = session;
        _lastFrameForSession.TryRemove(pid, out _);
        _currentSessionPid = pid;

        // 换游戏了，低帧率曲线重新开始记
        _low1History.Clear();
        _low01History.Clear();
        _lastLowSampleMs = 0;

        Log($"会话开始 pid={pid} name={session.GameName}");
    }

    private void CloseSession(int pid)
    {
        _currentSessionPid = 0;
        _lastFrameForSession.TryRemove(pid, out _);

        if (!_sessions.TryRemove(pid, out var session)) return;
        if (session.FrameCount < MinFramesToRecord)
        {
            Log($"会话丢弃 pid={pid} 帧数不足 ({session.FrameCount} < {MinFramesToRecord})");
            return;
        }

        SessionRecord? record = null;
        try { record = Recorder?.Save(session, WriteFrameCsv, MaxSessionsInHistory); }
        catch (Exception ex) { Log($"落盘失败: {ex.GetType().Name} {ex.Message}"); }

        if (record != null)
        {
            Log($"已保存 pid={pid} 平均={record.AverageFps} 1%Low={record.Low1PercentFps} " +
                $"帧={record.FrameCount} csv={record.CsvFile ?? "(无)"}");
            SessionCompleted?.Invoke(record);
        }
        else
        {
            Log($"未保存 pid={pid}（Recorder={(Recorder == null ? "null" : "ok")}）");
        }
    }

    /// <summary>丢弃 5 秒内没有任何 Present 的进程，让候选列表保持干净（会话不受影响）。</summary>
    private void PruneStale(double now)
    {
        if (now <= 0) return;
        foreach (var kv in _lastFrameTs)
        {
            if (now - kv.Value <= 5000) continue;
            _lastFrameTs.TryRemove(kv.Key, out _);
            if (_stats.TryRemove(kv.Key, out var st)) st.Reset();
        }
    }

    /// <summary>
    /// 解析手动指定的目标进程。
    /// 优先用界面选中的那个具体 PID；它失效后（游戏重启换了 PID）再按进程名找。
    /// </summary>
    private int ResolveManualTarget()
    {
        if (_manualPid > 0 && ProcessUtil.ProcessExists(_manualPid)) return _manualPid;

        if (string.IsNullOrWhiteSpace(_manualTargetName)) return 0;

        _manualPid = FindPidByName(_manualTargetName);
        return _manualPid;
    }

    /// <summary>界面选中某个进程时，直接把它钉住（比按名字猜准得多）。</summary>
    public void PinManualPid(int pid, string name)
    {
        _manualPid = pid;
        if (!string.IsNullOrEmpty(name)) _manualTargetName = name;
    }

    /// <summary>
    /// 按进程名找 PID。关键点：多进程应用（Edge/Chrome/Electron 游戏）会有十几个同名进程，
    /// 只有真正提交 Present 的那个才有帧数据，所以必须优先在"正在渲染的进程"里找，
    /// 不能直接取 GetProcessesByName 的第一个。
    /// </summary>
    private int FindPidByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;

        string bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

        // 1) 优先命中当前正在渲染的同名进程
        foreach (var kv in _lastFrameTs)
        {
            if (string.Equals(ProcessUtil.GetProcessName(kv.Key), bare, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }

        // 2) 退回到系统进程列表
        try
        {
            foreach (var p in Process.GetProcessesByName(bare))
            {
                try { return p.Id; }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return 0;
    }

    /// <summary>当前正在渲染的进程列表，供界面手动选择。</summary>
    public List<ProcessCandidate> GetCandidates()
    {
        var list = new List<ProcessCandidate>();

        foreach (var kv in _lastFrameTs)
        {
            int pid = kv.Key;
            string name = ProcessUtil.GetProcessName(pid);
            if (ProcessUtil.IsSystemProcess(name)) continue;
            if (IsSelfProcess(pid)) continue;

            double fps = _stats.TryGetValue(pid, out var st) ? st.Snapshot().Fps : 0;
            list.Add(new ProcessCandidate
            {
                Pid = pid,
                Name = name,
                Path = ProcessUtil.GetProcessPath(pid),
                Fps = Math.Round(fps, 1),
                IsForeground = pid == ProcessUtil.GetForegroundProcessId(),
            });
        }

        return list.OrderByDescending(x => x.Fps).ToList();
    }

    /// <summary>是否属于自身前端进程（按可执行文件路径匹配）。</summary>
    private bool IsSelfProcess(int pid)
    {
        var excluded = ExcludedExecutablePath;
        if (string.IsNullOrEmpty(excluded)) return false;
        return string.Equals(ProcessUtil.GetProcessPath(pid), excluded, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 选择"当前游戏"：优先前台窗口（且它确实在渲染），否则取帧率最高的活跃非系统进程。
    /// </summary>
    /// <summary>
    /// 已知的非游戏渲染进程。它们常年提交帧（动态壁纸、浏览器、桌面组件），
    /// 不该被当成"游戏"抢走监测目标。
    /// 仅用于判断"是否值得粘住"和"前台是不是另一个游戏"，
    /// 不阻止它们被选中——万一你玩的真是网页游戏，仍可手动锁定。
    /// </summary>
    private static readonly string[] NonGameProcesses =
    {
        "wallpaper32", "wallpaper64",                      // Wallpaper Engine
        "msedge", "chrome", "firefox", "brave", "opera",    // 浏览器
        "msedgewebview2", "webviewhost",                    // WebView2 宿主
        "explorer", "dwm", "SearchApp", "ShellExperienceHost", "StartMenuExperienceHost",
        "ApplicationFrameHost", "LockApp", "TextInputHost",
        "Code", "devenv", "rider64", "pycharm64",          // 编辑器
        "GamePP", "NVIDIA Overlay", "Discord", "QQ", "WeChat",
    };

    private static bool IsNonGameProcess(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var n in NonGameProcesses)
            if (string.Equals(name, n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private int PickGamePid(double now)
    {
        // 手动指定了目标就完全跳过自动检测
        if (ManualTargetEnabled) return ResolveManualTarget();

        if (_stats.IsEmpty) return 0;

        int fg = ProcessUtil.GetForegroundProcessId();
        string fgName = ProcessUtil.GetProcessName(fg);
        bool fgUsable = fg > 0 && !ProcessUtil.IsSystemProcess(fgName) && !IsSelfProcess(fg);

        // ---- 粘性目标：上次认定的游戏只要还在渲染就继续跟它 ----
        // 否则 alt-tab 到浏览器、动态壁纸刷帧时，监测目标会被反复抢走。
        if (_stickyGamePid > 0)
        {
            bool alive = ProcessUtil.ProcessExists(_stickyGamePid);
            bool rendering = _lastFrameTs.TryGetValue(_stickyGamePid, out var lastTs)
                             && (now - lastTs) < 1500;

            if (alive && rendering)
            {
                // 只有当前台冒出"另一个像游戏的进程"时才让位（比如换了款游戏）
                bool fgIsAnotherGame = fgUsable
                                       && fg != _stickyGamePid
                                       && !IsNonGameProcess(fgName);
                if (!fgIsAnotherGame) return _stickyGamePid;
            }
            else
            {
                _stickyGamePid = 0;      // 游戏退出或停止渲染，放弃粘性
            }
        }

        var active = new List<(int pid, double fps)>();
        foreach (var kv in _lastFrameTs)
        {
            if (now > 0 && now - kv.Value > 1500) continue;          // 已停止渲染
            if (ProcessUtil.IsSystemProcess(ProcessUtil.GetProcessName(kv.Key))) continue;
            if (IsSelfProcess(kv.Key)) continue;                     // 自己的界面不算游戏
            if (kv.Key == fg) { active.Add((kv.Key, double.MaxValue)); continue; }

            double fps = _stats.TryGetValue(kv.Key, out var st) ? st.Snapshot().Fps : 0;
            active.Add((kv.Key, fps));
        }

        if (active.Count == 0) return 0;

        int picked = (fgUsable && active.Any(a => a.pid == fg))
            ? fg
            : active.OrderByDescending(a => a.fps).First().pid;

        // 记住这次选中的目标（非游戏进程不粘，免得把浏览器钉死）
        if (!IsNonGameProcess(ProcessUtil.GetProcessName(picked)))
            _stickyGamePid = picked;

        return picked;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _capture.FramePresented -= OnFrame;
        _capture.Dispose();
        _hw.Dispose();
    }
}
