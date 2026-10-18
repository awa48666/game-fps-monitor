using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using GameFPS.Core;

namespace GameFPS.Backend;

/// <summary>
/// 宿主：把 ETW 采集引擎、游戏内悬浮窗、托盘、WebSocket IPC 与 Electron 前端串起来。
/// </summary>
public sealed class AppHost : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,      // MonitorSnapshot / OverlaySettings 以公共字段暴露
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly MonitorEngine _engine = new();
    private OverlaySettings _settings = new();
    private OverlayForm? _overlay;
    private IpcServer? _ipc;
    private TrayHost? _tray;
    private HotkeyWindow? _hotkeys;
    private Process? _electron;
    private MonitorSnapshot? _last;
    private SessionRecorder? _recorder;

    public string? StartError { get; private set; }

    public bool Start()
    {
        if (!EtwFrameCapture.IsElevated)
        {
            StartError = "帧率采集需要管理员权限（要创建 ETW 内核会话）。\n请右键选择“以管理员身份运行”。";
            return false;
        }

        // 每一步单独兜底：把失败位置说清楚，而不是笼统地抛 COMException
        try
        {
            _settings = OverlaySettings.Load();
        }
        catch (Exception ex)
        {
            StartError = $"读取设置失败：{ex.GetType().Name} {ex.Message}";
            return false;
        }

        try
        {
            _overlay = new OverlayForm(_settings);
            _ = _overlay.Handle;                 // 预建分层窗口句柄

            // 拖动结束：把新位置写回设置并广播给前端，让界面上的 X/Y 同步
            _overlay.DragCompleted += (x, y) =>
            {
                _settings.X = x;
                _settings.Y = y;
                _settings.Save();
                SendSettings();
            };
        }
        catch (Exception ex)
        {
            StartError = $"创建游戏内悬浮窗失败：{ex.GetType().Name} {ex.Message}";
            return false;
        }

        try
        {
            _ipc = new IpcServer();
            if (!_ipc.Start())
            {
                StartError = "无法在 127.0.0.1 上监听端口（8731-8750 均被占用）。";
                return false;
            }
            _ipc.MessageReceived += OnIpcMessage;
        }
        catch (Exception ex)
        {
            StartError = $"启动 IPC 服务失败：{ex.GetType().Name} {ex.Message}";
            return false;
        }

        try
        {
            if (!_engine.Start())
            {
                StartError = _engine.StartError ?? "ETW 采集器启动失败。";
                return false;
            }
            _engine.Updated += OnSnapshot;

            // 游戏退出时自动落盘帧率记录
            _recorder = new SessionRecorder(Path.Combine(AppContext.BaseDirectory, "records"));
            _engine.Recorder = _recorder;
            _engine.SessionCompleted += OnSessionCompleted;
            ApplyRecordingSettings();
        }
        catch (Exception ex)
        {
            StartError = $"启动 ETW 采集引擎失败：{ex.GetType().Name} {ex.Message}";
            return false;
        }

        try
        {
            _tray = new TrayHost(_settings.OverlayEnabled);
            _tray.OpenMainRequested += OpenMainUi;
            _tray.ToggleOverlayRequested += ToggleOverlay;
            _tray.ExitRequested += () => Application.ExitThread();
        }
        catch (Exception ex)
        {
            StartError = $"创建系统托盘图标失败：{ex.GetType().Name} {ex.Message}";
            return false;
        }

        try
        {
            _hotkeys = new HotkeyWindow(_settings);
            _hotkeys.ToggleOverlay += ToggleOverlay;
            _hotkeys.OpenMain += OpenMainUi;
            _hotkeys.HotkeyFailed += (hk, reason) =>
                _ipc?.Broadcast(JsonSerializer.Serialize(
                    new { type = "hotkeyFailed", data = new { hotkey = hk, reason } }, JsonOpts));
        }
        catch (Exception ex)
        {
            StartError = $"注册全局热键失败：{ex.GetType().Name} {ex.Message}";
            return false;
        }

        if (_settings.LaunchElectronUi) LaunchElectron();
        return true;
    }

    // ---------- 采集 → UI ----------

    private void OnSnapshot(MonitorSnapshot snap)
    {
        _last = snap;

        if (_ipc is { ClientCount: > 0 })
            _ipc.Broadcast(JsonSerializer.Serialize(new { type = "snapshot", data = snap }, JsonOpts));

        bool shouldShow = _settings.OverlayEnabled
                          && (!_settings.AutoShowOnGame || snap.GameActive);

        if (shouldShow) _overlay?.ApplySnapshot(snap);
        else _overlay?.HideOverlay();
    }

    // ---------- 游戏记录 ----------

    private void ApplyRecordingSettings()
    {
        _engine.RecordSessions = _settings.RecordSessions;
        _engine.SessionIdleSeconds = _settings.SessionIdleSeconds;
        _engine.WriteFrameCsv = _settings.WriteFrameCsv;
        _engine.MaxSessionsInHistory = _settings.MaxSessionsInHistory;
        _engine.ManualTargetEnabled = _settings.ManualTargetEnabled;
        _engine.ManualTargetName = _settings.ManualTargetName;
    }

    private void SendCandidates()
        => _ipc?.Broadcast(JsonSerializer.Serialize(
               new { type = "candidates", data = _engine.GetCandidates() }, JsonOpts));

    /// <summary>一场游戏结束且记录已落盘：托盘提示并通知界面刷新历史。</summary>
    private void OnSessionCompleted(SessionRecord record)
    {
        try
        {
            _tray?.ShowBalloon("已保存本场帧率记录",
                $"{record.GameName} · {record.DurationSeconds:F0} 秒 · 平均 {record.AverageFps:F0} FPS · " +
                $"1% Low {record.Low1PercentFps:F0}");
        }
        catch { }

        _ipc?.Broadcast(JsonSerializer.Serialize(
            new { type = "sessionCompleted", data = record }, JsonOpts));
    }

    private void SendSessions()
        => _ipc?.Broadcast(JsonSerializer.Serialize(
               new { type = "sessions", data = _recorder?.LoadHistory(200) ?? new List<SessionRecord>() },
               JsonOpts));

    private void OpenRecordsFolder()
    {
        try
        {
            var dir = _recorder?.RootDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ---------- 前端 → 后端 ----------

    private void OnIpcMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;

            switch (typeEl.GetString())
            {
                case "hello":
                    SendSettings();
                    SendSessions();
                    SendCandidates();
                    if (_last != null)
                        _ipc?.Broadcast(JsonSerializer.Serialize(new { type = "snapshot", data = _last }, JsonOpts));
                    break;

                case "getCandidates":
                    SendCandidates();
                    break;

                case "getSessions":
                    SendSessions();
                    break;

                case "resetSettings":
                    ApplySettings(new OverlaySettings());
                    break;

                case "getSessionFrames":
                    // data: { csvFile: "20261017_011813_msedge_pid57056.csv" }
                    if (doc.RootElement.TryGetProperty("csvFile", out var csvEl))
                    {
                        string csvFile = csvEl.GetString() ?? "";
                        var frames = _recorder?.LoadFrameTimes(csvFile) ?? Array.Empty<double>();
                        _ipc?.Broadcast(JsonSerializer.Serialize(
                            new { type = "sessionFrames", data = new { csvFile, frameTimes = frames } }, JsonOpts));
                    }
                    break;

                case "openRecordsFolder":
                    OpenRecordsFolder();
                    break;

                case "setSettings":
                    if (doc.RootElement.TryGetProperty("data", out var dataEl))
                    {
                        // 必须带上 JsonOpts：前端发来的是 camelCase，而 JsonElement.Deserialize
                        // 默认大小写敏感，不传选项会反序列化出一个全默认值对象，把用户设置覆盖掉。
                        var updated = dataEl.Deserialize<OverlaySettings>(JsonOpts);
                        if (updated != null)
                        {
                            ApplySettings(updated);

                            // 界面若一并带了选中的具体 PID，就把它钉住，
                            // 比按进程名猜准得多（Edge 这类多进程应用有十几个同名进程）。
                            if (doc.RootElement.TryGetProperty("targetPid", out var tpEl)
                                && tpEl.TryGetInt32(out int targetPid) && targetPid > 0)
                            {
                                _engine.PinManualPid(targetPid, updated.ManualTargetName);
                            }
                        }
                    }
                    break;

                case "setOverlayEnabled":
                    if (doc.RootElement.TryGetProperty("value", out var valueEl))
                    {
                        _settings.OverlayEnabled = valueEl.GetBoolean();
                        _settings.Save();
                        _tray?.SetOverlayChecked(_settings.OverlayEnabled);
                        SendSettings();
                        if (_last != null) OnSnapshot(_last);
                    }
                    break;

                case "setManualTarget":
                    // data: { name: "game.exe", pid: 1234 }  —— pid 可选，有就钉住它
                    if (doc.RootElement.TryGetProperty("name", out var nameEl))
                    {
                        string targetName = nameEl.GetString() ?? "";
                        int targetPid = doc.RootElement.TryGetProperty("pid", out var pidEl)
                            ? pidEl.GetInt32() : 0;

                        _settings.ManualTargetEnabled = true;
                        _settings.ManualTargetName = targetName;
                        _settings.Save();

                        _engine.ManualTargetEnabled = true;
                        _engine.ManualTargetName = targetName;
                        if (targetPid > 0) _engine.PinManualPid(targetPid, targetName);

                        SendSettings();
                        SendCandidates();
                        if (_last != null) OnSnapshot(_last);
                    }
                    break;

                case "clearManualTarget":
                    _settings.ManualTargetEnabled = false;
                    _settings.ManualTargetName = "";
                    _settings.Save();
                    ApplyRecordingSettings();
                    SendSettings();
                    SendCandidates();
                    if (_last != null) OnSnapshot(_last);
                    break;

                case "toggleOverlay":
                    ToggleOverlay();
                    break;

                case "setOverlayDragMode":
                    // data: { enabled: true } 进入拖动定位模式，false 退出
                    if (doc.RootElement.TryGetProperty("enabled", out var dragEl))
                    {
                        bool on = dragEl.ValueKind == JsonValueKind.True;
                        _overlay?.SetDragMode(on);
                        if (on) _overlay?.ShowForDrag();
                    }
                    break;

                case "openMainUi":
                    OpenMainUi();
                    break;

                case "quit":
                    // 必须用 Application.Exit()：本回调跑在线程池线程上，
                    // ExitThread() 只会去结束"当前线程"的消息循环，对主 UI 线程无效。
                    Application.Exit();
                    break;
            }
        }
        catch { /* 非法消息直接忽略 */ }
    }

    private void ApplySettings(OverlaySettings settings)
    {
        _settings = settings;
        _settings.Save();
        _overlay?.UpdateSettings(_settings);
        _hotkeys?.Apply(_settings);
        _tray?.SetOverlayChecked(_settings.OverlayEnabled);
        ApplyRecordingSettings();
        SendSettings();
        if (_last != null) OnSnapshot(_last);
    }

    private void SendSettings()
        => _ipc?.Broadcast(JsonSerializer.Serialize(new { type = "settings", data = _settings }, JsonOpts));

    private void ToggleOverlay()
    {
        _settings.OverlayEnabled = !_settings.OverlayEnabled;
        _settings.Save();
        _tray?.SetOverlayChecked(_settings.OverlayEnabled);

        if (!_settings.OverlayEnabled) _overlay?.HideOverlay();
        else if (_last != null) _overlay?.ApplySnapshot(_last);

        SendSettings();
    }

    // ---------- Electron 前端进程 ----------

    private void OpenMainUi()
    {
        if (_electron is { HasExited: false })
        {
            _ipc?.Broadcast(JsonSerializer.Serialize(new { type = "focus" }, JsonOpts));
            return;
        }
        LaunchElectron();
    }

    private void LaunchElectron()
    {
        var appDir = LocateFrontendApp();
        if (appDir == null) return;

        string? exe = LocateElectronExe(appDir);
        if (exe == null) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                // --no-sandbox: 在受限令牌的宿主环境里 Chromium 沙箱会初始化失败
                // (进程以 STATUS_BREAKPOINT 立即退出、主进程脚本完全不执行)。
                // 本界面只加载本地文件、不加载远端内容，关闭沙箱是可接受的取舍。
                Arguments = $"\"{appDir}\" --ws-port={_ipc?.Port ?? 8731} --no-sandbox",
                WorkingDirectory = appDir,
                UseShellExecute = false,
            };

            // 关键：electron.exe 会继承父进程环境。若 ELECTRON_RUN_AS_NODE 存在，
            // 它会退化成纯 Node 运行（require('electron') 拿到的是 npm 包的路径字符串），
            // 主进程脚本随即崩溃。某些 Electron 系宿主软件会注入这个变量，必须清掉。
            psi.Environment.Remove("ELECTRON_RUN_AS_NODE");
            psi.Environment.Remove("ELECTRON_NO_ATTACH_CONSOLE");

            _electron = Process.Start(psi);

            // 别把自己的前端界面当成"正在玩的游戏"
            if (_electron != null) _engine.ExcludedExecutablePath = exe;
        }
        catch { /* 前端缺失时后端仍可独立工作 */ }
    }

    /// <summary>依次尝试：exe 同级的 app\，以及开发期仓库根下的 app\。</summary>
    private static string? LocateFrontendApp()
    {
        var roots = new[]
        {
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        };

        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, "app");
            if (LocateElectronExe(candidate) != null) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Electron 运行时的两种布局：
    ///   发布版  app\electron\electron.exe
    ///   开发期  app\node_modules\electron\dist\electron.exe
    /// </summary>
    private static string? LocateElectronExe(string appDir)
    {
        if (!Directory.Exists(appDir)) return null;

        var candidates = new[]
        {
            Path.Combine(appDir, "electron", "electron.exe"),
            Path.Combine(appDir, "node_modules", "electron", "dist", "electron.exe"),
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    public void Dispose()
    {
        _engine.Updated -= OnSnapshot;
        _engine.SessionCompleted -= OnSessionCompleted;
        _engine.Dispose();

        try { _hotkeys?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _ipc?.Dispose(); } catch { }
        try { _overlay?.Dispose(); } catch { }

        try
        {
            if (_electron is { HasExited: false }) _electron.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
