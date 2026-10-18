using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameFPS.Backend;

/// <summary>
/// 本机 WebSocket 服务：向 Electron 前端推送监测快照，并接收前端的设置变更。
/// 只监听 127.0.0.1，且进程本身已是管理员，因此无需配置 URL ACL。
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }

    /// <summary>收到前端文本消息时触发（原始 JSON 文本）。</summary>
    public event Action<string>? MessageReceived;

    public int ClientCount => _clients.Count;

    public bool Start(int preferredPort = 8731, int attempts = 20)
    {
        for (int p = preferredPort; p < preferredPort + attempts; p++)
        {
            if (TryStart(p)) { Port = p; return true; }
        }
        return false;
    }

    private bool TryStart(int port)
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            _listener = listener;
            _cts = new CancellationTokenSource();
            Port = port;
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return true;
        }
        catch
        {
            return false;   // 端口占用或权限不足，换下一个
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                var listener = _listener;
                if (listener == null) break;
                ctx = await listener.GetContextAsync();
            }
            catch { break; }

            if (ctx.Request.IsWebSocketRequest)
                _ = Task.Run(() => HandleClientAsync(ctx, ct), ct);
            else
            {
                try { ctx.Response.StatusCode = 400; ctx.Response.Close(); } catch { }
            }
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocket socket;
        try
        {
            var wsc = await ctx.AcceptWebSocketAsync(null);
            socket = wsc.WebSocket;
        }
        catch { return; }

        string id = Guid.NewGuid().ToString("N");
        _clients[id] = socket;

        var buffer = new byte[16384];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                    MessageReceived?.Invoke(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
        }
        catch { /* 客户端断开 */ }
        finally
        {
            _clients.TryRemove(id, out _);
            try { socket.Dispose(); } catch { }
        }
    }

    public void Broadcast(string json)
    {
        if (_clients.IsEmpty) return;

        var segment = new ArraySegment<byte>(Encoding.UTF8.GetBytes(json));
        foreach (var kv in _clients)
        {
            var socket = kv.Value;
            if (socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(kv.Key, out _);
                continue;
            }

            _ = socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None)
                      .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;

        foreach (var kv in _clients)
        {
            try { kv.Value.Dispose(); } catch { }
        }
        _clients.Clear();
        _cts?.Dispose();
        _cts = null;
    }
}
