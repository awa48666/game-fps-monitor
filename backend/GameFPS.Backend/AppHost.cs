using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
    private CrosshairForm? _crosshair;
    private CrosshairHold? _hold;
    private IpcServer? _ipc;
    private TrayHost? _tray;
    private HotkeyWindow? _hotkeys;
    private Process? _electron;

    /// <summary>开机自启最后已知的真实状态（以注册表为准），用于避免高频路径重复访问注册表。</summary>
    private bool _autoStartKnown;
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
            // 准星单独兜底：它挂了不该拖垮帧率监测这个主功能
            _crosshair = new CrosshairForm(_settings.Crosshair);
            _hold = new CrosshairHold(_crosshair);
            _hold.Apply(_settings.Crosshair);
        }
        catch { _crosshair = null; _hold = null; }

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

            // 游戏退出时自动落盘帧率记录（存到 %LOCALAPPDATA%，不随程序更新丢失）
            _recorder = new SessionRecorder(AppPaths.RecordsDir);
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
            // 开机自启以注册表实际状态为准，不信设置文件 ——
            // 用户可能在别处（任务管理器启动项）关掉过它
            bool autoStart = AutoStartManager.IsEnabled();
            _autoStartKnown = autoStart;
            if (_settings.AutoStart != autoStart)
            {
                _settings.AutoStart = autoStart;
                _settings.Save();
            }

            _tray = new TrayHost(_settings.OverlayEnabled, autoStart);
            _tray.OpenMainRequested += OpenMainUi;
            _tray.ToggleOverlayRequested += ToggleOverlay;
            _tray.AutoStartToggleRequested += ApplyAutoStart;
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
            _hotkeys.ToggleCrosshair += ToggleCrosshair;
            _hotkeys.HotkeyFailed += (hk, reason) =>
                _ipc?.Broadcast(JsonSerializer.Serialize(
                    new { type = "hotkeyFailed", data = new { hotkey = hk, reason } }, JsonOpts));
        }
        catch (Exception ex)
        {
            StartError = $"注册全局热键失败：{ex.GetType().Name} {ex.Message}";
            return false;
        }

        ApplyPriority();

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
        _engine.SetCpuUsageMetric(_settings.CpuUsageMetric);
    }

    private void SendCandidates()
        => _ipc?.Broadcast(JsonSerializer.Serialize(
               new { type = "candidates", data = _engine.GetCandidates() }, JsonOpts));

    /// <summary>
    /// 已导入的自选图片列表。NexBox 的做法是：选完自己的图片，它就变成预设网格里的
    /// 一格，和内置预设并排，下次直接点。这里照做 —— 前端点「选择本地图片」时把图
    /// 交给后端落盘，落盘后再把整个目录列回去，前端重新渲染网格。
    /// </summary>
    private void SendCrosshairImages()
    {
        var list = new List<object>();
        try
        {
            string dir = AppPaths.CrosshairImageDir;
            if (Directory.Exists(dir))
            {
                foreach (string path in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg"
                        && ext != ".gif" && ext != ".bmp") continue;

                    list.Add(new
                    {
                        name = Path.GetFileName(path),
                        path,
                    });
                }
            }
        }
        catch { /* 目录读不了就当没有自选图 */ }

        _ipc?.Broadcast(JsonSerializer.Serialize(
            new { type = "crosshairImages", data = list }, JsonOpts));
    }

    /// <summary>显示器列表，供准星页选择把准星放在哪块屏上。</summary>
    private void SendDisplays()
    {
        var screens = Screen.AllScreens;
        var list = new List<object>(screens.Length);
        for (int i = 0; i < screens.Length; i++)
        {
            var sc = screens[i];
            list.Add(new
            {
                index = i,
                name = $"显示器 {i + 1}",
                primary = sc.Primary,
                width = sc.Bounds.Width,
                height = sc.Bounds.Height,
            });
        }
        _ipc?.Broadcast(JsonSerializer.Serialize(new { type = "displays", data = list }, JsonOpts));
    }

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
                    SendDisplays();
                    SendCrosshairImages();
                    if (_last != null)
                        _ipc?.Broadcast(JsonSerializer.Serialize(new { type = "snapshot", data = _last }, JsonOpts));
                    break;

                case "getDisplays":
                    SendDisplays();
                    break;

                case "getCrosshairImages":
                    SendCrosshairImages();
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

                case "deleteSession":
                    // data: { startedAt, endedAt, gameName, csvFile }
                    if (doc.RootElement.TryGetProperty("data", out var delEl))
                    {
                        var target = delEl.Deserialize<SessionRecord>(JsonOpts);
                        bool ok = target != null && (_recorder?.DeleteSession(target) ?? false);
                        _ipc?.Broadcast(JsonSerializer.Serialize(
                            new { type = "sessionDeleted", data = new { ok } }, JsonOpts));
                        if (ok) SendSessions();
                    }
                    break;

                case "clearSessions":
                    int cleared = _recorder?.ClearHistory() ?? 0;
                    _ipc?.Broadcast(JsonSerializer.Serialize(
                        new { type = "sessionsCleared", data = new { count = cleared } }, JsonOpts));
                    SendSessions();
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

                case "toggleCrosshair":
                    ToggleCrosshair();
                    break;

                case "setCrosshairImage":
                    // data: { name, dataUrl } —— 前端把预设图/自选图以 base64 dataURL 送进来
                    if (doc.RootElement.TryGetProperty("data", out var imgEl))
                    {
                        string? imgName = imgEl.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                        string? dataUrl = imgEl.TryGetProperty("dataUrl", out var dEl) ? dEl.GetString() : null;
                        SaveCrosshairImage(imgName, dataUrl);
                    }
                    break;

                case "deleteCrosshairImage":
                    // data: { name } —— 删除用户导入的图片；删的是当前在用图时回退程序绘制
                    if (doc.RootElement.TryGetProperty("data", out var delImgEl))
                    {
                        string? delName = delImgEl.TryGetProperty("name", out var dnEl) ? dnEl.GetString() : null;
                        DeleteCrosshairImage(delName);
                    }
                    break;

                case "setCrosshairEnabled":
                    // data: { value: true } —— 只要开关位，参数沿用当前设置
                    if (doc.RootElement.TryGetProperty("value", out var cxEl))
                    {
                        _settings.Crosshair ??= new CrosshairSettings();
                        _settings.Crosshair.Enabled = cxEl.ValueKind == JsonValueKind.True;
                        _settings.Save();
                        _crosshair?.Apply(_settings.Crosshair);
                        _hold?.Apply(_settings.Crosshair);
                        SendSettings();
                    }
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
        // 开机自启只在值真的变了时才动注册表。
        // 这里是高频路径（拖滑块每次都会进来），不能每次都去读注册表。
        if (settings.AutoStart != _autoStartKnown)
        {
            bool registryNow = AutoStartManager.IsEnabled();
            if (registryNow == settings.AutoStart)
            {
                // 注册表已经是目标状态（比如用户刚在托盘里改过），不用重写
                _autoStartKnown = registryNow;
            }
            else if (AutoStartManager.SetEnabled(settings.AutoStart))
            {
                _autoStartKnown = settings.AutoStart;
            }
            else
            {
                // 写失败（组策略限制等）：退回注册表实况，让界面开关弹回去
                settings.AutoStart = registryNow;
                _autoStartKnown = registryNow;
                _tray?.ShowBalloon("开机自启设置失败", "写入注册表启动项被拒绝。");
            }
            _tray?.SetAutoStartChecked(_autoStartKnown);
        }

        _settings = settings;
        _settings.Crosshair ??= new CrosshairSettings();   // 老配置或前端漏传时补齐
        _settings.Save();
        _overlay?.UpdateSettings(_settings);
        _crosshair?.Apply(_settings.Crosshair);
        _hold?.Apply(_settings.Crosshair);
        _hotkeys?.Apply(_settings);
        _tray?.SetOverlayChecked(_settings.OverlayEnabled);
        ApplyPriority();
        ApplyRecordingSettings();
        SendSettings();
        if (_last != null) OnSnapshot(_last);
    }

    /// <summary>
    /// 应用进程优先级。RealTime 不开放 —— 那会把鼠标键盘的输入线程都挤掉，
    /// 表现为系统卡顿。这里只接受 Normal / AboveNormal / High。
    /// </summary>
    /// <summary>
    /// 应用开机自启设置。托盘菜单和设置页两个入口都走这里，
    /// 以写注册表的实际结果为准回推两处状态 —— 失败时开关会被弹回去，
    /// 而不是留下一句「看起来开了、其实没写进去」。
    /// </summary>
    private void ApplyAutoStart(bool enabled)
    {
        // SetEnabled 返回的是「注册表是否已到达目标状态」，是个成功标志，
        // 不是新状态本身 —— 取消勾选时它同样返回 true（成功删掉了值）。
        // 所以真正的状态要分开算：成功就直接用请求值，失败则回读注册表实况。
        bool ok = AutoStartManager.SetEnabled(enabled);
        bool actual = ok ? enabled : AutoStartManager.IsEnabled();

        _autoStartKnown = actual;
        _settings.AutoStart = actual;
        // 无论成功失败都回推一次菜单勾选：勾上/取消都必须让托盘与实况一致，
        // 而不是只在失败时才弹回去。
        _tray?.SetAutoStartChecked(actual);
        _settings.Save();

        if (!ok) _tray?.ShowBalloon("开机自启设置失败", "写入注册表启动项被拒绝。");
        SendSettings();
    }

    private void ApplyPriority()
    {
        try
        {
            var cls = _settings.PriorityClass switch
            {
                "High" => ProcessPriorityClass.High,
                "AboveNormal" => ProcessPriorityClass.AboveNormal,
                _ => ProcessPriorityClass.Normal,
            };

            using var self = System.Diagnostics.Process.GetCurrentProcess();
            if (self.PriorityClass != cls) self.PriorityClass = cls;
        }
        catch
        {
            // 某些受保护环境下修改优先级会抛异常，忽略即可，不影响主功能
        }
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

    /// <summary>
    /// 保存前端送来的准星图片（内置预设图或用户自选图），落盘后写进设置并立即生效。
    /// 目录：%LOCALAPPDATA%\GameFPSMonitor\crosshair\ —— 图片必须落在本地磁盘上，
    /// 这样关掉界面、甚至把前端进程杀了，准星照样能画出来。
    /// </summary>
    private void SaveCrosshairImage(string? name, string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return;

        try
        {
            int comma = dataUrl.IndexOf(',');
            string b64 = comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl;
            byte[] bytes = Convert.FromBase64String(b64);
            if (bytes.Length == 0) return;

            // 与 settings.json / records 同一个数据根，避免两处目录分家
            string dir = AppPaths.CrosshairImageDir;
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, SafeFileName(name));
            File.WriteAllBytes(path, bytes);

            _settings.Crosshair ??= new CrosshairSettings();
            _settings.Crosshair.UseCustomImage = true;
            _settings.Crosshair.CustomImagePath = path;
            _settings.Crosshair.Enabled = true;
            _settings.Save();

            _crosshair?.Apply(_settings.Crosshair);
            _hold?.Apply(_settings.Crosshair);
            SendSettings();
            SendCrosshairImages();
        }
        catch
        {
            // 图片解不开就当没收到，前端还能重新选一次
        }
    }

    /// <summary>
    /// 删除一张用户导入的准星图。删的若是当前正在使用的图，
    /// 关掉 UseCustomImage 回退程序绘制，别让准星指向不存在的文件。
    /// </summary>
    private void DeleteCrosshairImage(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            string path = Path.Combine(AppPaths.CrosshairImageDir, SafeFileName(name));
            if (!File.Exists(path)) return;
            File.Delete(path);

            if (_settings.Crosshair?.CustomImagePath != null &&
                string.Equals(Path.GetFullPath(_settings.Crosshair.CustomImagePath),
                    Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                _settings.Crosshair.UseCustomImage = false;
                _settings.Crosshair.CustomImagePath = null;
                _settings.Save();
                _crosshair?.Apply(_settings.Crosshair);
                _hold?.Apply(_settings.Crosshair);
                SendSettings();
            }

            SendCrosshairImages();
        }
        catch
        {
            // 文件被占用等删除失败：回推一次列表，让界面与磁盘实际状态对齐
            SendCrosshairImages();
        }
    }

    /// <summary>只取文件名部分并替换非法字符，防止前端传来带路径的名字跳出目录。</summary>
    private static string SafeFileName(string? name)
    {
        string raw = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "crosshair.png" : name);
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);

        string cleaned = sb.ToString().Trim();
        return cleaned.Length == 0 ? "crosshair.png" : cleaned;
    }

    /// <summary>切换准星显示。被全局热键、IPC 与界面开关共用。</summary>
    private void ToggleCrosshair()
    {
        _settings.Crosshair ??= new CrosshairSettings();
        _settings.Crosshair.Enabled = !_settings.Crosshair.Enabled;
        _settings.Save();
        _crosshair?.Apply(_settings.Crosshair);
        _hold?.Apply(_settings.Crosshair);
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
        try { _hold?.Dispose(); } catch { }
        try { _crosshair?.Dispose(); } catch { }

        try
        {
            if (_electron is { HasExited: false }) _electron.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
